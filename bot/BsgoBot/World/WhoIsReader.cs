using System.Numerics;
using BsgoBot.Net;
using BsgoBot.Protocol;

namespace BsgoBot.World;

/// <summary>What a Reply.WhoIs payload told us about one object.</summary>
public readonly record struct WhoIsInfo(
    uint Id,
    SpaceEntityType Type,
    CreatingCause Cause,
    uint OwnerGuid,
    uint ObjectGuid,
    Vector3? Position,
    uint? PlayerId,
    uint? OwnerObjectId,
    CargoInteraction? CargoAction,
    float Radius);

/// <summary>
/// Decodes the per-type body of Reply.WhoIs.
///
/// The client dispatches this through SectorFactory.CreateSpaceObject, which picks a
/// SpaceObject subclass from the id's type bits and calls its Read override. Each override
/// begins with SpaceObject.BaseRead — byte CreatingCause, uint32 ownerGUID, uint32 objectGUID —
/// and then reads its own fields. Reproducing that switch is the only way to get static
/// objects' positions: asteroids, debris, cargo and planets carry their position HERE and
/// nowhere else, because they never send a movement update.
///
/// Ships deliberately carry no position: theirs arrives via Reply.Move / Reply.SyncMove.
/// </summary>
public static class WhoIsReader
{
    /// <summary>
    /// Reads one WhoIs body. <paramref name="r"/> must sit immediately after the object id.
    /// Returns null if the layout for this type isn't known, so a half-parsed object is
    /// never fed into the world model.
    /// </summary>
    public static WhoIsInfo? Read(uint id, BgoReader r)
    {
        var type = EntityTypes.Of(id);

        // SpaceObject.BaseRead — common prefix for every type.
        var cause = (CreatingCause)r.ReadByte();
        uint ownerGuid = r.ReadUInt32();
        uint objectGuid = r.ReadUInt32();

        Vector3? pos = null;
        uint? playerId = null;
        uint? ownerObj = null;
        CargoInteraction? cargo = null;
        float radius = 0f;

        switch (type)
        {
            // Asteroid.Read / Planetoid.Read
            case SpaceEntityType.Asteroid:
            case SpaceEntityType.Planetoid:
                pos = r.ReadVector3();
                radius = r.ReadSingle();
                r.ReadSingle();                       // rotationSpeed
                break;

            // PlayerShip.Read : Ship.Read + playerId + roles + visible
            case SpaceEntityType.Player:
                ReadShipBindings(r);
                ReadShipAspects(r);
                playerId = r.ReadUInt32();
                r.ReadUInt32();                       // BgoAdminRoles
                r.ReadBoolean();                      // initial visibility
                break;

            // AsteroidShip.Read : Ship.Read + position
            case SpaceEntityType.AsteroidBot:
                ReadShipBindings(r);
                ReadShipAspects(r);
                pos = r.ReadVector3();
                break;

            // Ship.Read, unchanged by the subclass (FighterShip, CruiserShip,
            // WeaponPlatform, MiningShip, OutpostShip, JumpBeacon)
            case SpaceEntityType.BotFighter:
            case SpaceEntityType.Cruiser:
            case SpaceEntityType.WeaponPlatform:
            case SpaceEntityType.MiningShip:
            case SpaceEntityType.Outpost:
            case SpaceEntityType.JumpBeacon:
                ReadShipBindings(r);
                ReadShipAspects(r);
                break;

            // Missile.Read
            case SpaceEntityType.Missile:
                ownerObj = r.ReadUInt32();            // launcher
                r.ReadUInt32();                       // target
                r.ReadByte();                         // tier
                r.ReadUInt16();                       // objectPointHash
                radius = r.ReadSingle();              // effect radius
                break;

            // Mine.Read
            case SpaceEntityType.Mine:
            case SpaceEntityType.MineField:
            case SpaceEntityType.SmartMine:
                ownerObj = r.ReadUInt32();
                r.ReadByte();                         // tier
                r.ReadUInt32();                       // timeWhenArmed
                break;

            // DebrisPile.Read
            case SpaceEntityType.Debris:
                pos = r.ReadVector3();
                r.ReadQuaternion();
                r.ReadVector3();                      // scale
                r.ReadSingle();                       // rotationSpeed
                break;

            // CargoObject.Read
            case SpaceEntityType.CargoObject:
                pos = r.ReadVector3();
                r.ReadQuaternion();
                radius = r.ReadSingle();              // interaction range
                cargo = (CargoInteraction)r.ReadByte();
                break;

            // BsgoTrigger.Read
            case SpaceEntityType.Trigger:
                r.ReadString();                       // name
                pos = r.ReadVector3();
                radius = r.ReadSingle();
                break;

            // Planet.Read
            case SpaceEntityType.Planet:
                pos = r.ReadVector3();
                r.ReadQuaternion();
                radius = r.ReadSingle();              // scale
                r.SkipColor();
                r.SkipColor();
                r.ReadSingle();                       // shininess
                break;

            // EventVolume.Read
            case SpaceEntityType.Volume:
                pos = r.ReadVector3();
                radius = r.ReadSingle();
                r.ReadBoolean();                      // inverted
                r.ReadByte();                         // notification type
                break;

            // SectorEvent.Read
            case SpaceEntityType.SectorEvent:
                r.ReadUInt32();                       // sector event card guid
                break;

            // JumpTargetTransponder.Read
            case SpaceEntityType.JumpTargetTransponder:
                ownerObj = r.ReadUInt32();
                r.ReadUInt32();                       // timeWhenActive
                r.ReadUInt32();                       // timeWhenInactive
                break;

            // CaptureTrigger.Read
            case SpaceEntityType.CaptureTrigger:
                ownerObj = r.ReadUInt32();            // parent
                radius = r.ReadSingle();
                break;

            // Comet.Read adds nothing to BaseRead.
            case SpaceEntityType.Comet:
                break;

            default:
                return null;
        }

        return new WhoIsInfo(id, type, cause, ownerGuid, objectGuid, pos, playerId, ownerObj, cargo, radius);
    }

    /// <summary>ShipBindings.Read — a tagged list, each tag with its own payload width.</summary>
    private static void ReadShipBindings(BgoReader r)
    {
        int n = r.ReadLength();
        for (int i = 0; i < n; i++)
        {
            switch (r.ReadByte())
            {
                case 1:                     // StickerBinding: hash + stickerId
                    r.Skip(4);
                    break;
                case 2:                     // ShipModuleBinding: hash + card guid
                    r.Skip(6);
                    break;
                case 3:                     // Syfy flag, no payload
                    break;
                case 4:                     // paint system card guid
                    r.Skip(4);
                    break;
                default:
                    throw new InvalidDataException("Unknown ship binding tag.");
            }
        }
    }

    /// <summary>ShipAspects.Read — uint16 count then one byte per aspect.</summary>
    private static void ReadShipAspects(BgoReader r) => r.Skip(r.ReadLength());
}
