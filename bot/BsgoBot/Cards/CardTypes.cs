using System.Numerics;

namespace BsgoBot.Cards;

/// <summary>
/// A card's stat block: <c>uint16 statId, float value</c> pairs.
///
/// Stored raw, keyed by the numeric id rather than by the bot's <c>ObjectStat</c> enum, because
/// that enum is a deliberate subset (~50 of 293 entries). Narrowing here would silently drop
/// every stat the bot doesn't yet reason about — and the whole point of reading the catalogue is
/// to stop guessing at things we haven't looked at yet.
/// </summary>
public sealed class StatBlock
{
    private readonly Dictionary<ushort, float> _stats = [];

    public IReadOnlyDictionary<ushort, float> Raw => _stats;
    public int Count => _stats.Count;

    public void Set(ushort id, float value) => _stats[id] = value;

    public float? Get(ushort id) => _stats.TryGetValue(id, out var v) ? v : null;

    public float? Get(Protocol.ObjectStat stat) => Get((ushort)stat);

    /// <summary>First of these stats that is present. Weapon stats come in per-family variants
    /// (CannonMaxRange, MissileMaxRange, ...) with the generic one as a fallback.</summary>
    public float? First(params ushort[] ids)
    {
        foreach (var id in ids)
            if (_stats.TryGetValue(id, out var v)) return v;
        return null;
    }

    public float GetOr(ushort id, float fallback) => Get(id) ?? fallback;
}

/// <summary>One hardpoint on a hull — <c>SpotDesc</c> in the client.
///
/// <see cref="LocalPosition"/> and <see cref="LocalRotation"/> are what the server's weapon
/// range check runs on: it transforms the spot into world space and measures range and firing
/// arc from <em>there</em>, not from the ship's centre.</summary>
public sealed record SpotInfo(
    ushort ObjectPointServerHash,
    string ObjectPointName,
    SpotType Type,
    Vector3 LocalPosition,
    Vector4 LocalRotation);

/// <summary>A fitted-by-the-player slot — <c>ShipSlotCard</c>.</summary>
public sealed record ShipSlotInfo(
    ushort SlotId,
    string ObjectPoint,
    ushort ObjectPointServerHash,
    ShipSlotType SystemType,
    byte Level);

/// <summary>
/// A slot welded into the hull — <c>ShipImmutableSlot</c>. NPCs carry their weapons here rather
/// than in player-fittable slots, so this is where an enemy's armament is declared.
/// </summary>
public sealed record ImmutableSlotInfo(
    ushort SlotId,
    ushort ObjectPointServerHash,
    ShipSlotType SystemType,
    uint SystemKey,
    ushort SystemLevel,
    uint ConsumableKey);

/// <summary><c>CardView.World</c> — the physical object: model, size, hardpoints.</summary>
/// <summary>
/// <c>CardView.Owner</c> (29) — client <c>OwnerCard.Read</c>: <c>bool IsDockable</c>,
/// <c>float DockRange</c>, <c>byte Level</c>. Six bytes, and the only authority on whether a
/// thing can be docked at and from how far.
///
/// The bot used to state outright that this "isn't on the wire" and guess instead: dockability
/// from the object's type (Outpost or Cruiser), and range from a flat 250u. Both guesses cost
/// something real — the type guess sent a retreat to a body that could not be docked, and the
/// range guess was blamed for three dropped sessions it had nothing to do with. The card was
/// arriving the whole time, sitting unparsed in the cache.
/// </summary>
public sealed record OwnerCardInfo(
    uint CardGuid,
    bool IsDockable,
    float DockRange,
    byte Level);

public sealed record WorldCardInfo(
    uint CardGuid,
    string PrefabName,
    byte LodCount,
    float Radius,
    IReadOnlyList<SpotInfo> Spots,
    string SystemMapTexture,
    bool Targetable)
{
    public SpotInfo? Spot(ushort hash) => Spots.FirstOrDefault(s => s.ObjectPointServerHash == hash);
}

/// <summary>
/// <c>CardView.Ship</c> — everything the server knows about a hull, ours or an enemy's.
///
/// <see cref="Stats"/> is the profile the combat solver wants: MaxHullPoints, Avoidance,
/// ArmorValue, Accuracy. <see cref="Slots"/> supplies the slot-type mapping the loadout panel
/// currently asks the user to type in.
/// </summary>
public sealed record ShipCardInfo(
    uint CardGuid,
    uint ShipObjectKey,
    byte Level,
    byte MaxLevel,
    byte LevelRequirement,
    byte HangarId,
    uint NextCardGuid,
    float Durability,
    byte Tier,
    IReadOnlyList<ShipRole> Roles,
    byte RoleDeprecated,
    string PaperdollLayoutFile,
    IReadOnlyList<ShipSlotInfo> Slots,
    bool CubitOnlyRepair,
    IReadOnlyList<uint> VariantHangarIds,
    int ParentHangarId,
    StatBlock Stats,
    byte Faction,
    IReadOnlyList<ImmutableSlotInfo> ImmutableSlots)
{
    public float? MaxHull => Stats.Get(Protocol.ObjectStat.MaxHullPoints);
    public float? MaxPower => Stats.Get(Protocol.ObjectStat.MaxPowerPoints);
    public float? Avoidance => Stats.Get(Protocol.ObjectStat.Avoidance);
    public float? Armor => Stats.Get(Protocol.ObjectStat.ArmorValue);
    public float? Accuracy => Stats.Get(Protocol.ObjectStat.Accuracy);
    public float? Speed => Stats.Get(Protocol.ObjectStat.Speed);

    public string RoleText => Roles.Count == 0 ? "?" : string.Join("/", Roles);
}

/// <summary>
/// <c>CardView.ShipAbility</c> — one castable ability.
///
/// <see cref="ItemBuffAdd"/> is the block the server itself reads in <c>AbilityAction</c> for
/// cooldown, power cost, ranges, firing angle and damage. It is the authoritative statement of
/// what a weapon does, as opposed to what the live slot-stat stream happens to have published.
/// </summary>
public sealed record ShipAbilityCardInfo(
    uint CardGuid,
    byte Level,
    byte Launch,
    ShipAbilityAffect Affect,
    uint AbilityGroupId,
    ushort TargetTierMask,
    ushort ConsumableType,
    uint ConsumableTier,
    ShipConsumableOption ConsumableOption,
    AbilityActionType ActionType,
    AbilityActionType OverwriteActionType,
    StatBlock ItemBuffAdd,
    StatBlock ItemBuffMultiply,
    bool OnByDefault)
{
    /// <summary>The action that actually runs — an overwrite wins when present, exactly as the
    /// client's <c>BuffActionType</c> resolves it.</summary>
    public AbilityActionType EffectiveAction =>
        OverwriteActionType != AbilityActionType.None ? OverwriteActionType : ActionType;

    public float? MaxRange => ItemBuffAdd.First(
        (ushort)Protocol.ObjectStat.CannonMaxRange,
        (ushort)Protocol.ObjectStat.MissileMaxRange,
        (ushort)Protocol.ObjectStat.MiningMaxRange,
        (ushort)Protocol.ObjectStat.MaxRange);

    public float? MinRange => ItemBuffAdd.First(
        (ushort)Protocol.ObjectStat.CannonMinRange,
        (ushort)Protocol.ObjectStat.MissileMinRange,
        (ushort)Protocol.ObjectStat.MiningMinRange,
        (ushort)Protocol.ObjectStat.MinRange);

    public float? OptimalRange => ItemBuffAdd.First(
        (ushort)Protocol.ObjectStat.CannonOptimalRange,
        (ushort)Protocol.ObjectStat.MiningOptimalRange,
        (ushort)Protocol.ObjectStat.OptimalRange);

    /// <summary>Half-angle of the firing cone in degrees; 0 means omnidirectional.</summary>
    public float? Angle => ItemBuffAdd.First(
        (ushort)Protocol.ObjectStat.CannonAngle,
        (ushort)Protocol.ObjectStat.MissileAngle,
        (ushort)Protocol.ObjectStat.MiningAngle,
        (ushort)Protocol.ObjectStat.Angle);

    public float? Cooldown => ItemBuffAdd.First(
        (ushort)Protocol.ObjectStat.CannonCooldown,
        (ushort)Protocol.ObjectStat.MissileCooldown,
        (ushort)Protocol.ObjectStat.MiningCooldown,
        (ushort)Protocol.ObjectStat.Cooldown);

    public float? PowerCost => ItemBuffAdd.First(
        (ushort)Protocol.ObjectStat.CannonPowerPointCost,
        (ushort)Protocol.ObjectStat.MissilePowerPointCost,
        (ushort)Protocol.ObjectStat.MiningPowerPointCost,
        (ushort)Protocol.ObjectStat.PowerPointCost);

    /// <summary>Mean damage per shot, averaged over the published low/high band.</summary>
    public float? MeanDamage
    {
        get
        {
            float? lo = ItemBuffAdd.First(
                (ushort)Protocol.ObjectStat.CannonDamageLow,
                (ushort)Protocol.ObjectStat.MissileDamageLow,
                (ushort)Protocol.ObjectStat.DamageLow);
            float? hi = ItemBuffAdd.First(
                (ushort)Protocol.ObjectStat.CannonDamageHigh,
                (ushort)Protocol.ObjectStat.MissileDamageHigh,
                (ushort)Protocol.ObjectStat.DamageHigh);
            if (lo is null && hi is null) return null;
            return ((lo ?? hi)!.Value + (hi ?? lo)!.Value) / 2f;
        }
    }

    /// <summary>Damage per second at full uptime, or null if either half is unknown.</summary>
    public float? Dps => MeanDamage is { } d && Cooldown is { } cd && cd > 0 ? d / cd : null;
}

/// <summary>
/// <c>CardView.ShipSystem</c> — the installable item that grants abilities. The link from a
/// slot's <c>SystemKey</c> to the ability numbers runs through here.
/// </summary>
public sealed record ShipSystemCardInfo(
    uint CardGuid,
    byte Level,
    byte MaxLevel,
    uint NextCardGuid,
    ShipSlotType SlotType,
    byte Tier,
    IReadOnlyList<uint> ShipObjectKeyRestrictions,
    IReadOnlyList<ShipRole> RoleRestrictions,
    IReadOnlyList<uint> SkillHashes,
    IReadOnlyList<uint> AbilityCardGuids,
    StatBlock StaticBuffs,
    StatBlock MultiplyBuffs,
    float Durability);

/// <summary>
/// <c>CardView.ShipList</c> — a faction's whole ship roster as guid pairs (base, upgrade).
/// Two of these cards enumerate every player-flyable hull on the server.
/// </summary>
public sealed record ShipListCardInfo(
    uint CardGuid,
    IReadOnlyList<uint> ShipCardGuids,
    IReadOnlyList<uint> UpgradeShipCardGuids);
