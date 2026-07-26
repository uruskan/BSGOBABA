namespace BsgoBot.World;

/// <summary>
/// One slot of one hangar ship, exactly as the server stated it in PlayerProtocol Reply.Slots.
///
/// This is the only place the slot list arrives as a fact. Everywhere else the bot has to infer
/// which slots exist — from the per-slot stat stream, or from watching you fire — and both of
/// those only ever show the slots that happen to publish something. A hull-repair module that
/// costs power and deals no damage appears in neither until you press it.
///
/// <para><see cref="SystemGuid"/> is the catalogue guid of the installed system, or 0 for an
/// empty slot. The bot does not read the catalogue, so it is an opaque identity — but it is a
/// stable one, which is what lets a name you typed follow the item across a refit.</para>
///
/// <para><see cref="ConsumableGuid"/> is the ammo or charge loaded into the slot, i.e. what the
/// game's "switch ammo" window picks.</para>
/// </summary>
public sealed record ShipSlotInfo(
    ushort SlotId,
    uint SystemGuid,
    uint ConsumableGuid,
    bool Inoperable,
    DateTime SeenAt)
{
    public bool Filled => SystemGuid != 0;
}

/// <summary>The slots of one hangar ship, keyed by slot id.</summary>
public sealed class ShipLoadout(ushort shipId)
{
    private readonly Dictionary<ushort, ShipSlotInfo> _slots = new();

    public ushort ShipId { get; } = shipId;
    public uint ShipGuid { get; set; }
    public string Name { get; set; } = "";
    public DateTime UpdatedAt { get; private set; }

    public void Set(ShipSlotInfo slot)
    {
        _slots[slot.SlotId] = slot;
        UpdatedAt = DateTime.UtcNow;
    }

    public int Count => _slots.Count;

    public List<ShipSlotInfo> Slots() => _slots.Values.OrderBy(s => s.SlotId).ToList();

    public ShipSlotInfo? Slot(ushort id) => _slots.GetValueOrDefault(id);
}
