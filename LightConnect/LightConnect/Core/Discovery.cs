using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LightConnect.Core
{
    class DiscoveredBulb
    {
        public string Id, Ip, Model, Name;
        public int Port = 55443;
        public IPAddress LocalIp;   // our address on the adapter the bulb answered on
    }

    /// <summary>
    /// Yeelight LAN discovery (SSDP M-SEARCH). Unlike the official connector - which rotates through
    /// one adapter every 5 s - every usable adapter is searched at the same time, so a scan takes
    /// about as long as the reply window (~3 s) whatever the number of virtual adapters.
    /// </summary>
    static class Discovery
    {
        static readonly byte[] Search = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1982\r\nMAN: \"ssdp:discover\"\r\nST: wifi_bulb\r\n\r\n");

        public static List<DiscoveredBulb> Scan(int windowMs)
        {
            var found = new Dictionary<string, DiscoveredBulb>(StringComparer.OrdinalIgnoreCase);
            var threads = new List<Thread>();

            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception e) { Log.Warn("Cannot enumerate network adapters: " + e.Message); return new List<DiscoveredBulb>(); }

            foreach (var nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch { continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;   // link-local: nothing reachable there

                    IPAddress local = ua.Address;
                    string name = nic.Name;
                    var t = new Thread(() => ScanAdapter(local, name, windowMs, found)) { IsBackground = true };
                    t.Start();
                    threads.Add(t);
                }
            }

            foreach (var t in threads) t.Join();
            lock (found) return new List<DiscoveredBulb>(found.Values);
        }

        static void ScanAdapter(IPAddress local, string adapterName, int windowMs, Dictionary<string, DiscoveredBulb> found)
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Bind(new IPEndPoint(local, 0));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

                    var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1982);
                    var buffer = new byte[4096];
                    DateTime end = DateTime.UtcNow.AddMilliseconds(windowMs);
                    DateTime resend = DateTime.UtcNow.AddMilliseconds(windowMs / 2);
                    s.SendTo(Search, group);

                    while (DateTime.UtcNow < end)
                    {
                        if (DateTime.UtcNow >= resend) { s.SendTo(Search, group); resend = end.AddSeconds(1); }
                        if (!s.Poll(100000, SelectMode.SelectRead)) continue;

                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        int n = s.ReceiveFrom(buffer, ref remote);
                        var bulb = Parse(Encoding.ASCII.GetString(buffer, 0, n));
                        if (bulb == null) continue;
                        bulb.LocalIp = local;
                        lock (found) { if (!found.ContainsKey(bulb.Id)) found[bulb.Id] = bulb; }
                    }
                }
            }
            catch (Exception e) { Log.Warn("Discovery on " + adapterName + " (" + local + ") failed: " + e.Message); }
        }

        static DiscoveredBulb Parse(string text)
        {
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = line.IndexOf(':');
                if (i > 0) h[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }

            string id, location;
            if (!h.TryGetValue("id", out id) || !h.TryGetValue("Location", out location)) return null;

            Uri uri;
            if (!Uri.TryCreate(location.Replace("yeelight://", "http://"), UriKind.Absolute, out uri)) return null;

            var bulb = new DiscoveredBulb { Id = id, Ip = uri.Host, Port = uri.Port > 0 ? uri.Port : 55443 };
            h.TryGetValue("model", out bulb.Model);
            h.TryGetValue("name", out bulb.Name);
            return bulb;
        }
    }
}
