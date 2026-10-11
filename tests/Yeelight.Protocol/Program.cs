using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ambilight.Lights;
namespace Ambilight.Lights { static class Log { public static void Info(string s) { } public static void Warn(string s) { } } }
class Program
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Test(bool error, bool dispose)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            var request = JObject.Parse(await reader.ReadLineAsync());
            Assert((string)request["method"] == "set_music", "Unexpected release command");
            Assert(request["params"] is JArray args && args.Count == 1 && (int)args[0] == 0, "Release must use exactly [0]");
            await Task.Delay(150);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(new JObject { ["method"] = "props", ["params"] = new JObject { ["power"] = "off" } }.ToString(Newtonsoft.Json.Formatting.None) + "\r\n"));
            var response = new JObject { ["id"] = 1 };
            if (error) response["error"] = new JObject { ["code"] = -1 }; else response["result"] = new JArray("ok");
            byte[] bytes = Encoding.UTF8.GetBytes(response.ToString(Newtonsoft.Json.Formatting.None) + "\r\n");
            await stream.WriteAsync(bytes.AsMemory(0, 8)); await Task.Delay(20); await stream.WriteAsync(bytes.AsMemory(8));
            Assert(await reader.ReadLineAsync() == null, "Release connection was not closed");
        });
        try
        {
            if (dispose) { var link = new MusicLink("127.0.0.1", port, IPAddress.Loopback); link.Dispose(); link.Dispose(); }
            else Assert(MusicLink.StopMusicMode("127.0.0.1", port) == !error, "Release accepted an error or missed a fragmented ACK");
            await server.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { listener.Stop(); }
    }
    static async Task Main()
    {
        await Test(false, false); await Test(true, false); await Test(false, true);
        Console.WriteLine("PASS: real release [0], fragmented ACK, notification filtering, error replies, socket closure and idempotent disposal.");
    }
}
