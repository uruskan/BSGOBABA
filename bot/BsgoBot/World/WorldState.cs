using System.Numerics;
using BsgoBot.Net;
using BsgoBot.Protocol;

namespace BsgoBot.World;

public sealed class SpaceObj
{
    public uint Id;
    public SpaceEntityType Type;
    public Faction Faction;
    public FactionGroup Group;
    public bool IsMe;

    /// <summary>Last position the server actually told us about.</summary>
    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>False until a WhoIs or a movement update gave us a real position.
    /// Without this, an unlocated object sits at the sector origin and looks "nearest".</summary>
    public bool HasPosition;

    /// <summary>Any traffic at all mentioning this object.</summary>
    public DateTime LastSeen = DateTime.UtcNow;

    /// <summary>When <see cref="Position"/> was last refreshed — drives dead reckoning.</summary>
    public DateTime PositionStamp = DateTime.UtcNow;

    public float Radius;
    public uint PlayerId;              // player ships only
    public uint OwnerObjectId;         // missiles, mines
    public CargoInteraction CargoAction;

    // Reply.Info / Reply.Stats — only arrives for objects we (or the client) subscribed to.
    public bool StatsKnown;
    public float Hull = 1f;
    public float Power = 1f;
    public float Vital = 1f;
    public bool InCombat;
    public uint TargetId;
    public bool Cloaked;

    // Reply.Scan — asteroids only.
    public bool Scanned;
    public bool IsMinable;
    public uint ResourceGuid;
    public uint ResourceCount;
    public DateTime MiningCooldown;

    /// <summary>When the scan arrived. A rock's contents are not fixed — the server respawns
    /// them on a timer and can pick a different resource — so a scan is a snapshot with a
    /// shelf life, not a permanent fact.</summary>
    public DateTime ScannedAt;

    public float DistanceTo(Vector3 p) => Vector3.Distance(Position, p);

    /// <summary>
    /// Position advanced along the last known velocity. The client reconstructs this exactly
    /// by replaying maneuvers; we linearise instead, which is accurate for a second or two and
    /// clamped past that so a stale ship never appears to have flown to the far side of the map.
    /// </summary>
    public Vector3 PredictedPosition(DateTime now, float maxSeconds = 3f)
    {
        if (!HasPosition) return Position;
        if (Velocity.LengthSquared() < 0.01f) return Position;
        float dt = (float)(now - PositionStamp).TotalSeconds;
        if (dt <= 0f) return Position;
        return Position + Velocity * Math.Min(dt, maxSeconds);
    }

    public override string ToString() => $"{Type} #{Id:X8}";
}

/// <summary>Item offered in a loot pile (PlayerProtocol Reply.Loot).</summary>
public readonly record struct LootItem(ushort ServerId, ItemType Type, uint CardGuid, uint Count);

/// <summary>
/// Live picture of the current sector, rebuilt purely from sniffed server traffic.
/// Nothing here is guessed: every field comes from a message layout transcribed
/// out of the client binary.
/// </summary>
public sealed class WorldState
{
    private readonly Dictionary<uint, SpaceObj> _objects = new();
    private readonly Lock _gate = new();

    /// <summary>Per-slot stats for MY ship, from the Player/Stats and Game/Info streams.
    /// This is where real weapon ranges and cooldowns come from.</summary>
    private readonly Dictionary<ushort, Dictionary<ObjectStat, float>> _slotStats = new();

    /// <summary>Slots the server reported a toggle buff for — i.e. continuous-fire abilities.</summary>
    private readonly HashSet<ushort> _toggleSlots = [];

    /// <summary>Every hangar ship the server has described, by ship id. Reply.Slots names the
    /// ship it is describing, and it describes ships you are not flying too.</summary>
    private readonly Dictionary<ushort, ShipLoadout> _hangar = new();

    /// <summary>Player names harvested from SubscribeProtocol. The client asks for these itself
    /// whenever a player shows up, so they arrive without the bot requesting anything.</summary>
    private readonly Dictionary<uint, string> _playerNames = new();

    /// <summary>My own player id, straight from PlayerProtocol Reply.ID.</summary>
    public uint MyPlayerId { get; private set; }

    /// <summary>Hangar id of the ship I am flying (PlayerProtocol Reply.ActiveShip). Not the
    /// same number as <see cref="MyObjectId"/>, which is the sector object.</summary>
    public ushort MyShipId { get; private set; }

    public uint MyObjectId { get; private set; }
    public Vector3 MyPosition { get; private set; }
    public Vector3 MyVelocity { get; private set; }
    public bool MyPositionKnown { get; private set; }
    public Faction MyFaction { get; private set; }
    public FactionGroup MyGroup { get; private set; }

    /// <summary>
    /// Hull and power in POINTS, exactly as the server sends them — SpaceSubscribeInfo carries
    /// absolute values clamped against MaxHullPoints / MaxPowerPoints, not fractions. Treating
    /// them as fractions is why a 495-hull viper read as "49,500%" and never retreated.
    /// Use <see cref="MyHullFraction"/> / <see cref="MyPowerFraction"/> to compare with a ratio.
    /// </summary>
    public float MyHull { get; private set; } = 1f;
    public float MyPower { get; private set; } = 1f;

    public float? MyMaxHull => ShipStat(ObjectStat.MaxHullPoints);
    public float? MyMaxPower => ShipStat(ObjectStat.MaxPowerPoints);

    /// <summary>Hull as a 0..1 ratio, or null until the server publishes MaxHullPoints.</summary>
    public float? MyHullFraction => MyMaxHull is > 0 ? Math.Clamp(MyHull / MyMaxHull.Value, 0f, 1f) : null;

    /// <summary>Power as a 0..1 ratio, or null until the server publishes MaxPowerPoints.</summary>
    public float? MyPowerFraction => MyMaxPower is > 0 ? Math.Clamp(MyPower / MyMaxPower.Value, 0f, 1f) : null;
    public uint CurrentTarget { get; set; }

    public int KnownObjects { get { lock (_gate) return _objects.Count; } }

    /// <summary>Raised when an object leaves the sector because it was destroyed.</summary>
    public event Action<uint>? Died;

    /// <summary>Raised when the server offers a loot pile: (lootId, items).</summary>
    public event Action<ushort, IReadOnlyList<LootItem>>? LootOffered;

    /// <summary>Items the server just added to the cargo hold. The one honest measure of yield.</summary>
    public event Action<IReadOnlyList<LootItem>>? HoldGained;

    /// <summary>Raised when a cast we (or you) made was accepted/rejected: (slotId, ok).</summary>
    public event Action<ushort, bool>? CastResult;

    /// <summary>Raised when the server turns an ability off, or -1 for all of them.</summary>
    public event Action<short>? AbilityStopped;

    /// <summary>Raised when we leave the sector entirely (death, jump, dock, disconnect).</summary>
    public event Action<RemovingCause>? SectorLeft;

    /// <summary>Raised when the server answers a scan for an asteroid. The reply is what
    /// identifies which of your abilities the scanner is — nothing else announces it.</summary>
    public event Action<uint>? ScanReceived;

    /// <summary>Raised when the server restates your ship's slots, so the loadout panel can
    /// pick up a refit without polling.</summary>
    public event Action? LoadoutChanged;

    public event Action<string>? Log;

    public List<SpaceObj> Snapshot()
    {
        lock (_gate) return _objects.Values.Select(Clone).ToList();
    }

    public SpaceObj? Get(uint id)
    {
        lock (_gate) return _objects.TryGetValue(id, out var o) ? Clone(o) : null;
    }

    private static SpaceObj Clone(SpaceObj o) => new()
    {
        Id = o.Id, Type = o.Type, Faction = o.Faction, Group = o.Group, IsMe = o.IsMe,
        Position = o.Position, Velocity = o.Velocity, HasPosition = o.HasPosition,
        LastSeen = o.LastSeen, PositionStamp = o.PositionStamp,
        Radius = o.Radius, PlayerId = o.PlayerId, OwnerObjectId = o.OwnerObjectId,
        CargoAction = o.CargoAction,
        StatsKnown = o.StatsKnown, Hull = o.Hull, Power = o.Power, Vital = o.Vital,
        InCombat = o.InCombat, TargetId = o.TargetId, Cloaked = o.Cloaked,
        Scanned = o.Scanned, IsMinable = o.IsMinable,
        ResourceGuid = o.ResourceGuid, ResourceCount = o.ResourceCount,
        MiningCooldown = o.MiningCooldown, ScannedAt = o.ScannedAt,
    };

    /// <summary>Pseudo-slot that hull-wide stats (StatUpdateType.Stat) are filed under.
    /// Real slot ids are byte-sized, so this can never collide with one.</summary>
    public const ushort ShipWideSlot = ushort.MaxValue;

    /// <summary>Best known stat for one of my slots, or null if the server never sent it.</summary>
    public float? SlotStat(ushort slot, ObjectStat stat)
    {
        lock (_gate)
        {
            if (_slotStats.TryGetValue(slot, out var m) && m.TryGetValue(stat, out var v)) return v;
        }
        return null;
    }

    /// <summary>A hull-wide stat of my ship (speed, max hull, dradis range...).</summary>
    public float? ShipStat(ObjectStat stat) => SlotStat(ShipWideSlot, stat);

    /// <summary>
    /// The three radii your client filters the sector with. DetectionInner/Outer/Visual are
    /// what DradisHelper actually reads; DradisRange/MapRange are the older stats some
    /// servers publish instead, so either will do.
    /// </summary>
    public DetectionRanges Detection => new(
        ShipStat(ObjectStat.DetectionVisualRadius) ?? 0f,
        ShipStat(ObjectStat.DetectionInnerRadius) ?? ShipStat(ObjectStat.DradisRange) ?? 0f,
        ShipStat(ObjectStat.DetectionOuterRadius) ?? ShipStat(ObjectStat.MapRange) ?? 0f);

    /// <summary>Which visibility band a contact falls into, from my ship's point of view.</summary>
    public ContactLayer LayerOf(SpaceObj o, DetectionRanges? ranges = null)
    {
        float? d = DistanceToMe(o);
        if (d is null) return ContactLayer.Unknown;
        return Visibility.Classify(o, d.Value, ranges ?? Detection);
    }

    /// <summary>Every real slot the server has reported any stat for.</summary>
    public IReadOnlyCollection<ushort> KnownSlots()
    {
        lock (_gate) return _slotStats.Keys.Where(s => s != ShipWideSlot).ToList();
    }

    // ---------------------------------------------------------------- loadout & names

    /// <summary>
    /// The slot list of the ship I am flying, or null until the server sends one.
    ///
    /// Falls back to the only ship described if Reply.ActiveShip hasn't arrived — on a server
    /// that sends the active ship's slots and nothing else, insisting on the id would leave the
    /// panel empty for no reason.
    /// </summary>
    public ShipLoadout? MyLoadout
    {
        get
        {
            lock (_gate)
            {
                if (MyShipId != 0 && _hangar.TryGetValue(MyShipId, out var mine)) return mine;
                return _hangar.Count == 1 ? _hangar.Values.First() : null;
            }
        }
    }

    /// <summary>Slots of the ship I am flying. Empty rather than null, so callers can just loop.</summary>
    public List<ShipSlotInfo> MySlots() => MyLoadout?.Slots() ?? [];

    /// <summary>A player's name, once the client has asked the server for it. Null otherwise —
    /// never a made-up placeholder, so the UI can say "unnamed" and mean it.</summary>
    public string? PlayerName(uint playerId)
    {
        if (playerId == 0) return null;
        lock (_gate) return _playerNames.GetValueOrDefault(playerId);
    }

    /// <summary>Name of the player flying an object, if it is a player ship and we know it.</summary>
    public string? NameOf(SpaceObj o) => o.PlayerId == 0 ? null : PlayerName(o.PlayerId);

    /// <summary>All stats known for one slot — used by the UI to explain what it learned.</summary>
    public IReadOnlyDictionary<ObjectStat, float> SlotStats(ushort slot)
    {
        lock (_gate)
            return _slotStats.TryGetValue(slot, out var m)
                ? new Dictionary<ObjectStat, float>(m)
                : new Dictionary<ObjectStat, float>();
    }

    public bool IsToggleSlot(ushort slot)
    {
        lock (_gate) return _toggleSlots.Contains(slot);
    }

    /// <summary>Relation of an object to me, using the client's own rule.</summary>
    public Relation RelationTo(uint objectId)
    {
        if (objectId == MyObjectId) return Relation.Self;
        return EntityTypes.RelationTo(objectId, MyFaction, MyGroup);
    }

    /// <summary>
    /// Nearest object matching a predicate. Only located objects are considered — an object
    /// we know exists but have never been given a position for is not "at the origin", it is
    /// simply not a candidate.
    /// </summary>
    public SpaceObj? Nearest(Func<SpaceObj, bool> match, float maxAgeSeconds = 60f)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            return _objects.Values
                .Where(o => !o.IsMe && o.HasPosition && match(o))
                .Where(o => EntityTypes.IsStatic(o.Id) || (now - o.LastSeen).TotalSeconds <= maxAgeSeconds)
                .Select(Clone)
                .OrderBy(o => Vector3.Distance(o.PredictedPosition(now), MyPosition))
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Highest-scoring object matching a predicate, where the score is computed from the object
    /// and its distance from me. Same visibility rules as <see cref="Nearest"/> — an object we
    /// have never been given a position for is not a candidate.
    /// </summary>
    public SpaceObj? Best(Func<SpaceObj, bool> match, Func<SpaceObj, float, float> score, float maxAgeSeconds = 60f)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            return _objects.Values
                .Where(o => !o.IsMe && o.HasPosition && match(o))
                .Where(o => EntityTypes.IsStatic(o.Id) || (now - o.LastSeen).TotalSeconds <= maxAgeSeconds)
                .Select(Clone)
                .OrderByDescending(o => score(o, Vector3.Distance(o.PredictedPosition(now), MyPosition)))
                .FirstOrDefault();
        }
    }

    /// <summary>Straight-line distance from my ship, dead-reckoned. Null if either end is unlocated.</summary>
    public float? DistanceToMe(SpaceObj o)
    {
        if (!MyPositionKnown || !o.HasPosition) return null;
        return Vector3.Distance(o.PredictedPosition(DateTime.UtcNow), MyPosition);
    }

    /// <summary>
    /// Supplies the player id from outside the sector stream — the launcher's +userID, or the
    /// login handshake the client sends. PlayerProtocol Reply.ID overrides it if it disagrees,
    /// because that one comes from the server itself.
    /// </summary>
    public void SeedPlayerId(uint playerId, string source)
    {
        if (playerId == 0 || MyPlayerId == playerId) return;
        MyPlayerId = playerId;
        Log?.Invoke($"My player id is {playerId} (from {source}).");
        AdoptShipOfPlayer(playerId);
    }

    public void SetMe(uint objectId)
    {
        lock (_gate)
        {
            if (MyObjectId == objectId) return;

            if (MyObjectId != 0 && _objects.TryGetValue(MyObjectId, out var old)) old.IsMe = false;

            MyObjectId = objectId;
            MyFaction = EntityTypes.FactionOf(objectId);
            MyGroup = EntityTypes.GroupOf(objectId);

            if (_objects.TryGetValue(objectId, out var o))
            {
                o.IsMe = true;
                if (o.HasPosition)
                {
                    MyPosition = o.Position;
                    MyVelocity = o.Velocity;
                    MyPositionKnown = true;
                }
            }
        }

        Log?.Invoke($"Identified my ship: #{objectId:X8} ({MyFaction}/{MyGroup}).");
    }

    /// <summary>
    /// Feeds one server-&gt;client message into the model. Unknown messages are ignored
    /// rather than guessed at, so a wrong assumption can never corrupt the map.
    /// </summary>
    public void OnServerMessage(ProtocolId protocol, ushort msgType, BgoReader r)
    {
        switch (protocol)
        {
            case ProtocolId.Game: OnGame(msgType, r); break;
            case ProtocolId.Player: OnPlayer(msgType, r); break;
            case ProtocolId.Subscribe: OnSubscribe(msgType, r); break;
        }
    }

    private void OnGame(ushort msgType, BgoReader r)
    {
        switch ((GameOp.Reply)msgType)
        {
            case GameOp.Reply.WhoIs:
            {
                uint id = r.ReadUInt32();
                var info = WhoIsReader.Read(id, r);
                if (info is not null) ApplyWhoIs(info.Value);
                else Touch(id);
                break;
            }

            case GameOp.Reply.Move:
            {
                // uint32 objectId, Maneuver
                uint id = r.ReadUInt32();
                ApplyManeuver(id, r);
                break;
            }

            case GameOp.Reply.SyncMove:
            {
                // uint32 objectId, Tick, MovementFrame, Maneuver
                uint id = r.ReadUInt32();
                r.ReadTick();
                var pos = r.ReadVector3();            // MovementFrame.position
                r.ReadEuler();                        // euler3
                var lin = r.ReadVector3();            // linearSpeed
                var strafe = r.ReadVector3();         // strafeSpeed
                r.ReadEuler();                        // Euler3Speed
                r.ReadByte();                         // mode
                Locate(id, pos, lin + strafe);
                ApplyManeuver(id, r);                 // trailing maneuver refines heading
                break;
            }

            case GameOp.Reply.ObjectLeft:
            {
                int n = r.ReadLength();
                for (int i = 0; i < n; i++)
                {
                    uint id = r.ReadUInt32();
                    r.ReadTick();
                    var cause = (RemovingCause)r.ReadByte();

                    // Three causes carry a trailing uint32; it must be consumed or
                    // every later entry in this message would be misaligned.
                    switch (cause)
                    {
                        case RemovingCause.Death:
                        case RemovingCause.Hit:
                        case RemovingCause.Collected:
                            r.ReadUInt32();
                            break;
                    }

                    Remove(id);
                    if (cause == RemovingCause.Death) Died?.Invoke(id);
                }
                break;
            }

            case GameOp.Reply.Info:
            {
                uint id = r.ReadUInt32();
                ReadStats(id, r);
                break;
            }

            case GameOp.Reply.ObjectState:
            {
                // SpaceObjectState.Read
                uint id = r.ReadUInt32();
                r.ReadUInt32();                       // revision
                r.ReadBoolean();                      // marked
                r.ReadBoolean();                      // fortified
                r.ReadSingle();                       // base signature
                bool cloaked = r.ReadBoolean();
                r.ReadBoolean();                      // short circuited
                r.ReadByte();                         // cargo volume
                lock (_gate)
                {
                    var o = GetOrAdd(id);
                    o.Cloaked = cloaked;
                    o.LastSeen = DateTime.UtcNow;
                }
                break;
            }

            case GameOp.Reply.Scan:
            {
                // uint32 asteroidId, Item, bool minable, Price, DateTime cooldown
                uint id = r.ReadUInt32();
                var (_, resourceGuid, resourceCount) = ReadItem(r);
                bool minable = r.ReadBoolean();
                SkipPrice(r);
                var cooldown = r.ReadDateTime();
                lock (_gate)
                {
                    var o = GetOrAdd(id);
                    o.Scanned = true;
                    o.IsMinable = minable;
                    o.ResourceGuid = resourceGuid;
                    o.ResourceCount = resourceCount;
                    o.MiningCooldown = cooldown;
                    o.ScannedAt = DateTime.UtcNow;
                    o.LastSeen = DateTime.UtcNow;
                }
                ScanReceived?.Invoke(id);
                break;
            }

            case GameOp.Reply.WeaponShot:
            {
                uint shooter = r.ReadUInt32();
                r.ReadUInt16();                       // objectPointHash
                uint target = r.ReadUInt32();
                Touch(shooter);
                if (target != 0) Touch(target);
                break;
            }

            case GameOp.Reply.Cast:
            {
                ushort slot = r.ReadUInt16();
                r.ReadByte();                         // SlotCategory
                bool ok = r.ReadByte() == 1;          // ShipAbility.CastReply: 0 = False, 1 = Ok
                CastResult?.Invoke(slot, ok);
                break;
            }

            case GameOp.Reply.StopSlotAbility:
            {
                short slot = r.ReadInt16();           // -1 means "all of them"
                AbilityStopped?.Invoke(slot);
                break;
            }

            case GameOp.Reply.RemoveMe:
            {
                var cause = (RemovingCause)r.ReadByte();
                Clear();
                SectorLeft?.Invoke(cause);
                break;
            }
        }
    }

    private void OnPlayer(ushort msgType, BgoReader r)
    {
        switch ((PlayerOp.Reply)msgType)
        {
            case PlayerOp.Reply.ID:
                SeedPlayerId(r.ReadUInt32(), "the server");
                break;

            // MyShipStats.Read — same layout as any object's stats, and the only place
            // per-slot weapon ranges and cooldowns are ever published. This one is always
            // mine by definition, and usually arrives before my ship's WhoIs does, so it
            // must not be gated on already knowing my object id.
            case PlayerOp.Reply.Stats:
                ReadStats(MyObjectId, r, alwaysMine: true);
                break;

            case PlayerOp.Reply.Loot:
            {
                ushort lootId = r.ReadUInt16();
                var items = ReadItemList(r);
                LootOffered?.Invoke(lootId, items);
                break;
            }

            // What actually reached the hold. Everything else about yield is inference —
            // a scan says what a rock holds, not what we got out of it, and another player
            // can break the rock we were shooting. This is the server stating the answer.
            case PlayerOp.Reply.HoldItems:
            {
                var items = ReadItemList(r);
                if (items.Count > 0) HoldGained?.Invoke(items);
                break;
            }

            // The loadout, stated outright. Client: PlayerProtocol Reply.Slots.
            case PlayerOp.Reply.Slots:
                ReadSlots(r);
                break;

            case PlayerOp.Reply.ActiveShip:
            {
                ushort id = r.ReadUInt16();
                bool changed;
                lock (_gate) { changed = MyShipId != id; MyShipId = id; }
                if (changed) LoadoutChanged?.Invoke();
                break;
            }

            case PlayerOp.Reply.AddShip:
            {
                ushort id = r.ReadUInt16();
                uint guid = r.ReadUInt32();
                lock (_gate) Hangar(id).ShipGuid = guid;
                break;
            }

            case PlayerOp.Reply.ShipName:
            {
                ushort id = r.ReadUInt16();
                string name = r.ReadString();
                lock (_gate) Hangar(id).Name = name;
                LoadoutChanged?.Invoke();
                break;
            }
        }
    }

    /// <summary>
    /// SubscribeProtocol. Only the name is kept: it is the one thing about another player that
    /// the sector stream never carries and that a contact list is useless without.
    /// </summary>
    private void OnSubscribe(ushort msgType, BgoReader r)
    {
        if ((SubscribeOp.Reply)msgType != SubscribeOp.Reply.PlayerName) return;

        uint playerId = r.ReadUInt32();
        string name = r.ReadString();
        if (playerId == 0 || name.Length == 0) return;
        lock (_gate) _playerNames[playerId] = name;
    }

    /// <summary>
    /// PlayerProtocol Reply.Slots, field for field from ShipSlot.Read.
    ///
    ///     uint16 shipId, uint16 count,
    ///     count x { uint16 slotId, item, uint32 consumableGuid, bool inoperable }
    ///
    /// The slot id in front of each entry is the item's own ServerID — the client reads it with
    /// the item and then looks the slot up by it (ItemFactory.ReadItemWithID does the same thing
    /// for inventory lists), which is why it sits outside the item body.
    /// </summary>
    private void ReadSlots(BgoReader r)
    {
        ushort shipId = r.ReadUInt16();
        int n = r.ReadLength();
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            var ship = Hangar(shipId);
            for (int i = 0; i < n; i++)
            {
                ushort slotId = r.ReadUInt16();
                var (_, systemGuid, _) = ReadItem(r);
                uint consumable = r.ReadUInt32();
                bool inoperable = r.ReadBoolean();
                ship.Set(new ShipSlotInfo(slotId, systemGuid, consumable, inoperable, now));
            }
        }

        LoadoutChanged?.Invoke();
    }

    /// <summary>Caller must hold the lock.</summary>
    private ShipLoadout Hangar(ushort shipId)
    {
        if (!_hangar.TryGetValue(shipId, out var ship)) _hangar[shipId] = ship = new ShipLoadout(shipId);
        return ship;
    }

    // ---------------------------------------------------------------- world updates

    private void ApplyWhoIs(WhoIsInfo info)
    {
        bool becameMe = false;

        lock (_gate)
        {
            var o = GetOrAdd(info.Id);
            o.Radius = info.Radius;
            o.OwnerObjectId = info.OwnerObjectId ?? 0;
            o.CargoAction = info.CargoAction ?? CargoInteraction.None;
            o.LastSeen = DateTime.UtcNow;

            if (info.PlayerId is { } pid)
            {
                o.PlayerId = pid;
                if (MyPlayerId != 0 && pid == MyPlayerId) becameMe = true;
            }

            if (info.Position is { } p)
            {
                o.Position = p;
                o.Velocity = Vector3.Zero;
                o.HasPosition = true;
                o.PositionStamp = DateTime.UtcNow;
            }
        }

        if (becameMe) SetMe(info.Id);
    }

    /// <summary>
    /// Reads a Maneuver off the wire. Rest/Teleport/Warp state a position outright; the
    /// steering maneuvers state a heading and a march speed instead, which is enough to keep
    /// dead reckoning pointed the right way until the next SyncMove corrects it.
    /// </summary>
    private void ApplyManeuver(uint id, BgoReader r)
    {
        if (r.Remaining < 1) return;

        var type = (ManeuverType)r.ReadByte();
        switch (type)
        {
            case ManeuverType.Rest:
            {
                r.ReadTick();
                var pos = r.ReadVector3();
                r.ReadEuler();
                Locate(id, pos, Vector3.Zero);
                break;
            }

            case ManeuverType.Teleport:
            {
                r.ReadTick();
                var pos = r.ReadVector3();
                SkipMovementOptions(r);
                Locate(id, pos, Vector3.Zero);
                break;
            }

            case ManeuverType.Warp:
            {
                r.ReadTick();
                var pos = r.ReadVector3();
                r.ReadEuler();
                SkipMovementOptions(r);
                Locate(id, pos, Vector3.Zero);
                break;
            }

            case ManeuverType.Directional:
            case ManeuverType.DirectionalWithoutRoll:
            {
                r.ReadTick();
                var dir = r.ReadEuler();              // pitch, yaw, roll
                float speed = ReadMovementSpeed(r);
                SetHeading(id, dir, speed);
                break;
            }

            case ManeuverType.Pulse:
            {
                r.ReadTick();
                var vel = r.ReadVector3();
                SkipMovementOptions(r);
                SetVelocity(id, vel);
                break;
            }

            // Everything else only changes attitude or is a launch; position keeps coasting
            // on whatever the last frame said, which is what the client does too.
            default:
                Touch(id);
                break;
        }
    }

    private void Locate(uint id, Vector3 pos, Vector3 velocity)
    {
        bool isMine;
        lock (_gate)
        {
            var o = GetOrAdd(id);
            o.Position = pos;
            o.Velocity = velocity;
            o.HasPosition = true;
            o.PositionStamp = DateTime.UtcNow;
            o.LastSeen = o.PositionStamp;
            isMine = o.IsMe || id == MyObjectId;
            if (isMine)
            {
                MyPosition = pos;
                MyVelocity = velocity;
                MyPositionKnown = true;
            }
        }
    }

    /// <summary>Turns an Euler3 heading + march speed into a velocity estimate.</summary>
    private void SetHeading(uint id, Vector3 euler, float speed)
    {
        var v = Forward(euler) * speed;
        SetVelocity(id, v);
    }

    private void SetVelocity(uint id, Vector3 v)
    {
        lock (_gate)
        {
            var o = GetOrAdd(id);
            var now = DateTime.UtcNow;
            if (o.HasPosition) o.Position = o.PredictedPosition(now);   // carry forward before re-aiming
            o.Velocity = v;
            o.PositionStamp = now;
            o.LastSeen = now;
            if (o.IsMe || id == MyObjectId)
            {
                if (o.HasPosition) MyPosition = o.Position;
                MyVelocity = v;
            }
        }
    }

    /// <summary>Unity's Quaternion.Euler(pitch, yaw, roll) * Vector3.forward, without Unity.</summary>
    public static Vector3 Forward(Vector3 euler)
    {
        const float deg = MathF.PI / 180f;
        float pitch = euler.X * deg, yaw = euler.Y * deg;
        float cp = MathF.Cos(pitch);
        return new Vector3(cp * MathF.Sin(yaw), -MathF.Sin(pitch), cp * MathF.Cos(yaw));
    }

    /// <summary>Inverse of <see cref="Forward"/> — the client's Euler3.Direction.</summary>
    public static Vector3 EulerTowards(Vector3 direction)
    {
        const float rad = 180f / MathF.PI;
        float yaw = MathF.Atan2(direction.X, direction.Z) * rad;
        float flat = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        float pitch = -MathF.Atan2(direction.Y, flat) * rad;
        return new Vector3(pitch, yaw, 0f);
    }

    private void Touch(uint id)
    {
        lock (_gate) GetOrAdd(id).LastSeen = DateTime.UtcNow;
    }

    /// <summary>Caller must hold the lock.</summary>
    private SpaceObj GetOrAdd(uint id)
    {
        if (!_objects.TryGetValue(id, out var o))
        {
            _objects[id] = o = new SpaceObj
            {
                Id = id,
                Type = EntityTypes.Of(id),
                Faction = EntityTypes.FactionOf(id),
                Group = EntityTypes.GroupOf(id),
                IsMe = id != 0 && id == MyObjectId,
            };
        }
        return o;
    }

    private void Remove(uint id)
    {
        lock (_gate)
        {
            _objects.Remove(id);
            if (id == MyObjectId) MyPositionKnown = false;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _objects.Clear();
            MyPositionKnown = false;
            MyObjectId = 0;
        }
    }

    /// <summary>Player id is known but their ship's WhoIs may already have gone past — re-check.</summary>
    private void AdoptShipOfPlayer(uint playerId)
    {
        uint found = 0;
        lock (_gate)
        {
            foreach (var o in _objects.Values)
                if (o.Type == SpaceEntityType.Player && o.PlayerId == playerId) { found = o.Id; break; }
        }
        if (found != 0) SetMe(found);
    }

    // ---------------------------------------------------------------- stats stream

    /// <summary>
    /// SpaceSubscribeInfo.Read. Note the count is a signed int16 and every branch has a fixed
    /// width, so the whole message stays walkable even when a tag isn't interesting to us.
    /// </summary>
    private void ReadStats(uint objectId, BgoReader r, bool alwaysMine = false)
    {
        int n = r.ReadInt16();
        bool mine = alwaysMine || (objectId != 0 && objectId == MyObjectId);

        for (int i = 0; i < n; i++)
        {
            switch ((StatUpdateType)r.ReadByte())
            {
                case StatUpdateType.Stat:
                {
                    var stat = (ObjectStat)r.ReadUInt16();
                    float v = r.ReadSingle();
                    if (mine) RecordSlotStat(ShipWideSlot, stat, v);
                    break;
                }

                case StatUpdateType.Buff:
                case StatUpdateType.ShortCircuit:
                    r.Skip(12);                       // ServerID, AbilityGuid, MaxTime
                    break;

                case StatUpdateType.Combat:
                {
                    bool inCombat = r.ReadBoolean();
                    WithObject(objectId, o => o.InCombat = inCombat);
                    break;
                }

                case StatUpdateType.Target:
                {
                    uint t = r.ReadUInt32();
                    WithObject(objectId, o => o.TargetId = t);
                    break;
                }

                case StatUpdateType.RemoveBuff:
                case StatUpdateType.RemoveStatsModifier:
                case StatUpdateType.RemoveShortCircuit:
                case StatUpdateType.RemoveSectorModifier:
                    r.Skip(4);
                    break;

                case StatUpdateType.PowerPoints:
                {
                    float v = r.ReadSingle();
                    WithObject(objectId, o => o.Power = v);
                    if (mine) MyPower = v;
                    break;
                }

                case StatUpdateType.HullPoints:
                {
                    float v = r.ReadSingle();
                    WithObject(objectId, o => o.Hull = v);
                    if (mine) MyHull = v;
                    break;
                }

                case StatUpdateType.VitalPoints:
                {
                    float v = r.ReadSingle();
                    WithObject(objectId, o => o.Vital = v);
                    break;
                }

                case StatUpdateType.Reset:
                    break;

                case StatUpdateType.SlotStat:
                {
                    ushort slot = r.ReadByte();
                    var stat = (ObjectStat)r.ReadUInt16();
                    float v = r.ReadSingle();
                    if (mine) RecordSlotStat(slot, stat, v);
                    break;
                }

                case StatUpdateType.ShipAspects:
                    r.Skip(r.ReadLength());
                    break;

                case StatUpdateType.ToggleBuff:
                {
                    ushort slot = r.ReadByte();
                    r.ReadUInt32();                   // ability guid
                    if (mine) lock (_gate) _toggleSlots.Add(slot);
                    break;
                }

                case StatUpdateType.RemoveToggleBuff:
                    r.Skip(5);
                    break;

                case StatUpdateType.StatsModifier:
                    r.Skip(8);                        // guid + duration
                    break;

                case StatUpdateType.CaptureStatus:
                    r.Skip(5);                        // faction byte + percent
                    break;

                case StatUpdateType.SectorModifier:
                    r.Skip(12);                       // index + guid + duration
                    break;

                default:
                    // An unrecognised tag has an unknown width — stop rather than desync.
                    return;
            }
        }

        WithObject(objectId, o => { o.StatsKnown = true; o.LastSeen = DateTime.UtcNow; });
    }

    private void RecordSlotStat(ushort slot, ObjectStat stat, float value)
    {
        lock (_gate)
        {
            if (!_slotStats.TryGetValue(slot, out var m)) _slotStats[slot] = m = new();
            m[stat] = value;
        }
    }

    private void WithObject(uint id, Action<SpaceObj> apply)
    {
        if (id == 0) return;
        lock (_gate) apply(GetOrAdd(id));
    }

    // ---------------------------------------------------------------- item helpers

    /// <summary>ItemFactory.ReadItem — a tag byte then a fixed body per type.</summary>
    private static (ItemType Type, uint CardGuid, uint Count) ReadItem(BgoReader r)
    {
        var type = (ItemType)r.ReadByte();
        switch (type)
        {
            case ItemType.None:
                return (type, 0, 0);
            case ItemType.System:                     // guid + durability(float) + timeOfLastUse(double)
                { uint g = r.ReadUInt32(); r.Skip(12); return (type, g, 1); }
            case ItemType.Countable:                  // guid + count
                { uint g = r.ReadUInt32(); return (type, g, r.ReadUInt32()); }
            case ItemType.Starter:
            case ItemType.Ship:                       // guid only
                return (type, r.ReadUInt32(), 1);
            default:
                throw new InvalidDataException($"Unknown item type {type}.");
        }
    }

    /// <summary>ItemFactory.ReadItemList — count, then serverId + item for each entry.</summary>
    private static List<LootItem> ReadItemList(BgoReader r)
    {
        int n = r.ReadLength();
        var list = new List<LootItem>(n);
        for (int i = 0; i < n; i++)
        {
            ushort serverId = r.ReadUInt16();
            var (type, guid, count) = ReadItem(r);
            list.Add(new LootItem(serverId, type, guid, count));
        }
        return list;
    }

    /// <summary>Price.Read — count, then guid + amount per entry.</summary>
    private static void SkipPrice(BgoReader r) => r.Skip(r.ReadLength() * 8);

    /// <summary>MovementOptions.Read — a gear byte followed by eleven floats.</summary>
    private static void SkipMovementOptions(BgoReader r) => r.Skip(1 + 11 * 4);

    /// <summary>Same layout, but keeps the march speed we steer by.</summary>
    private static float ReadMovementSpeed(BgoReader r)
    {
        r.ReadByte();                                 // gear
        float speed = r.ReadSingle();
        r.Skip(10 * 4);
        return speed;
    }
}
