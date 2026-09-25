using System.Buffers.Binary;

namespace OneC.Ipc;

/// <summary>
/// Length-prefixed frames: uint32 little-endian length, then that many bytes of UTF-8 JSON
/// (IPC_CONTRACT.md §1). Writes are serialised by the caller-supplied lock because one pipe
/// carries many concurrent requests.
/// </summary>
public static class Frames
{
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    public static async Task WriteAsync(Stream s, byte[] payload, SemaphoreSlim writeLock, CancellationToken ct = default)
    {
        if (payload.Length > MaxFrameBytes)
            throw new InvalidDataException($"frame of {payload.Length} bytes exceeds {MaxFrameBytes}");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);

        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await s.WriteAsync(header, ct).ConfigureAwait(false);
            await s.WriteAsync(payload, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    /// <summary>Next frame, or null at a clean end of stream.</summary>
    public static async Task<byte[]?> ReadAsync(Stream s, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(s, header, allowEof: true, ct).ConfigureAwait(false)) return null;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len > MaxFrameBytes)
            throw new InvalidDataException($"frame length {len} exceeds {MaxFrameBytes} — protocol error");
        var body = new byte[len];
        await ReadExactAsync(s, body, allowEof: false, ct).ConfigureAwait(false);
        return body;
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, bool allowEof, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (allowEof && off == 0) return false;
                throw new EndOfStreamException($"stream ended mid-frame ({off}/{buf.Length} bytes)");
            }
            off += n;
        }
        return true;
    }
}
