// Step 2 of the Yeelight Chroma Connect rewrite (console prototype, no UI yet):
//   YeelightLive.exe discover            find the bulbs on every network adapter (SSDP), map them to the old config
//   YeelightLive.exe live [seconds]      drive the bulbs from the Razer Chroma Broadcast colors (music mode)
// Built with the .NET Framework compiler (C# 5): no string interpolation, no ?. operator.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class Bulb
{
    public string Id, Ip, Model, Name, Power, Bright, Rgb;
    public int Port = 55443;
    public IPAddress LocalIp;      // adapter the bulb answered on
    public int Group;              // 1..5 from the old light_cfg.ini (0 = not configured)
}

static class Discovery
{
    public static Dictionary<string, Bulb> Scan(int perAdapterMs)
    {
        var found = new Dictionary<string, Bulb>(StringComparer.OrdinalIgnoreCase);
        var threads = new List<Thread>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                IPAddress local = ua.Address; string nicName = nic.Name;
                var t = new Thread(delegate() { ScanOne(local, nicName, perAdapterMs, found); });
                t.IsBackground = true; t.Start(); threads.Add(t);
            }
        }
        foreach (var t in threads) t.Join();
        return found;
    }

    static void ScanOne(IPAddress local, string nicName, int ms, Dictionary<string, Bulb> found)
    {
        try
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Bind(new IPEndPoint(local, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                byte[] msg = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1982\r\nMAN: \"ssdp:discover\"\r\nST: wifi_bulb\r\n\r\n");
                var dest = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1982);
                var buf = new byte[4096];
                DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
                s.SendTo(msg, dest);
                DateTime resend = DateTime.UtcNow.AddMilliseconds(ms / 2);
                while (DateTime.UtcNow < end)
                {
                    if (DateTime.UtcNow > resend) { s.SendTo(msg, dest); resend = end.AddSeconds(1); }
                    if (!s.Poll(100000, SelectMode.SelectRead)) continue;
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    int n = s.ReceiveFrom(buf, ref ep);
                    Bulb bulb = Parse(Encoding.ASCII.GetString(buf, 0, n));
                    if (bulb == null) continue;
                    bulb.LocalIp = local;
                    lock (found) { if (!found.ContainsKey(bulb.Id)) found[bulb.Id] = bulb; }
                }
            }
        }
        catch (Exception e) { Console.WriteLine("  scan on {0} ({1}) failed: {2}", nicName, local, e.Message); }
    }

    static Bulb Parse(string text)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int i = line.IndexOf(':');
            if (i > 0) h[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
        }
        string id, loc;
        if (!h.TryGetValue("id", out id) || !h.TryGetValue("Location", out loc)) return null;
        var b = new Bulb();
        b.Id = id;
        Uri u; if (!Uri.TryCreate(loc.Replace("yeelight://", "http://"), UriKind.Absolute, out u)) return null;
        b.Ip = u.Host; b.Port = u.Port > 0 ? u.Port : 55443;
        h.TryGetValue("model", out b.Model); h.TryGetValue("name", out b.Name);
        h.TryGetValue("power", out b.Power); h.TryGetValue("bright", out b.Bright); h.TryGetValue("rgb", out b.Rgb);
        return b;
    }
}

static class OldConfig
{
    // light_cfg.ini of the official connector: [0x<did>] / did / bright / chroma_ctrl_flag / select_group
    public static Dictionary<string, int> Groups()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"YeelightChromaConnector\light_cfg.ini");
        if (!File.Exists(path)) return map;
        string did = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("did=")) did = line.Substring(4);
            else if (line.StartsWith("select_group=") && did != null) { map[did] = int.Parse(line.Substring(13)); }
        }
        return map;
    }
}

class MusicLink : IDisposable
{
    readonly Bulb _bulb; TcpClient _control; TcpClient _music; TcpListener _listener; int _msg = 1;
    public MusicLink(Bulb b) { _bulb = b; }

    int _port;
    public string LastReply = "";

    // A bulb may still hold the previous music session for a moment, so retry a few times.
    public bool Start()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (TryStart()) return true;
            }
            catch (Exception e) { LastReply = e.Message; }
            CloseAll();
            Thread.Sleep(1500);
        }
        return false;
    }

    bool TryStart()
    {
        _control = new TcpClient(); _control.Connect(_bulb.Ip, _bulb.Port);
        _listener = new TcpListener(_bulb.LocalIp, 0); _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // Clear any session the bulb still holds (e.g. from the official connector), then open ours.
        Send(_control, "set_music", "0,\"" + _bulb.LocalIp + "\"," + _port);
        Thread.Sleep(400);
        while (_control.Available > 0) { var junk = new byte[2048]; _control.GetStream().Read(junk, 0, junk.Length); }
        Send(_control, "set_music", "1,\"" + _bulb.LocalIp + "\"," + _port);
        DateTime end = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < end)
        {
            if (_control.Available > 0)
            {
                var buf = new byte[2048]; int n = _control.GetStream().Read(buf, 0, buf.Length);
                LastReply = Encoding.ASCII.GetString(buf, 0, n).Trim().Replace("\r\n", " | ");
            }
            if (_listener.Pending()) { _music = _listener.AcceptTcpClient(); _music.NoDelay = true; return true; }
            Thread.Sleep(20);
        }
        return false;
    }

    void Send(TcpClient c, string method, string paramsJson)
    {
        byte[] data = Encoding.ASCII.GetBytes("{\"id\":" + (_msg++) + ",\"method\":\"" + method + "\",\"params\":[" + paramsJson + "]}\r\n");
        c.GetStream().Write(data, 0, data.Length);
    }
    public void SetPower(bool on) { Send(_music, "set_power", "\"" + (on ? "on" : "off") + "\",\"sudden\",0"); }
    public void SetBright(int percent) { Send(_music, "set_bright", percent + ",\"sudden\",0"); }
    public void SetRgb(int rgb) { Send(_music, "set_rgb", rgb + ",\"sudden\",0"); }

    void CloseAll()
    {
        try { if (_music != null) _music.Close(); } catch { }
        try { if (_control != null) _control.Close(); } catch { }
        try { if (_listener != null) _listener.Stop(); } catch { }
        _music = null; _control = null; _listener = null;
    }

    public void Dispose()
    {
        // Leave music mode explicitly so the bulb accepts a new session right away.
        try { if (_control != null && _control.Connected) Send(_control, "set_music", "0,\"" + _bulb.LocalIp + "\"," + _port); } catch { }
        Thread.Sleep(100);
        CloseAll();
    }
}

// Razer Chroma Broadcast (Chroma Connect) via Razer's own DLL, same as ChromaProbe.
static class Razer
{
    static readonly Guid AppId = new Guid("22F3B1AE-241A-40B2-AEDD-10EA03E58F84");
    const string DllDir = @"C:\Program Files\Razer\ChromaBroadcast\bin";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string p);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string p);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn(Guid g);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UnInitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Callback(byte type, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RegisterFn(Callback cb);
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Effect { public uint CL1, CL2, CL3, CL4, CL5; public int IsAppSpecific; }

    static Callback _cb; static UnInitFn _uninit;
    public static readonly uint[] Colors = new uint[5];
    public static volatile bool Live;
    public static int Events;

    static T Fn<T>(IntPtr m, string name) where T : class
    {
        IntPtr p = GetProcAddress(m, name);
        if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    static int OnEvent(byte type, IntPtr data)
    {
        if (type == 1)
        {
            var e = (Effect)Marshal.PtrToStructure(data, typeof(Effect));
            lock (Colors) { Colors[0] = e.CL1; Colors[1] = e.CL2; Colors[2] = e.CL3; Colors[3] = e.CL4; Colors[4] = e.CL5; }
            Events++;
        }
        else if (type == 2) { Live = data.ToInt64() == 1; }
        return 0;
    }

    public static bool Start()
    {
        SetDllDirectory(DllDir);
        IntPtr dll = LoadLibrary(DllDir + @"\RzChromaBroadcastAPI64.dll");
        if (dll == IntPtr.Zero) { Console.WriteLine("Razer Chroma Broadcast DLL not found."); return false; }
        var init = Fn<InitFn>(dll, "Init"); _uninit = Fn<UnInitFn>(dll, "UnInit");
        var reg = Fn<RegisterFn>(dll, "RegisterEventNotification");
        int r = init(AppId); if (r != 0) { Console.WriteLine("Chroma Broadcast Init failed: " + r); return false; }
        _cb = OnEvent;                       // keep the delegate alive
        return reg(_cb) == 0;
    }
    public static void Stop() { if (_uninit != null) _uninit(); }
    public static uint Get(int zone) { lock (Colors) { return Colors[zone]; } }
}

static class Program
{
    static void Print(Dictionary<string, Bulb> found, Dictionary<string, int> groups)
    {
        Console.WriteLine("Old connector config: {0} bulb(s) with a group.", groups.Count);
        foreach (var b in found.Values)
        {
            int g; groups.TryGetValue(b.Id, out g); b.Group = g;
            Console.WriteLine("  {0}  {1,-15} model={2,-8} power={3,-3} bright={4,-3} group={5}  (via {6})", b.Id, b.Ip, b.Model, b.Power, b.Bright, g == 0 ? "-" : g.ToString(), b.LocalIp);
        }
        foreach (var kv in groups) if (!found.ContainsKey(kv.Key)) Console.WriteLine("  NOT FOUND: {0} (group {1})", kv.Key, kv.Value);
    }

    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "discover";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var groups = OldConfig.Groups();
        Console.WriteLine("Scanning every network adapter...");
        var found = Discovery.Scan(3000);
        Console.WriteLine("Found {0} bulb(s) in {1} ms:", found.Count, sw.ElapsedMilliseconds);
        Print(found, groups);
        if (cmd != "live") return 0;

        int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
        if (!Razer.Start()) return 3;
        var links = new List<KeyValuePair<Bulb, MusicLink>>();
        foreach (var b in found.Values)
        {
            if (b.Group < 1 || b.Group > 5) continue;
            var link = new MusicLink(b);
            try
            {
                if (link.Start()) { link.SetPower(true); link.SetBright(100); links.Add(new KeyValuePair<Bulb, MusicLink>(b, link)); Console.WriteLine("music mode OK on {0} (group {1})", b.Ip, b.Group); }
                else { Console.WriteLine("music mode refused by {0} (last reply: {1})", b.Ip, link.LastReply); link.Dispose(); }
            }
            catch (Exception e) { Console.WriteLine("cannot drive {0}: {1}", b.Ip, e.Message); link.Dispose(); }
        }
        if (links.Count == 0) return 4;

        Console.WriteLine("Driving {0} bulb(s) for {1} s...", links.Count, seconds);
        var last = new Dictionary<string, int>();
        long sent = 0; DateTime stop = DateTime.UtcNow.AddSeconds(seconds); DateTime nextLog = DateTime.UtcNow;
        while (DateTime.UtcNow < stop)
        {
            foreach (var kv in links)
            {
                uint c = Razer.Get(kv.Key.Group - 1);
                int rgb = (int)(((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF));
                int prev; if (last.TryGetValue(kv.Key.Id, out prev) && prev == rgb) continue;
                try { kv.Value.SetRgb(rgb); last[kv.Key.Id] = rgb; sent++; } catch (Exception e) { Console.WriteLine("send to {0} failed: {1}", kv.Key.Ip, e.Message); }
            }
            if (DateTime.UtcNow >= nextLog)
            {
                nextLog = DateTime.UtcNow.AddSeconds(2);
                Console.WriteLine("  events={0} sent={1} live={2}  CL1..5: {3:X6} {4:X6} {5:X6} {6:X6} {7:X6}", Razer.Events, sent, Razer.Live, Razer.Get(0), Razer.Get(1), Razer.Get(2), Razer.Get(3), Razer.Get(4));
            }
            Thread.Sleep(66);   // ~15 updates per second
        }
        Console.WriteLine("Razer events: {0}, color commands sent: {1}", Razer.Events, sent);
        foreach (var kv in links) kv.Value.Dispose();
        Razer.Stop();
        return 0;
    }
}
