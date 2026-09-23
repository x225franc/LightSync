#nullable disable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Ambilight.Lights
{
    class GoveeSeen
    {
        public string Mac, Sku, Ip;
        public IPAddress Local;
    }

    /// <summary>What a Govee device reports for "devStatus".</summary>
    class GoveeStatus
    {
        public bool On;
        public int Brightness = 100, R, G, B, Kelvin;
    }

    /// <summary>
    /// Govee LAN protocol: discovery by multicast to 239.255.255.250:4001, replies and status answers to
    /// UDP 4002, commands to UDP 4003 on the device. Port 4002 can only have one owner per adapter, so a
    /// single socket per adapter address does everything (scan, commands, status), with a receive thread
    /// that routes each answer to whoever is waiting for it. Runs entirely on the LAN - no cloud.
    /// </summary>
    sealed class GoveeNet : IDisposable
    {
        sealed class Listener
        {
            public Socket Socket;
            public IPAddress Local;
            public Thread Thread;
            public volatile bool Stop;
        }

        static readonly byte[] ScanMessage = Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}");
        static readonly IPEndPoint ScanGroup = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 4001);

        readonly Action<GoveeSeen> _onSeen;
        readonly Dictionary<string, Listener> _listeners = new Dictionary<string, Listener>();
        readonly Dictionary<string, GoveeStatus> _status = new Dictionary<string, GoveeStatus>();
        readonly object _statusGate = new object();
        bool _disposed;

        public GoveeNet(Action<GoveeSeen> onSeen) { _onSeen = onSeen; }

        /// <summary>Makes sure there is a listening socket for every usable IPv4 address and drops the ones that vanished.</summary>
        public void Refresh()
        {
            if (_disposed) return;
            var wanted = new HashSet<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] b = ua.Address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;
                        wanted.Add(ua.Address.ToString());
                    }
                }
            }
            catch (Exception e) { Log.Warn("Govee: cannot enumerate network adapters: " + e.Message); return; }

            lock (_listeners)
            {
                foreach (string ip in new List<string>(_listeners.Keys))
                {
                    if (!wanted.Contains(ip) || _listeners[ip].Stop)
                    {
                        Close(_listeners[ip]);
                        _listeners.Remove(ip);
                    }
                }
                foreach (string ip in wanted)
                {
                    if (_listeners.ContainsKey(ip)) continue;
                    try { _listeners[ip] = Open(IPAddress.Parse(ip)); }
                    catch (Exception e) { Log.Warn("Govee: cannot listen on " + ip + ":4002 (" + e.Message + "). Is Govee Desktop running?"); }
                }
            }
        }

        Listener Open(IPAddress local)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(new IPEndPoint(local, 4002));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            }
            catch { s.Dispose(); throw; }

            var l = new Listener { Socket = s, Local = local };
            l.Thread = new Thread(() => ReceiveLoop(l)) { IsBackground = true, Name = "Govee listener " + local };
            l.Thread.Start();
            return l;
        }

        static void Close(Listener l)
        {
            l.Stop = true;
            try { l.Socket.Close(); } catch { }
        }

        void ReceiveLoop(Listener l)
        {
            var buf = new byte[4096];
            while (!l.Stop)
            {
                try
                {
                    if (!l.Socket.Poll(500000, SelectMode.SelectRead)) continue;
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = l.Socket.ReceiveFrom(buf, ref from);
                    Handle(Encoding.UTF8.GetString(buf, 0, n), ((IPEndPoint)from).Address.ToString(), l.Local);
                }
                catch (Exception)
                {
                    if (!l.Stop) { l.Stop = true; Log.Warn("Govee: the socket on " + l.Local + " stopped, it will be reopened."); }
                }
            }
        }

        void Handle(string text, string fromIp, IPAddress local)
        {
            try
            {
                var msg = JObject.Parse(text)["msg"] as JObject;
                if (msg == null) return;
                string cmd = (string)msg["cmd"];
                var data = msg["data"] as JObject;
                if (data == null) return;

                if (cmd == "scan")
                {
                    string mac = (string)data["device"], ip = (string)data["ip"];
                    if (mac == null || ip == null) return;
                    var seen = new GoveeSeen { Mac = mac, Sku = (string)data["sku"], Ip = ip, Local = local };
                    _onSeen(seen);
                }
                else if (cmd == "devStatus" || cmd == "status")
                {
                    var st = new GoveeStatus { On = ((int?)data["onOff"] ?? 0) == 1, Brightness = (int?)data["brightness"] ?? 100 };
                    var color = data["color"] as JObject;
                    if (color != null) { st.R = (int?)color["r"] ?? 0; st.G = (int?)color["g"] ?? 0; st.B = (int?)color["b"] ?? 0; }
                    st.Kelvin = (int?)data["colorTemInKelvin"] ?? 0;
                    lock (_statusGate) { _status[fromIp] = st; Monitor.PulseAll(_statusGate); }
                }
            }
            catch { /* not a Govee message */ }
        }

        /// <summary>Sends the discovery request on every adapter; answers arrive on the receive threads.</summary>
        public void Scan()
        {
            lock (_listeners)
                foreach (var l in _listeners.Values)
                {
                    if (l.Stop) continue;
                    try { l.Socket.SendTo(ScanMessage, ScanGroup); } catch (Exception e) { Log.Warn("Govee scan on " + l.Local + " failed: " + e.Message); }
                }
        }

        Listener For(IPAddress local)
        {
            lock (_listeners)
            {
                Listener l;
                if (local != null && _listeners.TryGetValue(local.ToString(), out l) && !l.Stop) return l;
                foreach (var any in _listeners.Values) if (!any.Stop) return any;
            }
            return null;
        }

        public bool Send(string ip, IPAddress local, string json)
        {
            var l = For(local);
            if (l == null) return false;
            try { l.Socket.SendTo(Encoding.UTF8.GetBytes(json), new IPEndPoint(IPAddress.Parse(ip), 4003)); return true; }
            catch { return false; }
        }

        public bool Turn(string ip, IPAddress local, bool on) { return Send(ip, local, "{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":" + (on ? 1 : 0) + "}}}"); }
        public bool Brightness(string ip, IPAddress local, int percent) { return Send(ip, local, "{\"msg\":{\"cmd\":\"brightness\",\"data\":{\"value\":" + Math.Max(1, Math.Min(100, percent)) + "}}}"); }
        public bool Color(string ip, IPAddress local, int rgb) { return Color(ip, local, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 0); }
        public bool Color(string ip, IPAddress local, int r, int g, int b, int kelvin)
        {
            return Send(ip, local, "{\"msg\":{\"cmd\":\"colorwc\",\"data\":{\"color\":{\"r\":" + r + ",\"g\":" + g + ",\"b\":" + b + "},\"colorTemInKelvin\":" + kelvin + "}}}");
        }

        // ---- "Razer" real-time stream: a base64 binary frame (0xBB header, command, payload, XOR checksum) ----

        bool RazerFrame(string ip, IPAddress local, byte[] frame)
        {
            byte x = 0;
            foreach (byte b in frame) x ^= b;
            var full = new byte[frame.Length + 1];
            Array.Copy(frame, full, frame.Length);
            full[frame.Length] = x;
            return Send(ip, local, "{\"msg\":{\"cmd\":\"razer\",\"data\":{\"pt\":\"" + Convert.ToBase64String(full) + "\"}}}");
        }

        public bool RazerMode(string ip, IPAddress local, bool on)
        {
            return RazerFrame(ip, local, new byte[] { 0xBB, 0x00, 0x01, 0xB1, (byte)(on ? 1 : 0) });
        }

        /// <summary>Streams one color to every segment of the device.</summary>
        public bool Segments(string ip, IPAddress local, int rgb, int count)
        {
            int len = 2 + count * 3;
            var f = new byte[6 + count * 3];
            f[0] = 0xBB; f[1] = (byte)(len >> 8); f[2] = (byte)len; f[3] = 0xB0; f[4] = 0x01; f[5] = (byte)count;
            for (int i = 0; i < count; i++)
            {
                f[6 + i * 3] = (byte)(rgb >> 16); f[7 + i * 3] = (byte)(rgb >> 8); f[8 + i * 3] = (byte)rgb;
            }
            return RazerFrame(ip, local, f);
        }

        /// <summary>Streams a different color to each segment (rgb[i] is segment i, 0xRRGGBB).</summary>
        public bool SegmentColors(string ip, IPAddress local, int[] rgb)
        {
            int count = Math.Min(rgb.Length, 255);
            int len = 2 + count * 3;
            var f = new byte[6 + count * 3];
            f[0] = 0xBB; f[1] = (byte)(len >> 8); f[2] = (byte)len; f[3] = 0xB0; f[4] = 0x01; f[5] = (byte)count;
            for (int i = 0; i < count; i++)
            {
                f[6 + i * 3] = (byte)(rgb[i] >> 16); f[7 + i * 3] = (byte)(rgb[i] >> 8); f[8 + i * 3] = (byte)rgb[i];
            }
            return RazerFrame(ip, local, f);
        }

        /// <summary>Asks a device for its current state (null if it does not answer in time).</summary>
        public GoveeStatus QueryStatus(string ip, IPAddress local, int timeoutMs)
        {
            lock (_statusGate) _status.Remove(ip);
            if (!Send(ip, local, "{\"msg\":{\"cmd\":\"devStatus\",\"data\":{}}}")) return null;

            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (_statusGate)
            {
                while (!_status.ContainsKey(ip))
                {
                    double left = (end - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return null;
                    Monitor.Wait(_statusGate, (int)left);
                }
                return _status[ip];
            }
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_listeners)
            {
                foreach (var l in _listeners.Values) Close(l);
                _listeners.Clear();
            }
        }
    }
}
