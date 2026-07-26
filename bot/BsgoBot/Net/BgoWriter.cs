using System.Numerics;
using System.Text;

namespace BsgoBot.Net;

/// <summary>
/// Mirrors the client's BgoProtocolWriter (bsgo_Data/Managed/Assembly-CSharp.dll -> BgoProtocolWriter).
///
/// Frame layout on the wire:
///     [uint16 length BIG-endian][byte protocolId][uint16 msgType][payload...]
///
/// The 2-byte length prefix is the ONLY big-endian field. Everything after it uses
/// BinaryWriter's native little-endian encoding, exactly like the real client.
/// `length` counts the bytes AFTER the prefix.
/// </summary>
public sealed class BgoWriter : BinaryWriter
{
    private readonly MemoryStream _ms;

    public BgoWriter(byte protocolId, ushort msgType)
        : base(new MemoryStream(), Encoding.UTF8, leaveOpen: false)
    {
        _ms = (MemoryStream)BaseStream;
        Write((ushort)0);   // length placeholder, patched in ToFrame()
        Write(protocolId);
        Write(msgType);
    }

    /// <summary>UTF8 string prefixed with a little-endian uint16 byte count.</summary>
    public override void Write(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Write((ushort)bytes.Length);
        if (bytes.Length > 0) Write(bytes, 0, bytes.Length);
    }

    public void Write(Vector3 v)
    {
        Write(v.X);
        Write(v.Y);
        Write(v.Z);
    }

    /// <summary>Pitch/yaw/roll triple, as the client's Euler3.</summary>
    public void WriteEuler(float pitch, float yaw, float roll)
    {
        Write(pitch);
        Write(yaw);
        Write(roll);
    }

    /// <summary>uint32 list prefixed with a uint16 count.</summary>
    public void WriteIdList(IReadOnlyCollection<uint> ids)
    {
        Write((ushort)ids.Count);
        foreach (var id in ids) Write(id);
    }

    public int Length => (int)_ms.Length;

    /// <summary>Serialises to a ready-to-send frame with the big-endian length patched in.</summary>
    public byte[] ToFrame()
    {
        Flush();
        var data = _ms.ToArray();
        var payloadLen = (ushort)(data.Length - 2);
        data[0] = (byte)((payloadLen >> 8) & 0xFF);
        data[1] = (byte)(payloadLen & 0xFF);
        return data;
    }
}
