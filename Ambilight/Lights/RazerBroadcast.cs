using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ambilight.Lights
{
    /// <summary>
    /// Razer Chroma Connect (Chroma Broadcast) client, through Razer's own RzChromaBroadcastAPI64.dll.
    /// The DLL calls back on its own thread with five colors (CL1..CL5) about 20 times per second.
    /// </summary>
    class RazerBroadcast : IDisposable
    {
        // Yeelight's Chroma Broadcast application id, as listed in Razer's Chroma Broadcast SDK.
        static readonly Guid AppId = new Guid("22F3B1AE-241A-40B2-AEDD-10EA03E58F84");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn(Guid appId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UnInitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Callback(byte type, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RegisterFn(Callback callback);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UnregisterFn();

        // Packed: five RZCOLOR (0x00BBGGRR) then a BOOL.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct Effect { public uint CL1, CL2, CL3, CL4, CL5; public int IsAppSpecific; }

        readonly uint[] _colors = new uint[5];
        Callback _callback;           // must stay referenced: the DLL calls it from its own thread
        UnInitFn _uninit;
        UnregisterFn _unregister;
        bool _started;
        int _events;

        public volatile bool Live;
        public int Events { get { return _events; } }
        public bool IsStarted { get { return _started; } }
        string _lastLogged;
        public string LastError { get; private set; }

        bool Fail(string message)
        {
            LastError = message;
            if (message != _lastLogged) { _lastLogged = message; Log.Warn("Razer Chroma Broadcast: " + message); }
            return false;
        }

        /// <summary>Tries to attach to Synapse's Chroma Broadcast module; safe to call again after a failure.</summary>
        public bool TryStart()
        {
            if (_started) return true;
            try
            {
                string dir = FindDllDirectory();
                if (dir == null) return Fail("Razer Chroma Connect (Synapse) is not installed.");

                SetDllDirectory(dir);
                IntPtr dll = LoadLibrary(Path.Combine(dir, DllName));
                if (dll == IntPtr.Zero) return Fail("Cannot load the Razer Chroma Broadcast library (error " + Marshal.GetLastWin32Error() + ").");

                var init = Fn<InitFn>(dll, "Init");
                _uninit = Fn<UnInitFn>(dll, "UnInit");
                _unregister = Fn<UnregisterFn>(dll, "UnRegisterEventNotification");
                var register = Fn<RegisterFn>(dll, "RegisterEventNotification");

                int r = init(AppId);
                if (r != 0) return Fail("Chroma Broadcast refused to start (code " + r + ").");

                _callback = OnEvent;
                r = register(_callback);
                if (r != 0) { _uninit(); return Fail("Could not register for Chroma events (code " + r + ")."); }

                _started = true;
                LastError = null;
                Log.Info("Registered with Razer Chroma Broadcast.");
                return true;
            }
            catch (Exception e)
            {
                return Fail("start failed: " + e.Message);
            }
        }

        // The library must match the bitness of this process: Ambilight runs as a 32-bit process, the old Light Connect as 64-bit.
        static readonly bool Is64 = IntPtr.Size == 8;
        static string DllName { get { return Is64 ? "RzChromaBroadcastAPI64.dll" : "RzChromaBroadcastAPI.dll"; } }

        static string FindDllDirectory()
        {
            string x64 = Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files";
            string x86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
            string[] candidates =
            {
                Path.Combine(Is64 ? x64 : x86, @"Razer\ChromaBroadcast\bin"),
                @"C:\Program Files\Razer\ChromaBroadcast\bin",
                @"C:\Program Files (x86)\Razer\ChromaBroadcast\bin"
            };
            foreach (string dir in candidates)
                if (File.Exists(Path.Combine(dir, DllName))) return dir;
            return null;
        }

        static T Fn<T>(IntPtr module, string name) where T : class
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        int OnEvent(byte type, IntPtr data)
        {
            try
            {
                if (type == 1)          // BROADCAST_EFFECT
                {
                    var e = (Effect)Marshal.PtrToStructure(data, typeof(Effect));
                    lock (_colors) { _colors[0] = e.CL1; _colors[1] = e.CL2; _colors[2] = e.CL3; _colors[3] = e.CL4; _colors[4] = e.CL5; }
                    _events++;
                }
                else if (type == 2)     // BROADCAST_STATUS
                {
                    Live = data.ToInt64() == 1;
                    Log.Info("Chroma Broadcast status: " + (Live ? "LIVE" : "NOT LIVE"));
                }
            }
            catch { /* never let an exception cross back into native code */ }
            return 0;
        }

        /// <summary>Latest color of a zone (0..4) as 0xRRGGBB.</summary>
        public int GetRgb(int zone)
        {
            uint c;
            lock (_colors) c = _colors[zone];
            return (int)(((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF));
        }

        public void Dispose()
        {
            if (!_started) return;
            _started = false;
            try { _unregister(); } catch { }
            try { _uninit(); } catch { }
        }
    }
}
