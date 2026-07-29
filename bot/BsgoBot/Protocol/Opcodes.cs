namespace BsgoBot.Protocol;

/// <summary>
/// Transcribed verbatim from the 2019 client binary (Assembly-CSharp.dll).
/// The client is the authority: both server implementations must conform to these,
/// so these values stay valid regardless of which server we finish.
/// </summary>
public enum ProtocolId : byte
{
    Login = 0,
    Universe = 1,
    Game = 2,
    Sync = 3,
    Player = 4,
    Debug = 5,
    Catalogue = 6,
    Ranking = 7,
    Story = 8,
    Scene = 9,
    Room = 10,
    Community = 11,
    Shop = 12,
    Setting = 13,
    Ship = 14,
    Dialog = 15,
    Market = 16,
    Notification = 17,
    Subscribe = 18,
    Feedback = 19,
    Tournament = 20,   // obsolete, folded into Zone
    Arena = 21,
    Battlespace = 22,
    Wof = 23,
    Zone = 24,
}

/// <summary>How the server should authenticate us. DebugPlayerId needs only a player id.</summary>
public enum ConnectType : byte
{
    Web = 0,
    DebugPlayerId = 1,
    DebugName = 2,
    DebugNew = 3,
    DebugResetByPlayerId = 4,
}

public static class LoginOp
{
    public enum Request : ushort
    {
        Init = 1,
        Player = 2,
        Echo = 5,
    }

    public enum Reply : ushort
    {
        Hello = 0,
        Init = 1,
        Error = 2,
        Player = 3,
        Wait = 4,
        Echo = 5,
    }
}

public static class GameOp
{
    public enum Request : ushort
    {
        WhoIs = 3,
        SubscribeInfo = 10,
        UnSubscribeInfo = 11,
        MoveToDirection = 12,
        MoveToDirectionWithoutRoll = 13,
        CastSlotAbility = 21,
        CastImmutableSlotAbility = 22,
        LockTarget = 25,
        Wasd = 29,
        Qweasd = 30,
        Mining = 35,
        Loot = 41,
        TakeLootItems = 43,
        Dock = 45,
        Jump = 46,
        AnsStartQueue = 48,
        AnsJump = 50,
        Quit = 54,
        SetSpeed = 56,
        SetGear = 57,
        JumpIn = 61,
        MoveInfo = 63,
        StopJump = 65,
        SelectRespawnLocation = 70,
        GroupJump = 72,
        StopGroupJump = 73,
        RequestJumpToTarget = 75,
        CompleteJump = 76,
        RequestUnanchor = 77,
        RequestAnchor = 78,
        RequestLaunchStrikes = 79,
        CancelMiningRequest = 82,
        RequestJumpToBeacon = 85,
        ToggleAbilityOn = 86,
        ToggleAbilityOff = 87,
        UpdateAbilityTargets = 88,
        GroupJumpToBeacon = 89,
        TurnToDirectionStrikes = 100,
        TurnByPitchYawStrikes = 101,
        CancelDocking = 102,
        GroupJumpToTarget = 103,
        CargoInteraction = 106,
    }

    public enum Reply : ushort
    {
        Info = 2,
        WhoIs = 4,
        Move = 6,
        ObjectLeft = 7,
        WeaponShot = 13,
        MissileDecoyed = 18,
        SyncMove = 20,
        Cast = 22,
        StopSlotAbility = 24,
        Scan = 34,
        CombatInfo = 40,
        AskStartQueue = 47,
        AskJump = 49,
        Collide = 55,
        FtlCharge = 58,
        VirusBlocked = 59,
        RemoveMe = 69,
        TimeOrigin = 70,
        StopGroupJump = 76,
        LeaderStopGroupJump = 77,
        NotEnoughTylium = 81,
        UpdateRoles = 83,
        StopJump = 86,
        ChangeVisibility = 87,
        UpdateFactionGroup = 88,
        MineField = 90,
        ObjectState = 91,
        FlareReleased = 92,
        LostAbilityTarget = 93,
        LostJumpTransponder = 94,
        DockingDelay = 95,
        ChangedPlayerSpeed = 96,
        ShortCircuitResult = 97,
        OutpostStateBroadcast = 98,
        RespawnOptions = 99,
        AnchorDeclined = 100,
        DetachedToSpace = 104,
        RetachedToSpace = 105,
        CargoInteraction = 106,
    }
}

/// <summary>
/// Room protocol (id 10) — the hangar you stand in, not the sector you fly in.
///
/// <c>Quit</c> is what the UNDOCK button actually sends (client: <c>UndockButton.Undock</c>).
/// Leaving the room is what makes the server put the ship back in space; the client then loads
/// the space level and sends <c>Game.JumpIn</c> itself once its cards are ready
/// (<c>SpaceLevel.Preload</c>). Injecting JumpIn from a hangar therefore does nothing at all —
/// it is the second half of a sequence whose first half never happened.
/// </summary>
public static class RoomOp
{
    public enum Request : ushort
    {
        Talk = 0,
        NpcMarks = 2,
        EnterDoor = 4,
        Quit = 5,
        Enter = 6,
    }
}

/// <summary>Player protocol (id 4). Only the replies the bot actually consumes.</summary>
public static class PlayerOp
{
    /// <summary>
    /// The hangar-side requests the bot sends. Transcribed from the client's PlayerProtocol —
    /// these are hangar actions, so they are legal only while docked.
    /// </summary>
    public enum Request : ushort
    {
        /// <summary>containerId, systemServerId, repairValue, useCubits.</summary>
        RepairSystem = 11,
        /// <summary>shipId, repairValue (points of durability to buy back), useCubits.</summary>
        RepairShip = 12,
        /// <summary>shipId, useCubits. The damage window's "repair all" — hull and every
        /// system in one message, which is what a death needs.</summary>
        RepairAll = 26,
    }

    public enum Reply : ushort
    {
        Reset = 1,
        PlayerInfo = 2,
        ShipInfo = 11,
        /// <summary>The whole loadout of one hangar ship: which slot holds which system, which
        /// consumable is loaded into it, and whether it is broken. Client: PlayerProtocol
        /// Reply.Slots -> ShipSlot.Read. This is the only message that states the slot list
        /// outright — everything else infers it from stats or from watching you fire.</summary>
        Slots = 12,
        AddShip = 15,
        RemoveShip = 16,
        ActiveShip = 17,
        ShipName = 19,
        ID = 22,
        Name = 23,
        Faction = 24,
        Level = 27,
        /// <summary>Items added to your cargo hold, as an ItemList. This is the authoritative
        /// "ore just landed" signal — the server states what you actually earned, so it needs no
        /// guess about who broke the rock. Client: Game.Me.Hold._AddItems.</summary>
        HoldItems = 7,
        RemoveHoldItems = 8,
        Loot = 30,
        RemoveLootItems = 31,
        Stats = 32,
        Anchor = 52,
        Unanchor = 53,
    }
}

/// <summary>
/// Subscribe protocol (id 18). The client asks for a player's details whenever one shows up
/// in the sector, so these replies stream past the proxy for free — which is where real
/// player names come from. Nothing in Reply.WhoIs carries a name.
/// </summary>
public static class SceneOp
{
    public enum Request : ushort
    {
        SceneLoaded = 1,
        Disconnect = 2,
        /// <summary>The "cancel logout" button: stops a server-side disconnect countdown.</summary>
        StopDisconnect = 3,
        QuitLogin = 4,
    }

    public enum Reply : ushort
    {
        /// <summary>The server telling the client which level to load next. For a space level
        /// the payload names the sector id — the only place our own sector is ever stated.</summary>
        LoadNextScene = 1,
        /// <summary>A logout countdown has started (float seconds left). The client shows a
        /// timer with a cancel button; when it runs out, Disconnect follows and the client
        /// quits itself — which from the outside looks exactly like a crash.</summary>
        DisconnectTimer = 2,
        Disconnect = 100,
    }
}

/// <summary>Client <c>GameLocation</c> — the second byte of Scene/LoadNextScene.</summary>
public enum GameLocation : byte
{
    Unknown = 0,
    Space = 1,
    Room = 2,
    Story = 3,
    Disconnect = 4,
    Arena = 5,
    BattleSpace = 6,
    Tournament = 7,
    Tutorial = 8,
    Teaser = 9,
    Avatar = 10,
    Starter = 11,
    Zone = 12,
}

public static class SubscribeOp
{
    public enum Reply : ushort
    {
        PlayerName = 1,
        PlayerFaction = 2,
        PlayerAvatar = 3,
        PlayerShips = 4,
        PlayerStatus = 5,
        PlayerLocation = 6,
        PlayerLevel = 7,
        PlayerGuild = 8,
        PlayerStats = 9,
        PlayerTitle = 10,
        PlayerMedal = 11,
        PlayerLogout = 12,
    }
}

/// <summary>
/// What kind of hardware a slot takes. Client: ShipSlotType, carried in the ship's catalogue
/// card rather than on the wire — so the bot never learns it and you type it in instead. The
/// values still match the client's, in case a future message does publish them.
/// </summary>
public enum ShipSlotType : byte
{
    Undefined = 0,
    Computer = 1,
    Engine = 2,
    Hull = 3,
    Weapon = 4,
    ShipPaint = 5,
    Avionics = 6,
    Launcher = 7,
    DefensiveWeapon = 8,
    Gun = 9,
    Role = 10,
    SpecialWeapon = 11,
}

public static class SlotTypes
{
    /// <summary>Client: ShipSlot.IsWeaponSlot. These are the hexes above the ship; everything
    /// else lands on the ability bar along the bottom.</summary>
    public static bool IsWeapon(ShipSlotType t) =>
        t is ShipSlotType.Weapon or ShipSlotType.Launcher or ShipSlotType.Gun
          or ShipSlotType.DefensiveWeapon or ShipSlotType.SpecialWeapon;
}

/// <summary>SpaceSubscribeInfo.Read tag byte. Drives the whole stats stream.</summary>
public enum StatUpdateType : byte
{
    Unknown = 0,
    Stat = 1,
    Buff = 2,
    Combat = 3,
    Target = 4,
    RemoveBuff = 5,
    PowerPoints = 6,
    HullPoints = 7,
    Reset = 9,
    SlotStat = 12,
    ShipAspects = 13,
    ToggleBuff = 14,
    RemoveToggleBuff = 15,
    StatsModifier = 16,
    RemoveStatsModifier = 17,
    ShortCircuit = 18,
    RemoveShortCircuit = 19,
    CaptureStatus = 20,
    SectorModifier = 21,
    RemoveSectorModifier = 22,
    VitalPoints = 23,
}

/// <summary>
/// The subset of ObjectStat the bot reasons about. Full enum is 150+ entries;
/// these are the ones that decide targeting, range and cadence.
/// </summary>
public enum ObjectStat : ushort
{
    None = 0,
    /// <summary>Attacker's side of the hit-chance roll, weighed against the target's Avoidance.</summary>
    Accuracy = 2,
    DamageHigh = 3,
    DamageLow = 4,
    PenetrationStrength = 6,
    ArmorPiercing = 7,
    DamageMining = 8,
    /// <summary>Defender's side of the hit-chance roll. Scaled down by the target's own
    /// throttle before use, so a ship sitting still is at its most hittable.</summary>
    Avoidance = 12,
    /// <summary>How much Avoidance survives at zero throttle: the multiplier floors at
    /// <c>1 - AvoidanceFading</c> rather than at zero.</summary>
    AvoidanceFading = 13,
    ArmorValue = 15,
    CriticalDefense = 16,
    Speed = 25,
    BoostSpeed = 26,
    /// <summary>Degrees per second the hull turns at. What decides whether a deflection can
    /// actually be flown before reaching the thing it is meant to miss.</summary>
    TurnSpeed = 32,
    MaxHullPoints = 33,
    HullRecovery = 34,
    MaxPowerPoints = 35,
    PowerRecovery = 36,
    DradisRange = 37,
    MapRange = 38,
    /// <summary>How far one FTL jump reaches, in galaxy-map units. The client's CanJump test
    /// compares this directly against the star-to-star distance, no scaling.</summary>
    FtlRange = 41,
    OptimalRange = 46,
    MaxRange = 47,
    MinRange = 48,
    Angle = 49,
    PowerPointCost = 50,
    Cooldown = 53,
    MissileDamageHigh = 60,
    MissileDamageLow = 61,
    MissileMaxRange = 67,
    MissileMinRange = 68,
    MissileAngle = 69,
    MissilePowerPointCost = 70,
    MissileCooldown = 71,
    CannonAccuracy = 73,
    CannonDamageHigh = 74,
    CannonDamageLow = 75,
    CannonOptimalRange = 80,
    CannonMaxRange = 81,
    CannonMinRange = 82,
    CannonAngle = 83,
    CannonPowerPointCost = 84,
    CannonCooldown = 85,
    MiningAccuracy = 86,
    MiningDamageHigh = 87,
    MiningDamageLow = 88,
    MiningOptimalRange = 91,
    MiningMaxRange = 92,
    MiningMinRange = 93,
    MiningAngle = 94,
    MiningPowerPointCost = 95,
    MiningCooldown = 96,
    Signature = 152,
    Detection = 153,
    /// <summary>DRADIS ring. Client: DradisHelper.IsInDetectorsDradisRange.</summary>
    DetectionInnerRadius = 154,
    /// <summary>Map ring. Client: DradisHelper.IsInDetectorsMapRange.</summary>
    DetectionOuterRadius = 155,
    /// <summary>Inside this, an object is drawn even when cloaked.</summary>
    DetectionVisualRadius = 157,
    MaxVitalPoints = 265,
    CargoHoldVolume = 290,
}

public enum ManeuverType : byte
{
    Pulse = 0,
    Teleport = 1,
    Rest = 2,
    Warp = 3,
    Directional = 4,
    Launch = 5,
    Rotation = 6,
    Flip = 7,
    Turn = 8,
    Follow = 9,
    DirectionalWithoutRoll = 10,
    TurnQweasd = 11,
    TurnToDirectionStrikes = 12,
    TurnByPitchYawStrikes = 13,
    TargetLaunch = 14,
}

public enum CreatingCause : byte
{
    AlreadyExists = 0,
    JumpIn = 1,
}

public enum Faction : byte
{
    Neutral = 0,
    Colonial = 1,
    Cylon = 2,
    Ancient = 3,
}

public enum FactionGroup : byte
{
    Group0 = 0,
    Group1 = 1,
}

public enum Relation
{
    Friend = 1,
    Enemy = 2,
    Neutral = 3,
    Self = 4,
}

/// <summary>CargoObject.Interaction in the client.</summary>
public enum CargoInteraction : byte
{
    None = 0,
    Pickup = 1,
    Dropoff = 2,
    Loot = 3,
}

/// <summary>
/// Card GUIDs of the minable resources, from the client's ResourceType enum. Reply.Scan
/// names an asteroid's contents with one of these, which is how the bot can be told to
/// mine tylium and walk past titanium.
/// </summary>
public enum ResourceType : uint
{
    Any = 0,
    Titanium = 207047790,
    Tylium = 215278030,
    Water = 130762195,
    Cubits = 264733124,
    Plutonium = 63148366,
    Uranium = 172582782,
}

public static class Resources
{
    /// <summary>
    /// What asteroids actually hold. The enum above is a DECODE table — every resource guid the
    /// client knows about, so a scan reporting any of them can still be named rather than printed
    /// as a raw number. This is the shorter list of what a rock can genuinely contain, and it is
    /// the only thing worth offering as a mining filter.
    ///
    /// Cubits are premium currency and are not mined; uranium and plutonium are not asteroid
    /// resources at all. Offering them meant a priority slot that could never match — and, worse,
    /// silently ranked above one that could.
    /// </summary>
    public static readonly ResourceType[] Minable =
        [ResourceType.Tylium, ResourceType.Water, ResourceType.Titanium];

    public static bool IsMinable(ResourceType r) => Array.IndexOf(Minable, r) >= 0;

    /// <summary>
    /// What one unit of ore is worth in cubits, for pricing a farm run in one currency.
    ///
    /// Exchange arithmetic, not market data: water sells for 0.2 cubits a unit outright;
    /// tylium has no sale price, but 1 cubit buys 10 tylium, so a mined unit is worth the
    /// 0.1 cubits it saves; titanium trades 2:1 into tylium, so half of tylium's rate.
    /// </summary>
    public static double CubitsPerUnit(ResourceType r) => r switch
    {
        ResourceType.Water => 0.2,
        ResourceType.Tylium => 0.1,
        ResourceType.Titanium => 0.05,
        _ => 0.0,
    };
}

/// <summary>ItemFactory.ItemType — the tag byte in front of every serialised item.</summary>
public enum ItemType : byte
{
    None = 0,
    System = 1,
    Countable = 2,
    Starter = 3,
    Ship = 4,
}

/// <summary>
/// An object's kind is encoded in the high bits of its server id:
///     type = objectId &amp; 0x1F000000
/// So we can classify every object from its id alone, with no per-type payload parsing.
/// (Client: SectorFactory.CreateSpaceObject)
/// </summary>
public enum SpaceEntityType : uint
{
    Player = 0x01000000,
    Missile = 0x02000000,
    WeaponPlatform = 0x03000000,
    Cruiser = 0x04000000,
    BotFighter = 0x05000000,
    Debris = 0x06000000,
    Asteroid = 0x07000000,
    CargoObject = 0x08000000,
    MiningShip = 0x09000000,
    Outpost = 0x0A000000,
    AsteroidBot = 0x0B000000,
    Trigger = 0x0C000000,
    Planet = 0x0D000000,
    Planetoid = 0x0E000000,
    Mine = 0x0F000000,
    Volume = 0x10000000,
    JumpBeacon = 0x11000000,
    SectorEvent = 0x12000000,
    MineField = 0x13000000,
    JumpTargetTransponder = 0x14000000,
    Comet = 0x15000000,
    SmartMine = 0x16000000,
    CaptureTrigger = 0x17000000,
}

public static class EntityTypes
{
    public const uint TypeMask = 0x1F000000;
    public const uint FactionMask = 0xC0000000;
    public const uint GroupMask = 0x20000000;

    public static SpaceEntityType Of(uint objectId) => (SpaceEntityType)(objectId & TypeMask);

    /// <summary>Client: SpaceObject.ExtractFaction.</summary>
    public static Faction FactionOf(uint objectId) => (objectId & FactionMask) switch
    {
        0xC0000000 => Faction.Ancient,
        0x40000000 => Faction.Colonial,
        0x80000000 => Faction.Cylon,
        _ => Faction.Neutral,
    };

    /// <summary>Client: SpaceObject.ExtractFactionGroup.</summary>
    public static FactionGroup GroupOf(uint objectId) =>
        (objectId & GroupMask) == 0 ? FactionGroup.Group0 : FactionGroup.Group1;

    /// <summary>
    /// Client: RelationHelper.GetRelation, with TargetBracketMode.Default.
    /// Neutral on either side wins; otherwise same faction AND same group is a friend.
    /// </summary>
    public static Relation RelationTo(uint objectId, Faction myFaction, FactionGroup myGroup)
    {
        var f = FactionOf(objectId);
        if (myFaction == Faction.Neutral || f == Faction.Neutral) return Relation.Neutral;
        if (f == myFaction && GroupOf(objectId) == myGroup) return Relation.Friend;
        return Relation.Enemy;
    }

    /// <summary>Ship-shaped things: they take damage and drop loot.</summary>
    public static bool IsShip(uint id)
    {
        var t = Of(id);
        return t is SpaceEntityType.Player or SpaceEntityType.BotFighter or SpaceEntityType.AsteroidBot
            or SpaceEntityType.MiningShip or SpaceEntityType.Cruiser or SpaceEntityType.Outpost
            or SpaceEntityType.WeaponPlatform;
    }

    /// <summary>NPC combatants — the default farm diet, players excluded.</summary>
    public static bool IsNpcCombatant(uint id)
    {
        var t = Of(id);
        return t is SpaceEntityType.BotFighter or SpaceEntityType.AsteroidBot
            or SpaceEntityType.MiningShip or SpaceEntityType.Cruiser;
    }

    public static bool IsMinable(uint id) => Of(id) == SpaceEntityType.Asteroid;

    /// <summary>
    /// Things you can plausibly dock at. Whether a given one really is dockable, and from how
    /// far, lives in its OwnerCard, which the bot doesn't read — so this only narrows the search
    /// and the approach does the rest.
    /// </summary>
    public static bool IsDockable(uint id) =>
        Of(id) is SpaceEntityType.Outpost or SpaceEntityType.Cruiser;

    public static bool IsLootable(uint id) =>
        Of(id) is SpaceEntityType.CargoObject or SpaceEntityType.Debris;

    /// <summary>
    /// Things with a hull you can fly into. Rocks and stations are the ones that matter — they
    /// are large, they never move out of the way, and the server resolves the overlap as damage
    /// rather than as a push. Ships are left out on purpose: they are small, they manoeuvre, and
    /// treating every passing fighter as an obstacle would have the bot dodging its own targets.
    /// </summary>
    public static bool IsSolid(uint id) =>
        Of(id) is SpaceEntityType.Asteroid or SpaceEntityType.Planetoid or SpaceEntityType.Planet
            or SpaceEntityType.Comet or SpaceEntityType.Outpost or SpaceEntityType.WeaponPlatform
            or SpaceEntityType.Cruiser;

    /// <summary>Objects whose position arrives once, in the WhoIs payload, and never moves after.</summary>
    public static bool IsStatic(uint id) =>
        Of(id) is SpaceEntityType.Asteroid or SpaceEntityType.Planetoid or SpaceEntityType.Planet
            or SpaceEntityType.Debris or SpaceEntityType.CargoObject or SpaceEntityType.Trigger
            or SpaceEntityType.Volume or SpaceEntityType.CaptureTrigger;
}

/// <summary>Why an object left the sector. Death/Hit/Collected carry a trailing uint32.</summary>
public enum RemovingCause : byte
{
    Disconnection = 1,
    Death = 2,
    JumpOut = 3,
    Ttl = 4,
    Dock = 5,
    Hit = 6,
    JustRemoved = 7,
    Collected = 8,
}

/// <summary>ShipControlsBase.SpeedMode in the client. Sent as a single byte.</summary>
public enum SpeedMode : byte
{
    Abs = 0,
    Delta = 1,
    Stop = 2,
    Full = 3,
}

/// <summary>Client's Gear enum. Sent as a single byte.</summary>
public enum Gear : byte
{
    Regular = 0,
    Boost = 1,
    Rcs = 2,
}
