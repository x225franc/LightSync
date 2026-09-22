// Step 1 of the Yeelight Chroma Connect rewrite: prove that a .NET app can register with
// Razer Synapse's Chroma Broadcast module and receive the colors, using Razer's own DLL.
// Usage: ChromaProbe.exe [seconds]   (default 20)
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class ChromaProbe
{
    // Yeelight's Chroma Broadcast application id (the same one the official connector uses).
    static readonly Guid AppId = new Guid("22F3B1AE-241A-40B2-AEDD-10EA03E58F84");
    const string DllDir = @"C:\Program Files\Razer\ChromaBroadcast\bin";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn(Guid appId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UnInitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NotificationCallback(byte type, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RegisterFn(NotificationCallback callback);

    // Packed: five RZCOLOR (0x00BBGGRR) then a BOOL.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BroadcastEffect { public uint CL1, CL2, CL3, CL4, CL5; public int IsAppSpecific; }

    // Must stay referenced for the whole run: the DLL calls it from its own thread.
    static NotificationCallback _callback;
    static int _events;
    static bool _all;
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    static T Fn<T>(IntPtr module, string name) where T : class
    {
        IntPtr p = GetProcAddress(module, name);
        if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    static string Rgb(uint c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF); }

    static int OnEvent(byte type, IntPtr data)
    {
        if (type == 1)
        {
            var e = (BroadcastEffect)Marshal.PtrToStructure(data, typeof(BroadcastEffect));
            _events++;
            if (_all || _events <= 15 || _events % 50 == 0)
                Console.WriteLine("  effect #{0}: {1} {2} {3} {4} {5}  appSpecific={6} t={7}", _events, Rgb(e.CL1), Rgb(e.CL2), Rgb(e.CL3), Rgb(e.CL4), Rgb(e.CL5), e.IsAppSpecific, _clock.ElapsedMilliseconds);
        }
        else if (type == 2)
        {
            Console.WriteLine("  status: {0}", data.ToInt64() == 1 ? "LIVE" : "NOT_LIVE");
        }
        return 0;
    }

    static int Main(string[] args)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 20;
        _all = args.Length > 1 && args[1] == "all";
        if (!Environment.Is64BitProcess) { Console.WriteLine("Must run as a 64-bit process."); return 2; }

        SetDllDirectory(DllDir);
        IntPtr dll = LoadLibrary(DllDir + @"\RzChromaBroadcastAPI64.dll");
        if (dll == IntPtr.Zero) { Console.WriteLine("Cannot load the Razer Chroma Broadcast DLL (error {0}).", Marshal.GetLastWin32Error()); return 3; }

        var init = Fn<InitFn>(dll, "Init");
        var uninit = Fn<UnInitFn>(dll, "UnInit");
        var register = Fn<RegisterFn>(dll, "RegisterEventNotification");

        int r = init(AppId);
        Console.WriteLine("Init = {0}", r);
        if (r != 0) return 4;

        _callback = OnEvent;
        Console.WriteLine("RegisterEventNotification = {0}", register(_callback));
        Console.WriteLine("Listening for {0} s (change lighting in Synapse / play a Chroma effect)...", seconds);
        Thread.Sleep(seconds * 1000);

        Console.WriteLine("events received: {0}", _events);
        uninit();
        return 0;
    }
}
