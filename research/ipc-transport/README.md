# IPC transport benchmark (milestone 3 input)

`dotnet run -c Release -- bench` — spawns one server process per transport and drives it from
the orchestrator. Every server builds the same JSON "read result" per request
(`{"rows":N,"data":[{"Наименование":…,"Код":…}]}`), so the differences are transport, not
serializer. Loopback only, Windows 11, .NET 9, 2026-09-23.

| transport | what |
|---|---|
| pipe | Windows named pipe, persistent connection, int32 length-prefixed JSON frames (`System.IO.Pipes`, in-box) |
| http | Kestrel HTTP/1.1 on 127.0.0.1, JSON body, `HttpClient` (ASP.NET Core, in-box with the SDK) |
| grpc | Kestrel HTTP/2 cleartext on 127.0.0.1, unary call carrying the same JSON bytes (`Grpc.AspNetCore` 2.66) |

## Cost added to the server process

| transport | idle WS before → listening | private | threads | WS after the load below |
|---|---|---|---|---|
| none | 24 → 27 MB | 7 MB | 8 | — |
| pipe | 25 → **30 MB** | 9 MB | 24 | 65–68 MB |
| http | 24 → **40 MB** | 11 MB | 14 | 132–135 MB |
| grpc | 24 → **41 MB** | 11 MB | 14 | 161–165 MB |

## Round trip

| transport | payload | concurrency | p50 | p99 | throughput |
|---|---|---|---|---|---|
| pipe | 1 row | 1 | **32 µs** | 60 µs | — |
| pipe | 50 rows | 1 | 134 µs | 326 µs | 92 MB/s |
| pipe | 5 000 rows | 1 | 4.7 ms | 10.0 ms | 251 MB/s |
| pipe | 50 rows | 8 | 98 µs | 629 µs | 860 MB/s |
| http | 1 row | 1 | 213 µs | 689 µs | — |
| http | 50 rows | 1 | 330 µs | 780 µs | 38 MB/s |
| http | 5 000 rows | 1 | 8.4 ms | 24.9 ms | 134 MB/s |
| http | 50 rows | 8 | 232 µs | 1.3 ms | 350 MB/s |
| grpc | 1 row | 1 | 314 µs | 769 µs | — |
| grpc | 50 rows | 1 | 447 µs | 1.1 ms | 28 MB/s |
| grpc | 5 000 rows | 1 | 7.5 ms | 31.3 ms | 138 MB/s |
| grpc | 50 rows | 8 | 303 µs | 1.2 ms | 290 MB/s |

## Reading

- Latency does not decide it. A warm 1C read is ~1 ms p50 and `Connect` is 1–2 s; every
  transport's overhead is a fraction of that.
- RAM does, per process. There is one host per 1C version. Kestrel costs ~16 MB idle and
  grows to 130–165 MB under a large-payload load, against ~5 MB idle / ~65 MB loaded for a
  pipe.
- What pipes cannot do: be reached from another machine, or be poked with curl / a browser.
  HTTP and gRPC can.
