namespace BsgoBot.Cards;

/// <summary>
/// CatalogueProtocol message types. Transcribed from the client's
/// <c>CatalogueProtocol.Request</c> / <c>CatalogueProtocol.Reply</c>.
/// </summary>
public static class CatalogueOp
{
    public enum Request : ushort
    {
        Card = 1,
    }

    public enum Reply : ushort
    {
        Card = 2,
    }
}

/// <summary>
/// Which *view* of a card is being asked for. One card guid has many views: a ship's guid
/// answers to <see cref="Ship"/> for its stats and slots, and to <see cref="World"/> for its
/// model, radius and hardpoint geometry.
///
/// Transcribed from the client's <c>CardView</c>. The full enum is listed so an unparsed view
/// still gets a readable name in the diagnostics; only a few are decoded (see
/// <see cref="CardReader"/>).
/// </summary>
public enum CardView : ushort
{
    GUI = 1,
    ShipSystem = 2,
    ShipConsumable = 3,
    World = 4,
    Global = 5,
    ShipAbility = 6,
    Counter = 7,
    Skill = 8,
    Ship = 10,
    Sector = 11,
    Starter = 13,
    Room = 14,
    Mission = 16,
    Reward = 18,
    Title = 19,
    Duty = 20,
    AvatarCatalogue = 21,
    Module = 22,
    Price = 23,
    Missile = 24,
    ShipList = 25,
    StickerList = 26,
    Movement = 28,
    Owner = 29,
    GalaxyMap = 30,
    Camera = 31,
    MailTemplate = 32,
    StarterKit = 34,
    ShipPaint = 35,
    Regulation = 36,
    ShipSale = 37,
    SectorEvent = 38,
    Tournament = 39,
    MapPart = 40,
    MapPartSet = 41,
    ShipLight = 42,
    EventShop = 43,
    GlobalBonusEvent = 44,
    Banner = 45,
    ConversionCampaign = 46,
    Zone = 47,
}

/// <summary>
/// Card guids the client has hardcoded, from <c>StaticCards</c>. These are the roots: asking
/// for a ship list yields every ship card guid in the game, which is why the whole player-ship
/// catalogue can be fetched with two requests instead of one per ship encountered.
/// </summary>
public static class RootCards
{
    public const uint ColonialShipList = 73551268u;
    public const uint CylonShipList = 188756164u;
    public const uint Global = 49842157u;
    public const uint GalaxyMap = 150576033u;
    public const uint StickerList = 166885587u;
}

/// <summary>
/// Slot kinds, from the client's <c>ShipSlotType</c>. This is the distinction the loadout panel
/// exists to collect by hand — it arrives free in every <see cref="CardView.Ship"/> reply.
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

/// <summary>Client <c>ShipRole</c>. A ship card carries several.</summary>
public enum ShipRole : byte
{
    Fighter = 1,
    Bomber = 2,
    Command = 3,
    ElectronicWarfare = 4,
    Engineer = 5,
    Interceptor = 6,
    Gunship = 7,
    Picket = 8,
    Destroyer = 9,
    Artillery = 10,
    Assault = 11,
    Stealth = 12,
    Carrier = 13,
    Mothership = 14,
}

/// <summary>
/// Client <c>ShipAbilityAffect</c> — how a cast picks its targets.
///
/// Note <see cref="Selected"/> is <b>0</b>, not a "none" value: the safe single-target mode is
/// the enum's default. The server refuses a Selected cast carrying more than one id and logs it
/// as cheating, so this field decides whether an area batch is legal.
/// </summary>
public enum ShipAbilityAffect : byte
{
    Selected = 0,
    Ignore = 1,
    Area = 2,
    MultiWeaponTarget = 3,
}

/// <summary>
/// Client <c>AbilityActionType</c> — what an ability actually does. This is the authoritative
/// answer to "is this slot a gun, a scanner, a repair or a flare", which the bot currently
/// infers from stat shape.
/// </summary>
public enum AbilityActionType : byte
{
    None = 0,
    FireMissle = 1,
    FireCannon = 2,
    DropFlare = 3,
    Buff = 4,
    RestoreBuff = 5,
    ResourceScan = 6,
    Debuff = 7,
    FireMining = 8,
    Flak = 9,
    PointDefence = 10,
    DispellVirus = 11,
    Follow = 12,
    ManeuverFlip = 13,
    Slide = 14,
    ActivatePaintTheTarget = 15,
    FollowFriend = 16,
    ActivateJumpTargetTransponder = 17,
    ToggleStealth = 18,
    FireTorpedo = 19,
    ToggleSystem = 20,
    FireLightMissile = 21,
    FireHeavyMissile = 22,
    FireShotgun = 23,
    FireKillCannon = 24,
    FireMachineGun = 25,
    Fortify = 26,
    DevBuff = 27,
    ShortCircuit = 28,
    DropAntiStealthMine = 29,
    DeflectMissile = 30,
}

/// <summary>Client <c>SpotType</c> — what a hardpoint on the hull is for.</summary>
public enum SpotType : byte
{
    Weapon = 1,
    Sticker = 2,
    Mining = 3,
    Door = 4,
}

/// <summary>
/// Client <c>ShipConsumableOption</c>. Beware the ordering: <see cref="Using"/> is 1 and
/// <see cref="NotUsing"/> is 2, which is the opposite of the reading order the names suggest.
/// Only <see cref="Using"/> makes the server check and decrement ammo.
/// </summary>
public enum ShipConsumableOption : byte
{
    Undefined = 0,
    Using = 1,
    NotUsing = 2,
    Optional = 3,
}
