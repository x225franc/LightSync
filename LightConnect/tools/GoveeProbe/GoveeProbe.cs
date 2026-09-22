// Govee LAN prototype (console): discovery on every adapter, colorwc control, and the per-segment "razer" stream.
//   GoveeProbe.exe discover
//   GoveeProbe.exe colors [ip]        red / green / blue / warm white on the whole device, verified with devStatus
//   GoveeProbe.exe rate [ip] [hz]     stream colorwc at the given rate for 6 s and report the answers we get back
//   GoveeProbe.exe segments [ip] [n]  per-segment stream (razer command) with n segments (default 10)
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class Device { public string Mac, Sku, Ip; public IPAddress Local; }

static class Govee
{
    public static List<Device> Scan(int windowMs)
    {
        var found = new Dictionary<string, Device>();
        var threads = new List<Thread>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                IPAddress local = ua.Address; string name = nic.Name;
                var t = new Thread(delegate() { ScanAdapter(local, name, windowMs, found); }); t.IsBackground = true; t.Start(); threads.Add(t);
            }
        }
        foreach (var t in threads) t.Join();
        return new List<Device>(found.Values);
    }

    static void ScanAdapter(IPAddress local, string name, int windowMs, Dictionary<string, Device> found)
    {
        try
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(new IPEndPoint(local, 4002));          // devices answer to 4002
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                byte[] scan = Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}");
                var dest = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 4001);
                var buf = new byte[4096];
                DateTime end = DateTime.UtcNow.AddMilliseconds(windowMs), resend = DateTime.UtcNow.AddMilliseconds(windowMs / 2);
                s.SendTo(scan, dest);
                while (DateTime.UtcNow < end)
                {
                    if (DateTime.UtcNow >= resend) { s.SendTo(scan, dest); resend = end.AddSeconds(1); }
                    if (!s.Poll(100000, SelectMode.SelectRead)) continue;
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    int n = s.ReceiveFrom(buf, ref ep);
                    string text = Encoding.UTF8.GetString(buf, 0, n);
                    string mac = Field(text, "device"), sku = Field(text, "sku"), ip = Field(text, "ip");
                    if (mac == null || ip == null) continue;
                    lock (found) { if (!found.ContainsKey(mac)) found[mac] = new Device { Mac = mac, Sku = sku, Ip = ip, Local = local }; }
                }
            }
        }
        catch (Exception e) { Console.WriteLine("  scan on {0} ({1}) failed: {2}", name, local, e.Message); }
    }

    // Tiny JSON string-field reader, enough for the flat replies used here.
    public static string Field(string json, string key)
    {
        int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        i = json.IndexOf(':', i); if (i < 0) return null;
        i++; while (i < json.Length && json[i] == ' ') i++;
        if (i >= json.Length) return null;
        if (json[i] == '"') { int j = json.IndexOf('"', i + 1); return j < 0 ? null : json.Substring(i + 1, j - i - 1); }
        int k = i; while (k < json.Length && ",}".IndexOf(json[k]) < 0) k++;
        return json.Substring(i, k - i).Trim();
    }

    public static void Send(Socket s, string ip, string json) { s.SendTo(Encoding.UTF8.GetBytes(json), new IPEndPoint(IPAddress.Parse(ip), 4003)); }
    public static void Turn(Socket s, string ip, bool on) { Send(s, ip, "{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":" + (on ? 1 : 0) + "}}}"); }
    public static void Brightness(Socket s, string ip, int v) { Send(s, ip, "{\"msg\":{\"cmd\":\"brightness\",\"data\":{\"value\":" + v + "}}}"); }
    public static void Color(Socket s, string ip, int r, int g, int b) { Send(s, ip, "{\"msg\":{\"cmd\":\"colorwc\",\"data\":{\"color\":{\"r\":" + r + ",\"g\":" + g + ",\"b\":" + b + "},\"colorTemInKelvin\":0}}}"); }

    public static void Razer(Socket s, string ip, byte[] frame)
    {
        byte x = 0; foreach (byte b in frame) x ^= b;
        var full = new byte[frame.Length + 1]; Array.Copy(frame, full, frame.Length); full[frame.Length] = x;
        Send(s, ip, "{\"msg\":{\"cmd\":\"razer\",\"data\":{\"pt\":\"" + Convert.ToBase64String(full) + "\"}}}");
    }
    public static void RazerMode(Socket s, string ip, bool on) { Razer(s, ip, new byte[] { 0xBB, 0x00, 0x01, 0xB1, (byte)(on ? 1 : 0) }); }
    public static void Segments(Socket s, string ip, int[][] rgb)
    {
        int len = 2 + rgb.Length * 3;
        var f = new List<byte> { 0xBB, (byte)(len >> 8), (byte)len, 0xB0, 0x01, (byte)rgb.Length };
        foreach (var c in rgb) { f.Add((byte)c[0]); f.Add((byte)c[1]); f.Add((byte)c[2]); }
        Razer(s, ip, f.ToArray());
    }

    // devStatus goes to port 4003 and the answer comes back to our port 4002 (bound socket only).
    public static string Status(Socket listen, string ip, int waitMs)
    {
        Send(listen, ip, "{\"msg\":{\"cmd\":\"devStatus\",\"data\":{}}}");
        var buf = new byte[4096]; DateTime end = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (DateTime.UtcNow < end)
        {
            if (!listen.Poll(50000, SelectMode.SelectRead)) continue;
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            int n = listen.ReceiveFrom(buf, ref ep);
            string t = Encoding.UTF8.GetString(buf, 0, n);
            if (t.Contains("devStatus")) return t;
        }
        return null;
    }
}

static class Program
{
    static Socket Bound(IPAddress local)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.Bind(new IPEndPoint(local, 4002));
        return s;
    }
    static Socket Ephemeral(IPAddress local)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(local, 0));
        return s;
    }
    static string Short(string status)
    {
        if (status == null) return "(no answer)";
        int i = status.IndexOf("\"data\"", StringComparison.Ordinal);
        return i < 0 ? status : status.Substring(i + 7).TrimEnd('}', ' ');
    }

    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "discover";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var devices = Govee.Scan(3000);
        Console.WriteLine("Found {0} Govee device(s) in {1} ms:", devices.Count, sw.ElapsedMilliseconds);
        foreach (var d in devices) Console.WriteLine("  {0}  {1,-6} {2,-15} via {3}", d.Mac, d.Sku, d.Ip, d.Local);
        if (cmd == "discover" || devices.Count == 0) return 0;

        Device dev = devices[0];
        if (args.Length > 1) foreach (var d in devices) if (d.Ip == args[1]) dev = d;
        Console.WriteLine("\nUsing {0} ({1}) at {2}", dev.Sku, dev.Mac, dev.Ip);

        using (var listen = Bound(dev.Local))
        using (var eph = Ephemeral(dev.Local))          // sends come from a random port: is that enough for control?
        {
            Console.WriteLine("status before : " + Short(Govee.Status(listen, dev.Ip, 1500)));

            if (cmd == "colors")
            {
                Govee.Turn(eph, dev.Ip, true); Govee.Brightness(eph, dev.Ip, 100); Thread.Sleep(400);
                int[][] tests = { new[] { 255, 0, 0 }, new[] { 0, 255, 0 }, new[] { 0, 0, 255 }, new[] { 255, 180, 60 } };
                foreach (var c in tests)
                {
                    Govee.Color(eph, dev.Ip, c[0], c[1], c[2]); Thread.Sleep(1200);
                    Console.WriteLine("sent {0},{1},{2} from an ephemeral port -> status: {3}", c[0], c[1], c[2], Short(Govee.Status(listen, dev.Ip, 1500)));
                }
            }
            else if (cmd == "rate")
            {
                int hz = args.Length > 2 ? int.Parse(args[2]) : 15;
                Govee.Turn(eph, dev.Ip, true); Govee.Brightness(eph, dev.Ip, 100);
                int total = hz * 6, interval = 1000 / hz; var t0 = sw.ElapsedMilliseconds;
                for (int i = 0; i < total; i++)
                {
                    double h = i * 12.0 % 360; int r, g, b; Hsv(h, out r, out g, out b);
                    Govee.Color(eph, dev.Ip, r, g, b);
                    Thread.Sleep(interval);
                }
                Console.WriteLine("sent {0} colorwc in {1} ms ({2} Hz requested)", total, sw.ElapsedMilliseconds - t0, hz);
                Console.WriteLine("status after  : " + Short(Govee.Status(listen, dev.Ip, 1500)));
            }
            else if (cmd == "razerseq")
            {
                // Four variants of the Razer stream, each with its own color signature, 6 s each, separated by a warm-white pause.
                Action<string> announce = delegate(string m) { Console.WriteLine("[{0:HH:mm:ss}] {1}", DateTime.Now, m); };
                Action pause = delegate()
                {
                    Govee.RazerMode(eph, dev.Ip, false); Thread.Sleep(400);
                    Govee.Color(eph, dev.Ip, 255, 180, 60); Govee.Brightness(eph, dev.Ip, 100); Thread.Sleep(3500);
                };
                Func<int, int[]> hue = delegate(int h) { int r, g, b; Hsv(h % 360, out r, out g, out b); return new[] { r, g, b }; };

                announce("A: full OpenRGB flow (turn+brightness, razer on, continuous 30 Hz frames, same socket) -> RED <-> BLUE flashing");
                Govee.Turn(listen, dev.Ip, true); Govee.Brightness(listen, dev.Ip, 100); Thread.Sleep(400);
                Govee.RazerMode(listen, dev.Ip, true); Thread.Sleep(400);
                for (int i = 0; i < 180; i++) { var c = (i / 12) % 2 == 0 ? new[] { 255, 0, 0 } : new[] { 0, 0, 255 }; var seg = new int[10][]; for (int k = 0; k < 10; k++) seg[k] = c; Govee.Segments(listen, dev.Ip, seg); Thread.Sleep(33); }
                pause();

                announce("B: razer on only, ephemeral socket, 30 Hz -> GREEN <-> WHITE flashing");
                Govee.RazerMode(eph, dev.Ip, true); Thread.Sleep(400);
                for (int i = 0; i < 180; i++) { var c = (i / 12) % 2 == 0 ? new[] { 0, 255, 0 } : new[] { 255, 255, 255 }; var seg = new int[10][]; for (int k = 0; k < 10; k++) seg[k] = c; Govee.Segments(eph, dev.Ip, seg); Thread.Sleep(33); }
                pause();

                announce("C: rainbow gradient across the 10 segments, 30 Hz, same socket -> RAINBOW moving");
                Govee.RazerMode(listen, dev.Ip, true); Thread.Sleep(400);
                for (int i = 0; i < 180; i++) { var seg = new int[10][]; for (int k = 0; k < 10; k++) seg[k] = hue(k * 36 + i * 6); Govee.Segments(listen, dev.Ip, seg); Thread.Sleep(33); }
                pause();

                announce("D: baseline, plain colorwc at 10 Hz -> YELLOW <-> CYAN flashing");
                for (int i = 0; i < 60; i++) { if ((i / 4) % 2 == 0) Govee.Color(eph, dev.Ip, 255, 255, 0); else Govee.Color(eph, dev.Ip, 0, 255, 255); Thread.Sleep(100); }
                Govee.Color(eph, dev.Ip, 255, 180, 60);
                announce("done");
            }
            else if (cmd == "segments")
            {
                int n = args.Length > 2 ? int.Parse(args[2]) : 10;
                Govee.Turn(eph, dev.Ip, true); Govee.Brightness(eph, dev.Ip, 100);
                Govee.RazerMode(eph, dev.Ip, true); Thread.Sleep(500);
                Console.WriteLine("razer mode on; sending {0} segments (red -> blue gradient) for 6 s", n);
                for (int step = 0; step < 30; step++)
                {
                    var seg = new int[n][];
                    for (int i = 0; i < n; i++) { int r, g, b; Hsv(((i * 360.0 / n) + step * 12) % 360, out r, out g, out b); seg[i] = new[] { r, g, b }; }
                    Govee.Segments(eph, dev.Ip, seg); Thread.Sleep(200);
                }
                Govee.RazerMode(eph, dev.Ip, false);
                Console.WriteLine("razer mode off");
            }
        }
        return 0;
    }

    static void Hsv(double h, out int r, out int g, out int b)
    {
        double c = 1, x = c * (1 - Math.Abs((h / 60) % 2 - 1)); double rr = 0, gg = 0, bb = 0;
        if (h < 60) { rr = c; gg = x; } else if (h < 120) { rr = x; gg = c; } else if (h < 180) { gg = c; bb = x; }
        else if (h < 240) { gg = x; bb = c; } else if (h < 300) { rr = x; bb = c; } else { rr = c; bb = x; }
        r = (int)(rr * 255); g = (int)(gg * 255); b = (int)(bb * 255);
    }
}
