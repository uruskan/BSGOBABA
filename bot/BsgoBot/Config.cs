using System.Text.Json;
using System.Text.Json.Serialization;
using BsgoBot.Bot;
using BsgoBot.Protocol;

namespace BsgoBot;

/// <summary>An ability the bot learned, remembered so "fire once manually" really is once.</summary>
public sealed class SavedWeapon
{
    public ushort AbilityId { get; set; }
    /// <summary>0 = cast per shot, 1 = toggle on and retarget.</summary>
    public int Kind { get; set; }
    /// <summary>0 = unknown, 1 = combat, 2 = mining.</summary>
    public int Role { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// One slot of your ship as you described it in the loadout panel.
///
/// Everything the bot can work out for itself is left out of this on purpose — ranges and
/// cooldowns are here only so a server that publishes no slot stats still has numbers to fly
/// on. What is genuinely yours to state is <see cref="Role"/> (what the thing is for) and
/// <see cref="Name"/> / <see cref="Category"/> (which hex it is, and what to call it).
/// </summary>
public sealed class SavedSlot
{
    /// <summary>
    /// Slot id, which is also the ability id used to fire it (client: ShipAbility.ServerID =>
    /// slot.ServerID). <b>-1</b> means the hex isn't bound to a slot yet.
    ///
    /// Not 0. Ability ids on this server start at zero — binding a real mining laser by firing
    /// it captures id 0 — and while 0 was the "unbound" marker that laser could not be bound,
    /// saved, or test-fired, because every check read it as "nothing here". A sentinel has to be
    /// a value the data cannot take.
    /// </summary>
    public int SlotId { get; set; } = -1;

    /// <summary>True once this hex points at a real ability id.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Bound => SlotId >= 0;

    public string Name { get; set; } = "";

    /// <summary><c>ShipSlotType</c> name: Gun, Launcher, Hull, Computer, Engine, Avionics…</summary>
    public string Category { get; set; } = "Undefined";

    public byte Level { get; set; }

    /// <summary><c>WeaponRole</c> name, or empty to let the bot work it out.</summary>
    public string Role { get; set; } = "";

    public float? MaxRange { get; set; }
    public float? OptimalRange { get; set; }
    public float? MinRange { get; set; }

    /// <summary>The card's Reload figure, in seconds.</summary>
    public float? Cooldown { get; set; }
    public float? PowerCost { get; set; }

    /// <summary>Which consumable is loaded, by name. Records what you picked in Switch Ammo.</summary>
    public string Ammo { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Where this sits in the hex layout: 1-4 are the hexes above the ship, 5-14 the
    /// ability bar along the bottom. 0 places it automatically from <see cref="Category"/>.</summary>
    public int Hex { get; set; }

    /// <summary>Catalogue guid the server last reported in this slot, so a refit is detectable —
    /// if the guid changes, what you typed no longer describes what is installed.</summary>
    public uint SystemGuid { get; set; }
}

/// <summary>A game server you can point the proxy at. Account lives here because
/// player ids and session codes are per-server, not per-client.</summary>
public sealed class ServerProfile
{
    public string Name { get; set; } = "New server";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 27060;
    public string PlayerId { get; set; } = "5085935";
    public string Session { get; set; } = "";
    public string Language { get; set; } = "en";

    /// <summary>
    /// How many hexes sit above the hull for weapons, which is how many gun slots this ship has.
    ///
    /// Stated, not detected: <c>Reply.Slots</c> carries a slot's id, its installed system guid and
    /// whether it is inoperable, and nothing at all about what KIND of slot it is — that lives in
    /// the catalogue, which the bot never reads. Hardcoding four numbered a three-gun ship's
    /// ability bar from 5.
    /// </summary>
    public int WeaponHexes { get; set; } = 4;

    /// <summary>Weapon ability ids are per-ship and per-server, so they live here.
    /// Hidden from the profiles grid — it's machine state, not something you type in.</summary>
    [System.ComponentModel.Browsable(false)]
    public List<SavedWeapon> Weapons { get; set; } = [];

    /// <summary>Your ship's loadout as you described it. Per-server for the same reason the
    /// weapons are: slot ids belong to a ship on a server, not to you.</summary>
    [System.ComponentModel.Browsable(false)]
    public List<SavedSlot> Slots { get; set; } = [];

    /// <summary>Parsed <see cref="PlayerId"/>, or 0 if it isn't a number.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public uint NumericPlayerId => uint.TryParse(PlayerId, out var v) ? v : 0u;

    public override string ToString() => $"{Name}  ({Host}:{Port})";
}
/// <summary>A game client install. Version is per-build, so it belongs to the client,
/// not the server.</summary>
public sealed class ClientProfile
{
    public string Name { get; set; } = "New client";
    public string Path { get; set; } = "";
    public string Version { get; set; } = "3b27980a3b7dd77e597872106ca98000";

    public override string ToString() => Name;
}

public sealed class Config
{
    /// <summary>Where the proxy listens. The client hardcodes 27050, so this stays 27050.</summary>
    public string ListenHost { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 27050;

    /// <summary>Start relaying as soon as the window opens, so you can't forget.</summary>
    public bool AutoStartProxy { get; set; } = true;

    /// <summary>
    /// Everything you can tune about how the farm loop behaves.
    ///
    /// This is the same object the running bot flies on — <c>MainForm</c> assigns it straight to
    /// <see cref="Bot.FarmBot.T"/> rather than copying it property by property, which is what
    /// this used to be: a parallel <c>BotSettings</c> class listing 47 of the bot's 77 settings,
    /// hand-copied across on every load. The other 30 were unreachable from this file entirely.
    /// </summary>
    public BotTuning Bot { get; set; } = new();

    public List<ServerProfile> Servers { get; set; } = [];
    public List<ClientProfile> Clients { get; set; } = [];

    public int SelectedServer { get; set; }
    public int SelectedClient { get; set; }

    /// <summary>Which of the centre views was open last: Map, Contacts or Loadout. Reopening
    /// on the one you were using beats reopening on the one that happens to be first.</summary>
    public string SelectedView { get; set; } = "Map";

    public ServerProfile? CurrentServer =>
        Servers.Count == 0 ? null : Servers[Math.Clamp(SelectedServer, 0, Servers.Count - 1)];

    public ClientProfile? CurrentClient =>
        Clients.Count == 0 ? null : Clients[Math.Clamp(SelectedClient, 0, Clients.Count - 1)];

    /// <summary>
    /// Where this instance's config lives. Defaults to bot.json next to the exe; overridden by
    /// <c>--config &lt;file&gt;</c> so a second instance (farming the other server) can run from
    /// the same build without the two fighting over one file. A relative path is resolved
    /// against the exe folder, not the shell's working directory.
    /// </summary>
    public static string FilePath { get; set; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "bot.json");

    /// <summary>
    /// How bot.json is read and written.
    ///
    /// The enum converter is not cosmetic. <see cref="BotTuning.Prey"/> and
    /// <see cref="BotTuning.WantedResources"/> are enum collections, and System.Text.Json
    /// writes enums as integers unless told otherwise — which would turn a readable
    /// <c>["Water", "Tylium"]</c> into <c>[3, 7]</c> and refuse to read the old file back.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Config Load()
    {
        Config cfg;
        try
        {
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOptions) ?? new Config()
                : new Config();
        }
        catch
        {
            cfg = new Config();
        }

        cfg.SeedDefaults();
        cfg.MigrateUnboundSlots();
        cfg.MigrateTuning();
        return cfg;
    }

    /// <summary>
    /// Moves settings whose MEANING changed, not just their default.
    ///
    /// Only exact old defaults are touched. A number you typed in yourself is a decision and
    /// stays put, even when the units under it have shifted — the alternative is a config file
    /// that silently rewrites your choices every time the code learns something.
    /// </summary>
    private void MigrateTuning()
    {
        // Was a distance from the rock's CENTRE, now a gap to its surface. 179 from the centre of
        // a typical rock is about 120 from its face, so an untouched default keeps its behaviour.
        if (Math.Abs(Bot.AsteroidStandoff - 179f) < 0.01f) Bot.AsteroidStandoff = 120f;

        // A flat 130u made a 38u rock into a 168u no-go sphere. The margin is for the ship, and
        // it is now floored by the ship's own size instead of guessing large.
        if (Math.Abs(Bot.CollisionMargin - 130f) < 0.01f) Bot.CollisionMargin = 70f;

        // Renamed when the settings moved onto BotTuning. Only an older file carries the old key.
        if (Bot.AutoRepairShip is { } legacyRepair)
        {
            Bot.AutoRepair = legacyRepair;
            Bot.AutoRepairShip = null;
        }

        // An older bot.json only ever held one resource. Promote it to a one-entry priority list
        // the first time, so the setting you picked keeps meaning what it did.
        if (Bot.WantedResources.Count == 0
            && Enum.TryParse<ResourceType>(Bot.WantedResource, out var legacyResource)
            && legacyResource != ResourceType.Any)
        {
            Bot.WantedResources.Add(legacyResource);
        }
        Bot.WantedResource = null;

        // Filtered against what a rock can actually hold: an earlier build offered cubits, uranium
        // and plutonium, so a saved list can still rank things that will never match. Left in
        // place they would occupy priority slots above resources that do exist.
        Bot.WantedResources.RemoveAll(r => !Resources.IsMinable(r));
    }

    /// <summary>
    /// Old profiles stored 0 for "this hex is not bound to anything". Zero is a real ability id,
    /// so that meaning has to be retired — but the two cases are indistinguishable in the file.
    /// A hex with a 0 and nothing else typed into it was certainly unbound; one that also
    /// carries a name or a role was described on purpose and keeps its id.
    /// </summary>
    private void MigrateUnboundSlots()
    {
        foreach (var server in Servers)
            foreach (var slot in server.Slots)
                if (slot.SlotId == 0 && slot.Name.Length == 0 && slot.Role.Length == 0)
                    slot.SlotId = -1;
    }

    /// <summary>Keeps a fresh install (or an old flat bot.json) usable without hand-editing.</summary>
    private void SeedDefaults()
    {
        if (Servers.Count == 0)
        {
            Servers.Add(new ServerProfile
            {
                Name = "Local C# server",
                Host = "127.0.0.1",
                Port = 27060,
                PlayerId = "5085935",
                // Blank on purpose. A session belongs to one account on one server, so a real
                // one baked into a seeded default is a credential in the source and useless to
                // anyone else. Fill it in under Profiles, or let Catch session write one.
                Session = "",
            });
            Servers.Add(new ServerProfile
            {
                Name = "Local bsgocore",
                Host = "127.0.0.1",
                Port = 27060,
                PlayerId = "1",
                Session = "preconfigured-session",
            });
        }

        if (Clients.Count == 0)
        {
            Clients.Add(new ClientProfile
            {
                Name = "BSGOFUN client",
                Path = @"C:\Program Files (x86)\BSGOFUN\client\live",
            });
        }
    }

    public void Save() =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
}
