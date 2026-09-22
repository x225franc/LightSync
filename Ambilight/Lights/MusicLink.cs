using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ambilight.Lights
{
    /// <summary>
    /// A Yeelight "music mode" session: the bulb connects back to us and then accepts an unlimited
    /// number of commands without replying, which is what a live color stream needs.
    /// </summary>
    class MusicLink : IDisposable
    {
        readonly string _ip;
        readonly int _controlPort;
        readonly IPAddress _localIp;
        TcpClient _control, _music;
        TcpListener _listener;
        int _listenPort, _messageId = 1;

        public string LastReply { get; private set; }

        public MusicLink(string ip, int port, IPAddress localIp)
        {
            _ip = ip; _controlPort = port; _localIp = localIp; LastReply = "";
        }

        /// <summary>
        /// Opens the session. A bulb can still hold a previous session (for example the official connector's),
        /// so any old one is cleared first and the whole thing is retried a few times.
        /// </summary>
        public bool Open()
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try { if (TryOpen()) return true; }
                catch (Exception e) { LastReply = e.Message; }
                CloseSockets();
                Thread.Sleep(1200);
            }
            return false;
        }

        bool TryOpen()
        {
            _control = new TcpClient();
            var connect = _control.BeginConnect(_ip, _controlPort, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(3000)) throw new TimeoutException("bulb did not accept the TCP connection");
            _control.EndConnect(connect);

            _listener = new TcpListener(_localIp, 0);
            _listener.Start();
            _listenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            SendOn(_control, "set_music", "0,\"" + _localIp + "\"," + _listenPort);
            Thread.Sleep(400);
            Drain();
            SendOn(_control, "set_music", "1,\"" + _localIp + "\"," + _listenPort);

            DateTime end = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < end)
            {
                Drain();
                if (_listener.Pending())
                {
                    _music = _listener.AcceptTcpClient();
                    _music.NoDelay = true;
                    return true;
                }
                Thread.Sleep(20);
            }
            return false;
        }

        void Drain()
        {
            while (_control != null && _control.Available > 0)
            {
                var buf = new byte[2048];
                int n = _control.GetStream().Read(buf, 0, buf.Length);
                if (n <= 0) break;
                LastReply = Encoding.ASCII.GetString(buf, 0, n).Trim().Replace("\r\n", " | ");
            }
        }

        void SendOn(TcpClient c, string method, string paramsJson)
        {
            byte[] data = Encoding.ASCII.GetBytes("{\"id\":" + (_messageId++) + ",\"method\":\"" + method + "\",\"params\":[" + paramsJson + "]}\r\n");
            c.GetStream().Write(data, 0, data.Length);
        }

        /// <summary>False as soon as the bulb has closed the session (power cut, Wi-Fi drop, another client took over).</summary>
        public bool IsAlive
        {
            get
            {
                try
                {
                    if (_music == null || !_music.Connected) { CloseReason = "socket no longer connected"; return false; }
                    var s = _music.Client;
                    if (s.Poll(0, SelectMode.SelectRead) && s.Available == 0) { CloseReason = "closed by the bulb"; return false; }
                    return true;
                }
                catch (Exception e) { CloseReason = e.Message; return false; }
            }
        }

        public string CloseReason { get; private set; }

        /// <summary>What a bulb was showing, so a preview can put it back.</summary>
        public class Snapshot { public bool On; public int Brightness = 100, Rgb, ColorMode = 1, Ct = 4000; }

        /// <summary>Reads power/brightness/color through the control connection (null if the bulb does not answer).</summary>
        public Snapshot QueryState()
        {
            try
            {
                Drain();
                int id = _messageId;
                SendOn(_control, "get_prop", "\"power\",\"bright\",\"rgb\",\"color_mode\",\"ct\"");
                DateTime end = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < end)
                {
                    if (_control.Available > 0)
                    {
                        var buf = new byte[2048];
                        int n = _control.GetStream().Read(buf, 0, buf.Length);
                        foreach (string line in Encoding.ASCII.GetString(buf, 0, n).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var json = Newtonsoft.Json.Linq.JObject.Parse(line);
                            var result = json["result"] as Newtonsoft.Json.Linq.JArray;
                            if (result == null || (int?)json["id"] != id || result.Count < 5) continue;
                            int v;
                            var s = new Snapshot { On = (string)result[0] == "on" };
                            if (int.TryParse((string)result[1], out v)) s.Brightness = Math.Max(1, v);
                            if (int.TryParse((string)result[2], out v)) s.Rgb = v;
                            if (int.TryParse((string)result[3], out v)) s.ColorMode = v;
                            if (int.TryParse((string)result[4], out v)) s.Ct = v;
                            return s;
                        }
                    }
                    Thread.Sleep(20);
                }
            }
            catch (Exception e) { Log.Warn("Could not read the state of " + _ip + ": " + e.Message); }
            return null;
        }

        public bool SetCt(int kelvin) { return Send("set_ct_abx", Math.Max(1700, Math.Min(6500, kelvin)) + ",\"sudden\",0"); }

        public bool SetRgb(int rgb) { return Send("set_rgb", Math.Max(1, rgb) + ",\"sudden\",0"); }   // a bulb refuses 0 (pure black)

        /// <summary>Sets the color and the brightness together (a bulb keeps its own brightness when only a color is sent).</summary>
        public bool SetColorAndBrightness(int rgb, int percent)
        {
            return Send("set_scene", "\"color\"," + Math.Max(1, rgb) + "," + Math.Max(1, Math.Min(100, percent)));
        }
        public bool SetBrightness(int percent) { return Send("set_bright", Math.Max(1, Math.Min(100, percent)) + ",\"sudden\",0"); }
        public bool SetPower(bool on) { return Send("set_power", "\"" + (on ? "on" : "off") + "\",\"sudden\",0"); }

        /// <summary>Switches off with the bulb's own fade, which can go below the 1% brightness floor the color commands have.</summary>
        public bool SetPowerOffSmooth(int milliseconds)
        {
            return milliseconds <= 0 ? SetPower(false) : Send("set_power", "\"off\",\"smooth\"," + Math.Max(30, milliseconds));
        }

        bool Send(string method, string paramsJson)
        {
            try { SendOn(_music, method, paramsJson); return true; }
            catch (Exception e) { CloseReason = "send failed: " + e.Message; return false; }
        }

        void CloseSockets()
        {
            try { if (_music != null) _music.Close(); } catch { }
            try { if (_control != null) _control.Close(); } catch { }
            try { if (_listener != null) _listener.Stop(); } catch { }
            _music = null; _control = null; _listener = null;
        }

        public void Dispose()
        {
            // Leave music mode explicitly so the bulb is immediately available to the next client.
            try { if (_control != null && _control.Connected) SendOn(_control, "set_music", "0,\"" + _localIp + "\"," + _listenPort); } catch { }
            Thread.Sleep(100);
            CloseSockets();
        }
    }
}
