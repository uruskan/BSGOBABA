using BsgoBot.Net;

namespace BsgoBot.Cards;

/// <summary>
/// Decodes card bodies out of <c>CatalogueProtocol Reply.Card</c>.
///
/// Every layout below is transcribed field-for-field from the decompiled client's
/// <c>Read(BgoProtocolReader)</c> override for that card class. The client is the authority
/// here rather than any server implementation: it is the binary that actually talks to the
/// server we are proxying, so whatever it parses is by definition what arrives.
///
/// Two client conventions matter and are easy to get wrong:
/// <list type="bullet">
/// <item><c>GameItemCard.Read</c> — the base of <c>ShipCard</c> — consumes <b>no</b> bytes. It
/// only fetches sibling views. So a ship card body starts immediately at ShipObjectKey.</item>
/// <item><c>ReadSet&lt;T&gt;</c> is a <b>uint16 bitmask</b>, not a counted list. Reading it as a
/// count would desynchronise everything after it.</item>
/// </list>
/// </summary>
public static class CardReader
{
    /// <summary>Views this class knows how to decode. Anything else is still cached raw.</summary>
    public static bool CanParse(CardView view) => view is
        CardView.Ship or CardView.World or CardView.ShipAbility or
        CardView.ShipSystem or CardView.ShipList or CardView.Owner;

    /// <summary>Client <c>OwnerCard.Read</c>. Three fields, six bytes.</summary>
    public static OwnerCardInfo ReadOwner(uint guid, BgoReader r)
    {
        bool dockable = r.ReadBoolean();
        float dockRange = r.ReadSingle();
        byte level = r.ReadByte();
        return new OwnerCardInfo(guid, dockable, dockRange, level);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>ObjectStats: <c>uint16 count</c> then that many <c>uint16 id, float value</c>.</summary>
    public static StatBlock ReadStats(BgoReader r)
    {
        var block = new StatBlock();
        int n = r.ReadLength();
        for (int i = 0; i < n; i++)
        {
            ushort id = r.ReadUInt16();
            block.Set(id, r.ReadSingle());
        }
        return block;
    }

    /// <summary>
    /// The client's <c>ReadSet&lt;T&gt;</c>: a single uint16 whose set bits are the members.
    /// Kept as the raw mask — the bot only ever asks "does this contain X".
    /// </summary>
    private static ushort ReadSetMask(BgoReader r) => r.ReadUInt16();

    // ---------------------------------------------------------------- World

    /// <summary>Client <c>WorldCard.Read</c>.</summary>
    public static WorldCardInfo ReadWorld(uint guid, BgoReader r)
    {
        string prefab = r.ReadString();
        byte lod = r.ReadByte();
        float radius = r.ReadSingle();

        int spotCount = r.ReadLength();
        var spots = new List<SpotInfo>(spotCount);
        for (int i = 0; i < spotCount; i++)
        {
            ushort hash = r.ReadUInt16();
            string name = r.ReadString();
            var type = (SpotType)r.ReadByte();
            var pos = r.ReadVector3();
            var rot = r.ReadQuaternion();
            spots.Add(new SpotInfo(hash, name, type, pos, rot));
        }

        string mapTexture = r.ReadString();
        r.ReadSByte();                        // FrameIndex
        r.ReadSByte();                        // SecondaryFrameIndex
        bool targetable = r.ReadBoolean();
        r.ReadBoolean();                      // ShowBracketWhenInRange
        r.ReadBoolean();                      // ForceShowOnMap

        return new WorldCardInfo(guid, prefab, lod, radius, spots, mapTexture, targetable);
    }

    // ---------------------------------------------------------------- Ship

    /// <summary>
    /// Client <c>ShipCard.Read</c>. The trailing guid the client reads and discards is kept
    /// discarded here too — it is fetched as a sibling view, not used as a field.
    /// </summary>
    public static ShipCardInfo ReadShip(uint guid, BgoReader r)
    {
        uint shipObjectKey = r.ReadGuid();
        byte level = r.ReadByte();
        byte maxLevel = r.ReadByte();
        byte levelReq = r.ReadByte();
        byte hangarId = r.ReadByte();
        uint nextCard = r.ReadGuid();
        float durability = r.ReadSingle();
        byte tier = r.ReadByte();

        int roleCount = r.ReadLength();
        var roles = new List<ShipRole>(roleCount);
        for (int i = 0; i < roleCount; i++) roles.Add((ShipRole)r.ReadByte());

        byte roleDeprecated = r.ReadByte();
        string paperdoll = r.ReadString();

        int slotCount = r.ReadLength();
        var slots = new List<ShipSlotInfo>(slotCount);
        for (int i = 0; i < slotCount; i++)
        {
            ushort slotId = r.ReadUInt16();
            string objectPoint = r.ReadString();
            ushort hash = r.ReadUInt16();
            var type = (ShipSlotType)r.ReadByte();
            byte slotLevel = r.ReadByte();
            slots.Add(new ShipSlotInfo(slotId, objectPoint, hash, type, slotLevel));
        }

        bool cubitOnlyRepair = r.ReadBoolean();
        var variants = r.ReadUInt32List();
        int parentHangar = r.ReadInt32();
        var stats = ReadStats(r);
        byte faction = r.ReadByte();

        int immCount = r.ReadLength();
        var immutable = new List<ImmutableSlotInfo>(immCount);
        for (int i = 0; i < immCount; i++)
        {
            ushort slotId = r.ReadUInt16();
            ushort hash = r.ReadUInt16();
            var type = (ShipSlotType)r.ReadByte();
            uint systemKey = r.ReadGuid();
            ushort systemLevel = r.ReadUInt16();
            uint consumableKey = r.ReadGuid();
            immutable.Add(new ImmutableSlotInfo(slotId, hash, type, systemKey, systemLevel, consumableKey));
        }

        r.ReadGuid();                         // UpgradeRewardCard guid, fetched not stored

        return new ShipCardInfo(
            guid, shipObjectKey, level, maxLevel, levelReq, hangarId, nextCard, durability, tier,
            roles, roleDeprecated, paperdoll, slots, cubitOnlyRepair, variants, parentHangar,
            stats, faction, immutable);
    }

    // ---------------------------------------------------------------- ShipAbility

    /// <summary>Client <c>ShipAbilityCard.Read</c>.</summary>
    public static ShipAbilityCardInfo ReadShipAbility(uint guid, BgoReader r)
    {
        byte level = r.ReadByte();
        byte launch = r.ReadByte();
        var affect = (ShipAbilityAffect)r.ReadByte();
        uint groupId = r.ReadUInt32();
        ushort targetTiers = ReadSetMask(r);
        ushort consumableType = r.ReadUInt16();
        uint consumableTier = r.ReadUInt32();
        var consumableOption = (ShipConsumableOption)r.ReadByte();
        var actionType = (AbilityActionType)r.ReadByte();
        var overwriteAction = (AbilityActionType)r.ReadByte();
        r.ReadString();                       // GUIBuffAtlas
        r.ReadUInt16();                       // GUIBuffIndex

        var itemAdd = ReadStats(r);
        var itemMul = ReadStats(r);
        ReadStats(r);                         // RemoteBuffAdd
        ReadStats(r);                         // RemoteBuffMultiply
        ReadStats(r);                         // ToggleSystemAdd
        ReadStats(r);                         // ToggleSystemMultiply

        bool onByDefault = r.ReadBoolean();

        int blacklist = r.ReadLength();
        r.Skip(blacklist);                    // effectTypeBlacklist, one byte each
        int affected = r.ReadLength();
        r.Skip(affected);                     // AffectedAbilityTypes, one byte each

        return new ShipAbilityCardInfo(
            guid, level, launch, affect, groupId, targetTiers, consumableType, consumableTier,
            consumableOption, actionType, overwriteAction, itemAdd, itemMul, onByDefault);
    }

    // ---------------------------------------------------------------- ShipSystem

    /// <summary>Client <c>ShipSystemCard.Read</c>.</summary>
    public static ShipSystemCardInfo ReadShipSystem(uint guid, BgoReader r)
    {
        byte level = r.ReadByte();
        byte maxLevel = r.ReadByte();
        uint nextCard = r.ReadGuid();
        var slotType = (ShipSlotType)r.ReadByte();
        byte tier = r.ReadByte();

        var shipRestrictions = r.ReadUInt32List();

        int roleCount = r.ReadLength();
        var roleRestrictions = new List<ShipRole>(roleCount);
        for (int i = 0; i < roleCount; i++) roleRestrictions.Add((ShipRole)r.ReadByte());

        var skillHashes = r.ReadUInt32List();
        var abilityGuids = r.ReadUInt32List();

        var staticBuffs = ReadStats(r);
        var multiplyBuffs = ReadStats(r);
        float durability = r.ReadSingle();

        // Read to the end even though none of it is kept: a parser that stops early cannot tell
        // "I ignored the tail" from "the layout drifted and I am now misaligned", and the
        // leftover-bytes check in CatalogueSpy is the only warning we get for the latter.
        r.ReadByte();                         // ShipSystemClass
        int views = r.ReadLength();
        r.Skip(views);                        // StatView, one byte each
        r.ReadBoolean();                      // Unique
        r.ReadBoolean();                      // ReplaceableOnly
        r.ReadBoolean();                      // UserUpgradeable
        r.ReadBoolean();                      // Trashable
        r.ReadBoolean();                      // Indestructible
        r.ReadByte();                         // MaxCountPerShip

        return new ShipSystemCardInfo(
            guid, level, maxLevel, nextCard, slotType, tier,
            shipRestrictions, roleRestrictions, skillHashes, abilityGuids,
            staticBuffs, multiplyBuffs, durability);
    }

    // ---------------------------------------------------------------- ShipList

    /// <summary>
    /// Client <c>ShipListCard.Read</c>: a count, then that many <b>pairs</b> of guids — the base
    /// ship and its upgraded form.
    /// </summary>
    public static ShipListCardInfo ReadShipList(uint guid, BgoReader r)
    {
        int n = r.ReadLength();
        var ships = new List<uint>(n);
        var upgrades = new List<uint>(n);
        for (int i = 0; i < n; i++)
        {
            ships.Add(r.ReadGuid());
            upgrades.Add(r.ReadGuid());
        }
        return new ShipListCardInfo(guid, ships, upgrades);
    }
}
