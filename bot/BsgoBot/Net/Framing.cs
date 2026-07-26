namespace BsgoBot.Net;

/// <summary>
/// BSGO wire framing: [uint16 length BIG-endian][byte protocolId][uint16 msgType][payload].
/// The length prefix counts bytes AFTER itself and is the only big-endian field.
/// </summary>
public static class Framing
{
    /// <summary>Reads exactly one frame. Returns the payload (protocolId onward), or null on EOF.</summary>
    public static async Task<byte[]?> ReadFrameAsync(this Stream s, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(s, header, ct)) return null;

        int len = (header[0] << 8) | header[1];
        if (len == 0) return Array.Empty<byte>();

        var payload = new byte[len];
        if (!await ReadExactAsync(s, payload, ct)) return null;
        return payload;
    }

    /// <summary>Writes a payload back out with its big-endian length prefix restored.</summary>
    public static async Task WriteFrameAsync(this Stream s, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[payload.Length + 2];
        frame[0] = (byte)((payload.Length >> 8) & 0xFF);
        frame[1] = (byte)(payload.Length & 0xFF);
        Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }
}
