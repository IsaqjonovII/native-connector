using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using IpcBench;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// IPC transport benchmark for milestone 3. Three candidates for host <-> supervisor <-> UI:
//   pipe  — Windows named pipe, length-prefixed JSON frames (System.IO.Pipes, in-box)
//   http  — Kestrel HTTP/1.1 on loopback, JSON body (ASP.NET Core, in-box with the SDK)
//   grpc  — Kestrel HTTP/2 (h2c) on loopback, unary call carrying the same JSON bytes
// Every server builds the same JSON "read result" per request, so differences are transport.
// Measures what the rewrite cares about: round-trip latency AND the RAM a transport adds to
// every host process (there is one host per 1C version).

static class TransportBench
{
    const string PipeName = "aiba-ipc-bench";
    const int HttpPort = 55971, GrpcPort = 55972;

    static int Main(string[] a)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string mode = a.Length > 0 ? a[0] : "bench";
        return mode switch
        {
            "server" => Server(a[1]),
            "bench" => Orchestrate(),
            _ => 1
        };
    }

    // ---------------- payload ----------------

    static byte[] Payload(int rows)
    {
        var list = new List<Dictionary<string, string>>(rows);
        for (int i = 0; i < rows; i++)
            list.Add(new() { ["Наименование"] = $"Номенклатура тестовая позиция {i:D5}", ["Код"] = $"{i:D11}" });
        return JsonSerializer.SerializeToUtf8Bytes(new { rows = list.Count, data = list });
    }

    // ---------------- servers ----------------

    static int Server(string transport)
    {
        // Idle baseline first, before the transport exists.
        Report("before-transport");
        switch (transport)
        {
            case "none": break;
            case "pipe": StartPipeServer(); break;
            case "http": StartKestrel(http2: false); break;
            case "grpc": StartKestrel(http2: true); break;
        }
        Thread.Sleep(500);
        Report("ready");
        Console.In.ReadLine();          // orchestrator closes stdin to stop us
        Report("stopping");
        return 0;
    }

    static void Report(string what)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var p = Process.GetCurrentProcess(); p.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            what, ws = p.WorkingSet64 / 1024 / 1024, priv = p.PrivateMemorySize64 / 1024 / 1024,
            threads = p.Threads.Count, handles = p.HandleCount
        }));
        Console.Out.Flush();
    }

    static void StartPipeServer()
    {
        for (int i = 0; i < 16; i++)
            new Thread(() =>
            {
                while (true)
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16,
                        PipeTransmissionMode.Byte, PipeOptions.None, 1 << 16, 1 << 16);
                    s.WaitForConnection();
                    var hdr = new byte[4];
                    try
                    {
                        while (ReadExact(s, hdr))
                        {
                            int rows = BitConverter.ToInt32(hdr);
                            var body = Payload(rows);
                            s.Write(BitConverter.GetBytes(body.Length));
                            s.Write(body);
                            s.Flush();
                        }
                    }
                    catch (IOException) { }
                }
            }) { IsBackground = true }.Start();
    }

    static bool ReadExact(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf, off, buf.Length - off);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    static void StartKestrel(bool http2)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, http2 ? GrpcPort : HttpPort,
            l => l.Protocols = http2 ? HttpProtocols.Http2 : HttpProtocols.Http1));
        if (http2) b.Services.AddGrpc();
        var app = b.Build();
        if (http2) app.MapGrpcService<BenchService>();
        else app.MapGet("/read", (int rows) => Results.Bytes(Payload(rows), "application/json"));
        app.StartAsync().GetAwaiter().GetResult();
    }

    sealed class BenchService : IpcBench.Bench.BenchBase
    {
        public override Task<Resp> Call(Req r, ServerCallContext _) =>
            Task.FromResult(new Resp { Json = ByteString.CopyFrom(Payload(r.Rows)) });
    }

    // ---------------- clients ----------------

    interface IClient : IDisposable { int Call(int rows); }

    sealed class PipeClient : IClient
    {
        private readonly NamedPipeClientStream _s = new(".", PipeName, PipeDirection.InOut);
        public PipeClient() => _s.Connect(5000);
        public int Call(int rows)
        {
            _s.Write(BitConverter.GetBytes(rows)); _s.Flush();
            var hdr = new byte[4]; ReadExact(_s, hdr);
            var body = new byte[BitConverter.ToInt32(hdr)]; ReadExact(_s, body);
            return body.Length;
        }
        public void Dispose() => _s.Dispose();
    }

    sealed class HttpJsonClient : IClient
    {
        private static readonly HttpClient Http = new() { BaseAddress = new Uri($"http://127.0.0.1:{HttpPort}") };
        public int Call(int rows) => Http.GetByteArrayAsync($"/read?rows={rows}").GetAwaiter().GetResult().Length;
        public void Dispose() { }
    }

    sealed class GrpcClient : IClient
    {
        private static readonly GrpcChannel Ch = GrpcChannel.ForAddress($"http://127.0.0.1:{GrpcPort}");
        private static readonly IpcBench.Bench.BenchClient C = new(Ch);
        public int Call(int rows) => C.Call(new Req { Rows = rows }).Json.Length;
        public void Dispose() { }
    }

    // ---------------- orchestration ----------------

    static int Orchestrate()
    {
        string exe = Environment.ProcessPath!;
        Console.WriteLine("transport  | server idle WS (before → ready) | payload | conc | calls | p50 µs | p99 µs | MB/s | server WS after");
        foreach (var t in new[] { "none", "pipe", "http", "grpc" })
        {
            var psi = new ProcessStartInfo(exe, $"server {t}")
            { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var before = JsonDocument.Parse(p.StandardOutput.ReadLine()!).RootElement;
            var ready = JsonDocument.Parse(p.StandardOutput.ReadLine()!).RootElement;
            string idle = $"{before.GetProperty("ws").GetInt64()} → {ready.GetProperty("ws").GetInt64()} MB " +
                          $"(priv {ready.GetProperty("priv").GetInt64()}, thr {ready.GetProperty("threads").GetInt32()})";

            if (t == "none") { Console.WriteLine($"{t,-10} | {idle}"); p.StandardInput.Close(); p.WaitForExit(); continue; }

            Func<IClient> make = t switch
            {
                "pipe" => () => new PipeClient(),
                "http" => () => new HttpJsonClient(),
                _ => () => new GrpcClient()
            };

            foreach (var (rows, conc, calls) in new[] { (1, 1, 2000), (50, 1, 2000), (5000, 1, 200), (50, 8, 4000) })
            {
                var (p50, p99, mbps) = Measure(make, rows, conc, calls);
                p.Refresh();
                Console.WriteLine($"{t,-10} | {idle,-31} | {rows,5} r | {conc,4} | {calls,5} | {p50,6:F0} | {p99,6:F0} | " +
                                  $"{mbps,5:F0} | {p.WorkingSet64 / 1024 / 1024} MB");
                idle = "";
            }
            p.StandardInput.Close();
            p.WaitForExit(5000);
        }
        return 0;
    }

    static (double p50, double p99, double mbps) Measure(Func<IClient> make, int rows, int conc, int calls)
    {
        // Warm up: connection setup and JIT are not what we are comparing.
        using (var w = make()) for (int i = 0; i < 50; i++) w.Call(rows);

        var lat = new List<double>[conc];
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        var threads = Enumerable.Range(0, conc).Select(c => new Thread(() =>
        {
            using var cl = make();
            var l = lat[c] = new List<double>(calls / conc);
            for (int i = 0; i < calls / conc; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                int n = cl.Call(rows);
                l.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
                Interlocked.Add(ref bytes, n);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        double secs = sw.Elapsed.TotalSeconds;

        var all = lat.SelectMany(x => x).OrderBy(x => x).ToList();
        double P(double q) => all[Math.Clamp((int)Math.Ceiling(q * all.Count) - 1, 0, all.Count - 1)];
        return (P(0.50), P(0.99), bytes / 1024.0 / 1024.0 / secs);
    }
}
