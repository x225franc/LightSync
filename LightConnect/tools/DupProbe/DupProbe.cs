// Can the desktop still be captured (DXGI Desktop Duplication, what Ambilight uses) while the screensaver runs?
//   DupProbe.exe plain|input [seconds]
//     plain : the thread stays on the desktop it started on
//     input : before each capture attempt the thread is attached to the current INPUT desktop
//             (that is the one the screensaver runs on)
// Prints once per second: screensaver flag, frames captured in that second, last error.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Direct3D;

static class DupProbe
{
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    static OutputDuplication Create(Factory1 factory, out SharpDX.Direct3D11.Device device)
    {
        var adapter = factory.GetAdapter1(0);
        device = new SharpDX.Direct3D11.Device(adapter);
        _dev = device;
        var output = adapter.GetOutput(0).QueryInterface<Output1>();
        return output.DuplicateOutput(device);
    }

    static SharpDX.Direct3D11.Device _dev;
    static Texture2D _staging;

    // Samples the captured frame: returns "avg=(r,g,b) black=NN%" (share of near-black pixels).
    static string Describe(SharpDX.DXGI.Resource res)
    {
        using (var tex = res.QueryInterface<Texture2D>())
        {
            var d = tex.Description;
            if (_staging == null || _staging.Description.Width != d.Width || _staging.Description.Height != d.Height)
            {
                if (_staging != null) _staging.Dispose();
                d.CpuAccessFlags = CpuAccessFlags.Read; d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.OptionFlags = ResourceOptionFlags.None;
                _staging = new Texture2D(_dev, d);
            }
            _dev.ImmediateContext.CopyResource(tex, _staging);
            DataStream stream;
            var box = _dev.ImmediateContext.MapSubresource(_staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None, out stream);
            long r = 0, g = 0, b = 0, n = 0, black = 0;
            int w = _staging.Description.Width, h = _staging.Description.Height;
            for (int y = 0; y < h; y += 16)
            {
                stream.Position = (long)y * box.RowPitch;
                for (int x = 0; x < w; x += 16)
                {
                    stream.Position = (long)y * box.RowPitch + x * 4;
                    int px = stream.Read<int>();
                    int bl = px & 0xFF, gr = (px >> 8) & 0xFF, rd = (px >> 16) & 0xFF;
                    r += rd; g += gr; b += bl; n++;
                    if (rd + gr + bl < 30) black++;
                }
            }
            _dev.ImmediateContext.UnmapSubresource(_staging, 0);
            stream.Dispose();
            return string.Format("avg=({0},{1},{2}) black={3}%", r / n, g / n, b / n, black * 100 / n);
        }
    }

    static int Main(string[] args)
    {
        bool attach = args.Length > 0 && args[0] == "input";
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 20;
        Console.WriteLine("mode: {0}, {1} s", attach ? "attach to the input desktop" : "plain", seconds);

        var factory = new Factory1();
        SharpDX.Direct3D11.Device device;
        OutputDuplication dup = null;
        string lastError = "-";
        string content = "";
        string createError = "";
        int frames = 0, timeouts = 0, recreated = 0;
        var clock = Stopwatch.StartNew();
        long nextReport = 1000;

        while (clock.ElapsedMilliseconds < seconds * 1000)
        {
            if (attach)
            {
                IntPtr desk = OpenInputDesktop(0, false, 0x10000000);   // GENERIC_ALL
                if (desk == IntPtr.Zero) lastError = "OpenInputDesktop failed (" + Marshal.GetLastWin32Error() + ")";
                else if (!SetThreadDesktop(desk)) lastError = "SetThreadDesktop failed (" + Marshal.GetLastWin32Error() + ")";
            }

            try
            {
                if (dup == null) { dup = Create(factory, out device); recreated++; createError = ""; }
                SharpDX.DXGI.Resource res; OutputDuplicateFrameInformation info;
                var r = dup.TryAcquireNextFrame(100, out info, out res);
                if (r.Success)
                {
                    frames++;
                    if (clock.ElapsedMilliseconds >= nextReport - 60) { try { content = Describe(res); } catch (Exception) { content = "(read failed)"; } }
                    res.Dispose(); dup.ReleaseFrame();
                }
                else if (r == SharpDX.DXGI.ResultCode.WaitTimeout) timeouts++;
                else { lastError = "acquire: 0x" + r.Code.ToString("X8"); dup.Dispose(); dup = null; }
            }
            catch (SharpDXException e)
            {
                createError = "create: 0x" + e.ResultCode.Code.ToString("X8");
                if (dup != null) { dup.Dispose(); dup = null; }
                Thread.Sleep(100);
            }

            if (clock.ElapsedMilliseconds >= nextReport)
            {
                bool ss = false; SystemParametersInfo(0x72, 0, ref ss, 0);
                Console.WriteLine("[{0,2} s] screensaver={1,-5} frames={2,3} {3}  {4}",
                    nextReport / 1000, ss, frames, content, createError);
                frames = 0; timeouts = 0; nextReport += 1000;
            }
        }
        return 0;
    }
}
