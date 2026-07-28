using System.Numerics;
using BsgoBot.Net;
using BsgoBot.Protocol;

namespace BsgoBot.World;

/// <summary>
/// One weapon discharge, from <c>Reply.WeaponShot</c>. <see cref="Target"/> is 0 when the shot
/// was aimed at nothing in particular.
/// </summary>
public readonly record struct ShotEvent(
    uint Shooter, ushort HardpointHash, uint Target, byte FxType, DateTime At);

/// <summary>
/// One hit involving us, from <c>Reply.CombatInfo</c>.
///
/// <see cref="Value"/> is signed exactly as the server sent it: <b>negative is damage, positive
/// is a repair</b> — the client branches on the sign to pick its log line. <see cref="FromMe"/>
/// separates damage we dealt from damage we took, and <see cref="Other"/> is the far end in
/// either direction.
/// </summary>
public readonly record struct CombatEvent(
    bool FromMe, uint Other, float Value, bool Destroyed, bool Critical, DateTime At)
{
    public bool IsDamage => Value < 0f;
    public bool IsRepair => Value > 0f;

    /// <summary>Magnitude with the sign removed — what you actually add up.</summary>
    public float Amount => Math.Abs(Value);
}

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

    /// <summary>
    /// The catalogue guid of what this object *is* — its model. From WhoIs's second guid
    /// (SpaceObject.BaseRead's objectGUID, the WorldCard guid).
    ///
    /// One guid, several views: the same number answers to CardView.Ship for stats and slots and
    /// to CardView.World for size and hardpoints. Two ships of the same class share it, which is
    /// what makes it the right key for anything learned about a class rather than an individual.
    /// </summary>
    public uint CardGuid;

    /// <summary>The guid of the owner card — faction/allegiance flavour, not the model.</summary>
    public uint OwnerCardGuid;

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

    /// <summary>
    /// The cargo hold as last stated, one stack per server id. Kept because Reply.HoldItems
    /// restates a stack's running TOTAL (client: Hold._AddItems replaces the stack), so the
    /// only way to know what a message actually earned is the rise against this.
    /// </summary>
    private readonly Dictionary<ushort, LootItem> _holdStacks = new();
    private DateTime _holdFirstAt = DateTime.MinValue;

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

    /// <summary>
    /// The sector we are in — or docked over, when the current scene is a room. Stated by
    /// Scene/LoadNextScene, the message that tells the client which level to load, and the only
    /// place the server ever names our own sector. Zero until the first scene change flows
    /// through the proxy, which means a bot attached mid-session does not know its sector until
    /// the next dock, jump or respawn.
    /// </summary>
    public uint CurrentSectorId { get; private set; }

    /// <summary>Raised when LoadNextScene names a sector, after <see cref="CurrentSectorId"/>
    /// is updated. Fires on every scene change, not only when the sector differs.</summary>
    public event Action<uint>? SectorIdentified;

    /// <summary>Hangar id of the ship I am flying (PlayerProtocol Reply.ActiveShip). Not the
    /// same number as <see cref="MyObjectId"/>, which is the sector object.</summary>
    public ushort MyShipId { get; private set; }

    public uint MyObjectId { get; private set; }
    public Vector3 MyPosition { get; private set; }
    public Vector3 MyVelocity { get; private set; }
    public bool MyPositionKnown { get; private set; }

    /// <summary>
    /// When the server last stated where MY ship actually is, as opposed to where dead reckoning
    /// thinks it has got to.
    ///
    /// The distinction matters because only some messages carry an absolute position — SyncMove,
    /// and the Rest/Teleport/Warp maneuvers. A normal approach is a stream of Directional
    /// maneuvers, which state a heading and a march speed and nothing else, so between fixes the
    /// ship's position is integrated rather than known. The integration is good for a second or
    /// two and drifts after that: it flies the ordered heading in a straight line at the ordered
    /// speed, while the real ship arcs through its turn and spends time accelerating.
    ///
    /// Anything deciding "am I in range" off a position this old is guessing, and the guess is
    /// what parks the ship out of reach of a rock it believes it is mining.
    /// </summary>
    public DateTime MyFixAt { get; private set; } = DateTime.MinValue;

    /// <summary>Seconds since the server last confirmed our position outright. Huge when we have
    /// no position at all, so callers can treat "unknown" and "long stale" the same way.</summary>
    public double MyFixAgeSeconds =>
        MyPositionKnown && MyFixAt != DateTime.MinValue
            ? (DateTime.UtcNow - MyFixAt).TotalSeconds
            : double.MaxValue;
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

    /// <summary>
    /// The death screen, as the server states it: every place we may respawn, as
    /// <c>(sectorId, carrierPlayerId)</c>. A carrier id of 0 means a station rather than a
    /// player's ship. Answer it with <c>SelectRespawnLocation</c> — until something does, the
    /// player stays dead, which is why the bot cannot simply wait for a hangar to appear.
    /// </summary>
    public event Action<IReadOnlyList<(uint SectorId, uint CarrierPlayerId)>>? RespawnOffered;

    /// <summary>Raised when the server restates a hangar ship's condition: (shipId, durability).</summary>
    public event Action<ushort, float>? ShipConditionChanged;

    /// <summary>
    /// The carrier we are riding, or 0 when we are flying our own ship.
    ///
    /// Anchoring is a third state, and the bot used to have no idea it existed: not the hangar,
    /// and not flying either. The client's own view of it is total — <c>Reply.Anchor</c> does
    /// <c>SetPlayerShip(carrier)</c>, i.e. the carrier BECOMES your ship — and it disables the
    /// ability bar and every flight control while it lasts.
    /// </summary>
    public uint AnchoredTo { get; private set; }

    public bool Anchored => AnchoredTo != 0;

    /// <summary>Raised when we anchor to a carrier (its object id) or come off one (0).</summary>
    public event Action<uint>? AnchorChanged;

    /// <summary>Raised when the server answers a scan for an asteroid. The reply is what
    /// identifies which of your abilities the scanner is — nothing else announces it.</summary>
    public event Action<uint>? ScanReceived;

    /// <summary>Raised when the server restates your ship's slots, so the loadout panel can
    /// pick up a refit without polling.</summary>
    public event Action? LoadoutChanged;

    /// <summary>
    /// Raised on every WhoIs that names a model: <c>(objectId, cardGuid, type)</c>.
    ///
    /// This is the hook the catalogue spy hangs on. A WhoIs arrives as soon as an object enters
    /// dradis range, which is long before it is a threat — so the card for a hull can be
    /// fetched and read while it is still a dot on the map.
    /// </summary>
    public event Action<uint, uint, SpaceEntityType>? ObjectIdentified;

    /// <summary>Every weapon discharge in the sector, ours and everyone else's.</summary>
    public event Action<ShotEvent>? ShotSeen;

    /// <summary>Every point of damage or repair we dealt or received, per hit.</summary>
    public event Action<CombatEvent>? CombatSeen;

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
        CargoAction = o.CargoAction, CardGuid = o.CardGuid, OwnerCardGuid = o.OwnerCardGuid,
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
                // The id match is still preferred, but only when the entry it names actually
                // describes something. Reply.AddShip creates a hangar entry per ship you own,
                // and Reply.Slots is not necessarily keyed by the same number Reply.ActiveShip
                // uses — so the id can match an entry that has no slots at all while the real
                // list sits under a different key. Reporting that empty entry as "your loadout"
                // is how every slot came back unfilled, which then made CastAllowed refuse to
                // fire the mining lasers.
                if (MyShipId != 0 && _hangar.TryGetValue(MyShipId, out var mine)
                    && mine.Slots().Any(s => s.Filled))
                    return mine;

                // Otherwise take the entry that carries the most fitted slots. A ship with
                // hardware in it is a far better guess at "the one I am flying" than a ship
                // with none, whatever the ids say.
                var best = _hangar.Values
                    .OrderByDescending(h => h.Slots().Count(s => s.Filled))
                    .ThenByDescending(h => h.Count)
                    .FirstOrDefault();

                if (best is not null && best.Slots().Any(s => s.Filled)) return best;

                // Nothing anywhere has a fitted slot. Fall back to the old rules so a server
                // that genuinely reports empty slots still produces a list to look at.
                if (MyShipId != 0 && _hangar.TryGetValue(MyShipId, out var named)) return named;
                return _hangar.Count == 1 ? _hangar.Values.First() : null;
            }
        }
    }

    /// <summary>Catalogue guid of the ship I am flying, from Reply.AddShip. This is the key to
    /// its card, and the card is where full durability is stated.</summary>
    public uint MyShipGuid
    {
        get
        {
            lock (_gate)
                return MyShipId != 0 && _hangar.TryGetValue(MyShipId, out var mine) ? mine.ShipGuid : 0u;
        }
    }

    /// <summary>Hull condition of the ship I am flying, in points, or null if the server has
    /// never sent a ShipInfo for it.</summary>
    public float? MyCondition
    {
        get
        {
            lock (_gate)
                return MyShipId != 0 && _hangar.TryGetValue(MyShipId, out var mine) ? mine.Durability : null;
        }
    }

    /// <summary>Every hangar entry and how much of it is filled — for diagnosing which ship the
    /// slot list actually landed under.</summary>
    public IReadOnlyList<(ushort ShipId, int Slots, int Filled)> HangarSummary()
    {
        lock (_gate)
            return _hangar.Values
                .Select(h => (h.ShipId, h.Count, h.Slots().Count(s => s.Filled)))
                .ToList();
    }

    /// <summary>
    /// The loadout only when the server's own ship id names it — never the best-guess fallback.
    ///
    /// <see cref="MyLoadout"/> deliberately guesses when the id does not resolve, because a panel
    /// showing the likeliest slot list beats a panel showing nothing. That guess is "the hangar
    /// entry with the most fitted slots", which on an account owning a Vanir and a Raptor is the
    /// Vanir whichever one is actually in space.
    ///
    /// <para>Fine for drawing. Not fine for <b>destroying</b> anything: a sweep that withdraws
    /// your declarations because the guid disagrees will withdraw every one of them when handed
    /// the wrong ship's slots, which is how a declared scanner — its role, its 4,000u reach —
    /// vanished and the bot went back to probing utility slots to find one.</para>
    ///
    /// <para>So anything that invalidates what you typed asks for this instead, and does nothing
    /// at all until the server has told us which ship we are in.</para>
    /// </summary>
    public ShipLoadout? ConfirmedLoadout
    {
        get
        {
            lock (_gate)
                return MyShipId != 0 && _hangar.TryGetValue(MyShipId, out var mine)
                    && mine.Slots().Any(s => s.Filled) ? mine : null;
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
                    MyFixAt = o.PositionStamp;
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
            case ProtocolId.Scene: OnScene(msgType, r); break;
        }
    }

    /// <summary>
    /// Scene/LoadNextScene, transcribed from the client's <c>SceneProtocol.ParseMessage</c>.
    /// Only the sector id is kept, and only the locations that state one are parsed — the
    /// payload's field order differs by location, so each case reads exactly what the client
    /// reads and nothing after the id.
    /// </summary>
    private void OnScene(ushort msgType, BgoReader r)
    {
        if ((SceneOp.Reply)msgType != SceneOp.Reply.LoadNextScene) return;

        // The one moment a sector change is BOTH certain and early enough. RemoveMe is not
        // always sent to us (hand jumps, cross-sector respawns), and the client's JumpIn is
        // too LATE: the server streams the new sector's every WhoIs while the loading screen
        // is still up — the client literally waits for those objects' cards before sending
        // JumpIn — so a clear there wipes the rocks that were just announced, and a static
        // rock never announces itself twice. This message precedes the whole stream, which
        // makes it the only safe wipe point for a sector change RemoveMe didn't cover.
        Clear();

        r.ReadByte();                             // TransSceneType
        var location = (GameLocation)r.ReadByte();

        uint sector = 0;
        switch (location)
        {
            // Room: GUID cardGuid, then uint32 sectorId — the sector the room's station sits
            // in, which is why the bot knows its sector even while docked.
            case GameLocation.Room:
                r.ReadUInt32();
                sector = r.ReadUInt32();
                break;

            // Space and its variants: uint32 sectorId first, then the sector's card guid.
            case GameLocation.Space:
            case GameLocation.Story:
            case GameLocation.BattleSpace:
            case GameLocation.Tournament:
            case GameLocation.Tutorial:
            case GameLocation.Teaser:
            case GameLocation.Zone:
                sector = r.ReadUInt32();
                break;
        }

        if (sector == 0) return;
        CurrentSectorId = sector;
        SectorIdentified?.Invoke(sector);
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
                // Broadcast to every client in the sector, not just the participants — so this
                // is a free, sector-wide record of who is shooting whom, including fights we
                // are not in. It is the closest thing on the wire to the server's own aggro
                // table, which is what NPC target selection actually runs on.
                uint shooter = r.ReadUInt32();
                ushort hardpoint = r.ReadUInt16();
                uint target = r.ReadUInt32();
                byte fx = r.HasMore ? r.ReadByte() : (byte)0;   // WeaponFxType
                Touch(shooter);
                if (target != 0) Touch(target);
                ShotSeen?.Invoke(new ShotEvent(shooter, hardpoint, target, fx, DateTime.UtcNow));
                break;
            }

            case GameOp.Reply.CombatInfo:
            {
                // Client: GameProtocol.Reply.CombatInfo. The float is SIGNED — negative is
                // damage, positive is a repair — and only ever concerns us: either we dealt it
                // or we took it. `fromMe` says which.
                bool fromMe = r.ReadBoolean();
                uint other = r.ReadUInt32();
                float value = r.ReadSingle();
                byte flags = r.ReadByte();
                bool destroyed = (flags & 1) != 0;
                bool critical = (flags & 2) != 0;

                Touch(other);
                CombatSeen?.Invoke(new CombatEvent(
                    fromMe, other, value, destroyed, critical, DateTime.UtcNow));
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

            // Two id lists of equal length, read as pairs — client: Reply.RespawnOptions builds
            // one RespawnLocationInfo per index. A mismatched pair of lists is the client's own
            // "invalid respawn location list" case, and is dropped here for the same reason.
            case GameOp.Reply.RespawnOptions:
            {
                var sectors = r.ReadUInt32List();
                var carriers = r.ReadUInt32List();
                if (sectors.Count == 0 || sectors.Count != carriers.Count) break;

                var options = new List<(uint, uint)>(sectors.Count);
                for (int i = 0; i < sectors.Count; i++) options.Add((sectors[i], carriers[i]));
                RespawnOffered?.Invoke(options);
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
            //
            // The catch: each entry carries the stack's running TOTAL, not the amount added.
            // Passing the raw counts downstream is how "mined" reached nine digits — a hold
            // carrying 200k tylium re-counted on every restatement. HoldDelta turns the
            // message into genuine gains before anyone accumulates it.
            case PlayerOp.Reply.HoldItems:
            {
                var gained = HoldDelta(ReadItemList(r));
                if (gained.Count > 0) HoldGained?.Invoke(gained);
                break;
            }

            // Stacks leaving the hold — an unload at a base, mostly. Not a negative gain,
            // but the snapshot must forget them, or the replacement stack the next mining
            // run creates under a fresh id would be measured against a ghost.
            case PlayerOp.Reply.RemoveHoldItems:
            {
                var ids = r.ReadUInt16List();
                lock (_gate) foreach (var id in ids) _holdStacks.Remove(id);
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

            // Client: Reply.ShipInfo -> HangarShip._SetDurability. One ship, one number, and the
            // only statement of hull condition anywhere on the wire.
            case PlayerOp.Reply.ShipInfo:
            {
                ushort id = r.ReadUInt16();
                float durability = r.ReadSingle();
                bool changed;
                lock (_gate)
                {
                    var ship = Hangar(id);
                    changed = ship.Durability is not { } was || Math.Abs(was - durability) > 0.01f;
                    ship.Durability = durability;
                }
                if (changed) ShipConditionChanged?.Invoke(id, durability);
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

            // Client: Reply.Anchor -> Game.Me.Anchored = true, AnchorTarget = carrier, and
            // SetPlayerShip(carrier). We are a passenger from here until told otherwise.
            case PlayerOp.Reply.Anchor:
            {
                uint carrier = r.ReadUInt32();
                bool changed;
                lock (_gate) { changed = AnchoredTo != carrier; AnchoredTo = carrier; }
                if (changed) AnchorChanged?.Invoke(carrier);
                break;
            }

            // Client: Reply.Unanchor -> our own ship id back, plus an UnanchorReason byte. Note
            // the reason is read but not kept: launched, timed out or killed, the fact that
            // matters here is identical — the ship is ours to fly again.
            case PlayerOp.Reply.Unanchor:
            {
                r.ReadUInt32();                       // our ship, handed back
                bool changed;
                lock (_gate) { changed = AnchoredTo != 0; AnchoredTo = 0; }
                if (changed) AnchorChanged?.Invoke(0);
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
            o.CardGuid = info.ObjectGuid;
            o.OwnerCardGuid = info.OwnerGuid;
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

                // A WhoIs about our own ship states where it is, exactly like a Rest maneuver
                // does, and it was being written to the object while MyPosition kept the older
                // reading. That is a free fix thrown away — and the one that arrives first after
                // a jump, when nothing else has said where we came out.
                if (o.IsMe || info.Id == MyObjectId)
                {
                    MyPosition = p;
                    MyVelocity = Vector3.Zero;
                    MyPositionKnown = true;
                    MyFixAt = o.PositionStamp;
                }
            }
        }

        if (becameMe) SetMe(info.Id);

        // Announced outside the lock: subscribers go and fetch catalogue cards, and holding the
        // world lock across that would put network work on the traffic-decoding path.
        if (info.ObjectGuid != 0) ObjectIdentified?.Invoke(info.Id, info.ObjectGuid, info.Type);
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
                // This is a fix, not an estimate: the position came off the wire rather than out
                // of the integrator. Only Locate stamps this — SetVelocity carries the position
                // forward itself, which is exactly the dead reckoning MyFixAt exists to distrust.
                MyFixAt = o.PositionStamp;
            }
        }
    }

    /// <summary>
    /// Which way our nose points, independent of whether we are moving.
    ///
    /// Facing used to be inferred from the velocity vector, which is fine while under way and
    /// useless the moment the ship stops — and stopped is exactly the state mining is done in.
    /// The server enforces a firing arc per weapon (Algorithm3D.isWeaponPositionInRange takes the
    /// ability's Angle), so a bot that cannot say where its nose is cannot tell an out-of-arc
    /// target from a rock that is not there. It concluded the latter and threw the rock away.
    ///
    /// Zero until a heading has been seen at all.
    /// </summary>
    public Vector3 MyFacing { get; private set; }

    /// <summary>Turns an Euler3 heading + march speed into a velocity estimate.</summary>
    private void SetHeading(uint id, Vector3 euler, float speed)
    {
        var forward = Forward(euler);
        if (id == MyObjectId || (_objects.TryGetValue(id, out var o) && o.IsMe))
        {
            // Kept whatever the speed. A Rest maneuver states a heading with a march speed of
            // zero, and that is still a statement about where the ship is pointing.
            lock (_gate) MyFacing = forward;
        }
        SetVelocity(id, forward * speed);
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
            if (id == MyObjectId) { MyPositionKnown = false; MyFixAt = DateTime.MinValue; }
        }
    }

    public void Clear()
    {
        bool wasAnchored;
        lock (_gate)
        {
            _objects.Clear();
            MyPositionKnown = false;
            MyFixAt = DateTime.MinValue;
            MyObjectId = 0;

            // Leaving the sector ends any anchoring with it — the carrier is not in the sector we
            // are going to. Kept in step here because the server's own Unanchor may arrive before
            // the removal, after it, or (on a death) not at all.
            wasAnchored = AnchoredTo != 0;
            AnchoredTo = 0;
        }
        if (wasAnchored) AnchorChanged?.Invoke(0);
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

    /// <summary>
    /// Converts a HoldItems restatement into what was actually GAINED, as per-item rises
    /// against the last known stack. The first messages after a login describe cargo carried
    /// in, not cargo earned, so everything inside the seed window only sets the baseline —
    /// mining cannot plausibly land anything that soon after a connect.
    /// </summary>
    private const double HoldSeedSeconds = 5.0;

    private List<LootItem> HoldDelta(List<LootItem> items)
    {
        var now = DateTime.UtcNow;
        var gained = new List<LootItem>();
        lock (_gate)
        {
            if (_holdFirstAt == DateTime.MinValue) _holdFirstAt = now;
            bool seeding = (now - _holdFirstAt).TotalSeconds < HoldSeedSeconds;
            foreach (var it in items)
            {
                long before = _holdStacks.TryGetValue(it.ServerId, out var known) ? known.Count : 0;
                _holdStacks[it.ServerId] = it;
                if (seeding) continue;
                long rise = it.Count - before;
                if (rise > 0) gained.Add(it with { Count = (uint)rise });
            }
        }
        return gained;
    }

    /// <summary>Forget the hold baseline. For a NEW connection only: server ids do not survive
    /// a login, so measuring the next session's hold against them would invent yield.</summary>
    public void ResetHoldTracking()
    {
        lock (_gate)
        {
            _holdStacks.Clear();
            _holdFirstAt = DateTime.MinValue;
        }
    }

    /// <summary>The hold as last stated by the server, one entry per stack.</summary>
    public List<LootItem> HoldSnapshot()
    {
        lock (_gate) return _holdStacks.Values.ToList();
    }

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
