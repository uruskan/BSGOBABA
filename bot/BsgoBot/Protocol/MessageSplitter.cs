using BsgoBot.Net;

namespace BsgoBot.Protocol;

/// <summary>One decoded message inside a frame: its type and where its body sits.</summary>
public readonly record struct FrameMessage(ProtocolId Protocol, ushort MsgType, int BodyOffset, int BodyLength)
{
    public BgoReader Reader(byte[] payload) => new(payload, BodyOffset, BodyLength);
}

/// <summary>
/// A frame is not always one message.
///
/// The client's send path (BgoProtocol.NewMessage + GameProtocol.UpdateMessage) writes the
/// protocol id ONCE and then appends every queued message — msgType + body, back to back —
/// into a single frame. A fire click typically ships LockTarget, SubscribeInfo and
/// CastSlotAbility together. Anything that reads only the first msgType therefore sees
/// whichever message happened to be queued first and silently drops the rest.
///
/// The receive path is asymmetric: ProtocolManager.Update reads exactly one protocol id and
/// one msgType per frame, so server-&gt;client frames carry exactly one message. We only need
/// to walk the client's side.
///
/// Walking requires knowing where each body ends, which means knowing every request layout.
/// The table below is transcribed from GameProtocol's writer methods. An unknown opcode ends
/// the walk rather than guessing a length — a wrong guess would desynchronise everything
/// after it, which is worse than missing one message.
/// </summary>
public static class MessageSplitter
{
    /// <summary>
    /// Splits one frame into its messages. <paramref name="payload"/> starts at protocolId.
    /// Never throws: a malformed tail simply ends the walk.
    /// </summary>
    public static List<FrameMessage> Split(byte[] payload, bool fromClient)
    {
        var result = new List<FrameMessage>(1);
        if (payload.Length < 3) return result;

        var protocol = (ProtocolId)payload[0];

        // Server -> client: exactly one message per frame (ProtocolManager.Update).
        // Same for every client protocol whose layouts we haven't transcribed — treating
        // the rest of the frame as one body is what the old code did, and it is correct
        // whenever nothing was batched.
        if (!fromClient || protocol != ProtocolId.Game)
        {
            ushort type = (ushort)(payload[1] | (payload[2] << 8));
            result.Add(new FrameMessage(protocol, type, 3, payload.Length - 3));
            return result;
        }

        int pos = 1;
        while (pos + 2 <= payload.Length)
        {
            ushort msgType = (ushort)(payload[pos] | (payload[pos + 1] << 8));
            int bodyStart = pos + 2;

            int bodyLen;
            try
            {
                bodyLen = MeasureGameRequest((GameOp.Request)msgType, payload, bodyStart);
            }
            catch
            {
                bodyLen = -1;
            }

            if (bodyLen < 0 || bodyStart + bodyLen > payload.Length)
            {
                // Unknown or truncated. Hand back what's left as one opaque message so the
                // caller can still see the type, then stop — we can no longer find the next
                // boundary with confidence.
                result.Add(new FrameMessage(protocol, msgType, bodyStart, payload.Length - bodyStart));
                return result;
            }

            result.Add(new FrameMessage(protocol, msgType, bodyStart, bodyLen));
            pos = bodyStart + bodyLen;
        }

        return result;
    }

    /// <summary>
    /// Body length in bytes for one client-&gt;server Game request, or -1 if the opcode is
    /// not in the table. Every entry mirrors a writer in the client's GameProtocol.
    /// </summary>
    private static int MeasureGameRequest(GameOp.Request op, byte[] buf, int at) => op switch
    {
        // no body
        GameOp.Request.Quit
            or GameOp.Request.JumpIn
            or GameOp.Request.StopJump
            or GameOp.Request.StopGroupJump
            or GameOp.Request.CompleteJump
            or GameOp.Request.RequestUnanchor
            or GameOp.Request.RequestLaunchStrikes
            or GameOp.Request.CancelMiningRequest
            or GameOp.Request.CancelDocking => 0,

        // byte
        GameOp.Request.Wasd
            or GameOp.Request.Qweasd
            or GameOp.Request.AnsStartQueue
            or GameOp.Request.AnsJump
            or GameOp.Request.SetGear => 1,

        // uint16
        GameOp.Request.ToggleAbilityOff => 2,

        // uint32
        GameOp.Request.WhoIs
            or GameOp.Request.SubscribeInfo
            or GameOp.Request.UnSubscribeInfo
            or GameOp.Request.LockTarget
            or GameOp.Request.Mining
            or GameOp.Request.Loot
            or GameOp.Request.Jump
            or GameOp.Request.RequestAnchor
            or GameOp.Request.RequestJumpToBeacon => 4,

        // byte + float
        GameOp.Request.SetSpeed => 5,

        // uint32 + byte
        GameOp.Request.CargoInteraction => 5,

        // uint32 + uint32  /  uint32 + float
        GameOp.Request.Dock
            or GameOp.Request.SelectRespawnLocation
            or GameOp.Request.RequestJumpToTarget => 8,

        // Euler3
        GameOp.Request.MoveToDirection
            or GameOp.Request.MoveToDirectionWithoutRoll => 12,

        // Euler3 + float + float + float   /   Vector3 + Vector2 + float
        GameOp.Request.TurnToDirectionStrikes
            or GameOp.Request.TurnByPitchYawStrikes => 24,

        // uint16 abilityId + uint16 count + count * uint32
        GameOp.Request.CastSlotAbility
            or GameOp.Request.CastImmutableSlotAbility
            or GameOp.Request.ToggleAbilityOn
            or GameOp.Request.UpdateAbilityTargets => 2 + 2 + 4 * U16(buf, at + 2),

        // uint16 count + count * uint32
        GameOp.Request.MoveInfo => 2 + 4 * U16(buf, at),

        // uint32 sectorId + uint16 count + count * uint32
        GameOp.Request.GroupJump
            or GameOp.Request.GroupJumpToBeacon
            or GameOp.Request.GroupJumpToTarget => 4 + 2 + 4 * U16(buf, at + 4),

        // uint16 lootId + uint16 BYTE-count + that many bytes (client writes items*4, not a count)
        GameOp.Request.TakeLootItems => 2 + 2 + U16(buf, at + 2),

        _ => -1,
    };

    private static int U16(byte[] buf, int at)
    {
        if (at + 2 > buf.Length) throw new EndOfStreamException();
        return buf[at] | (buf[at + 1] << 8);
    }
}
