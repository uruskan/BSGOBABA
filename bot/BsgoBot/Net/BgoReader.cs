using System.Numerics;
using System.Text;

namespace BsgoBot.Net;

/// <summary>
/// Mirrors the client's BgoProtocolReader. Operates on a single frame's payload,
/// i.e. the bytes AFTER the 2-byte big-endian length prefix, starting at protocolId.
/// All multi-byte numbers are little-endian (BinaryReader default), matching the client.
/// </summary>
public sealed class BgoReader : BinaryReader
{
    public BgoReader(byte[] payload) : base(new MemoryStream(payload, writable: false), Encoding.UTF8) { }

    public BgoReader(byte[] payload, int offset, int count)
        : base(new MemoryStream(payload, offset, count, writable: false), Encoding.UTF8) { }

    /// <summary>Lengths and collection counts are uint16 on the wire.</summary>
    public int ReadLength() => ReadUInt16();

    public override string ReadString()
    {
        int len = ReadLength();
        if (len <= 0) return string.Empty;
        return Encoding.UTF8.GetString(ReadBytes(len));
    }

    public uint ReadGuid() => ReadUInt32();

    public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());

    public Vector2 ReadVector2() => new(ReadSingle(), ReadSingle());

    /// <summary>Client's Euler3: pitch, yaw, roll — three floats, same width as a Vector3.</summary>
    public Vector3 ReadEuler() => new(ReadSingle(), ReadSingle(), ReadSingle());

    public Vector4 ReadQuaternion() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

    /// <summary>Tick is a bare int32 (Tick.Read).</summary>
    public int ReadTick() => ReadInt32();

    /// <summary>Colour is 4 raw bytes (BgoProtocolReader.ReadColor).</summary>
    public void SkipColor() => Skip(4);

    public List<uint> ReadUInt32List()
    {
        int n = ReadLength();
        var list = new List<uint>(n);
        for (int i = 0; i < n; i++) list.Add(ReadUInt32());
        return list;
    }

    public List<ushort> ReadUInt16List()
    {
        int n = ReadLength();
        var list = new List<ushort>(n);
        for (int i = 0; i < n; i++) list.Add(ReadUInt16());
        return list;
    }

    public DateTime ReadDateTime() =>
        DateTime.UnixEpoch.AddSeconds(ReadUInt32());

    /// <summary>Advances without allocating. Throws past the end, like every other read.</summary>
    public void Skip(int count)
    {
        if (count < 0 || count > Remaining) throw new EndOfStreamException();
        BaseStream.Position += count;
    }

    public long Position
    {
        get => BaseStream.Position;
        set => BaseStream.Position = value;
    }

    public long Remaining => BaseStream.Length - BaseStream.Position;

    public bool HasMore => Remaining > 0;

    /// <summary>Hex dump of the unread tail — used when a message layout is not yet known.</summary>
    public string DumpRemaining()
    {
        if (!HasMore) return "<empty>";
        var rest = ReadBytes((int)Remaining);
        return Convert.ToHexString(rest);
    }

    /// <summary>Reads the big-endian uint16 frame length prefix.</summary>
    public static ushort ReadFrameLength(byte[] twoBytes) =>
        (ushort)((twoBytes[0] << 8) | twoBytes[1]);
}
