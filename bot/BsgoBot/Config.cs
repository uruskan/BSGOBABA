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

/// <summary>
/// One of your ships, and everything that is true about flying it.
///
/// Ships disagree about nearly every number the bot has. A Raptor wants to be shoved out of the
/// way of every rock it passes; a Vanir carries 4,500 hull and regenerates 35 a second, so the
/// same rock is cheaper hit than avoided. A Gopher's optimal range is 250u and a Badger's is 688.
/// One of those settings being right has always meant the other being wrong, because there was
/// only ever one of each.
///
/// <para>So the tuning, the slot descriptions and the learned ability ids travel together under a
/// name you pick, and switching ship is picking a different name. Nothing about it is automatic —
/// the server does tell us which ship is active, but a wrong guess here silently flies the wrong
/// ship's settings, which is worse than a dropdown.</para>
/// </summary>
public sealed class ShipProfile
{
    public string Name { get; set; } = "New ship";

    /// <summary>Everything about how this ship is flown. See <see cref="BotTuning"/>.</summary>
    public BotTuning Tuning { get; set; } = new();

    /// <summary>Ability ids the bot learned for this ship. They are per-ship, not per-account:
    /// slot 4 on a Vanir and slot 4 on a Raptor are different guns.</summary>
    public List<SavedWeapon> Weapons { get; set; } = [];

    /// <summary>This ship's loadout as you described it in the panel.</summary>
    public List<SavedSlot> Slots { get; set; } = [];

    /// <summary>How many hexes sit above the hull for weapons, which is how many gun slots this
    /// ship has. A Vanir has eight; a Raptor has four.</summary>
    public int WeaponHexes { get; set; } = 4;

    public override string ToString() => Name;
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

    /// <summary>The ships you fly on this server. Never empty after a load — see
    /// <see cref="CurrentShip"/>.</summary>
    [System.ComponentModel.Browsable(false)]
    public List<ShipProfile> Ships { get; set; } = [];

    /// <summary>Which entry of <see cref="Ships"/> is being flown. Yours to set; nothing
    /// changes it by itself.</summary>
    [System.ComponentModel.Browsable(false)]
    public int SelectedShip { get; set; }

    /// <summary>
    /// The ship currently selected, creating one if the list is somehow empty.
    ///
    /// Never returns null, because every caller would otherwise need a fallback and they would
    /// not all pick the same one — a half-configured bot flying on defaults it never announced
    /// is exactly the failure this whole structure exists to prevent.
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public ShipProfile CurrentShip
    {
        get
        {
            if (Ships.Count == 0) Ships.Add(new ShipProfile { Name = "Ship 1" });
            if (SelectedShip < 0 || SelectedShip >= Ships.Count) SelectedShip = 0;
            return Ships[SelectedShip];
        }
    }

    // ---- pre-ship-profile bot.json, migrated on load and then dropped ------------------

    /// <summary>What <see cref="ShipProfile.WeaponHexes"/> was before ships had profiles.</summary>
    [System.ComponentModel.Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WeaponHexes { get; set; }

    /// <summary>What <see cref="ShipProfile.Weapons"/> was before ships had profiles.</summary>
    [System.ComponentModel.Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SavedWeapon>? Weapons { get; set; }

    /// <summary>What <see cref="ShipProfile.Slots"/> was before ships had profiles.</summary>
    [System.ComponentModel.Browsable(false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SavedSlot>? Slots { get; set; }

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
    /// The tuning of the ship you are currently flying, which is the object the running bot flies
    /// on — <c>MainForm</c> assigns it straight to <see cref="Bot.FarmBot.T"/> rather than copying
    /// it property by property.
    ///
    /// <para>Computed, not stored. It lives on the selected <see cref="ShipProfile"/>, because one
    /// global set of numbers cannot be right for a strike ship and a line ship at once — which is
    /// what it used to be, and why tuning the Vanir quietly detuned the Raptor.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BotTuning Tuning => CurrentServer?.CurrentShip.Tuning ?? _orphanTuning;

    /// <summary>Somewhere for the settings to live when no server profile is selected, so the UI
    /// is still constructible instead of needing a null check on every binding.</summary>
    private readonly BotTuning _orphanTuning = new();

    /// <summary>The single global tuning bot.json held before ships had profiles. Folded into the
    /// first ship on load and then dropped from the file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BotTuning? Bot { get; set; }

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
        cfg.MigrateToShipProfiles();
        cfg.MigrateUnboundSlots();
        cfg.MigrateTuning();
        return cfg;
    }

    /// <summary>
    /// Folds a pre-ship-profile bot.json into one ship per server.
    ///
    /// Everything the file used to hold globally — the tuning — and per-server — the slots, the
    /// learned ability ids, the hex count — described exactly one ship, because that is all the
    /// bot could fly. So each server gets a single ship carrying all of it, and nothing is lost:
    /// the setup you had keeps working, under a name, with room beside it for the next hull.
    ///
    /// <para>The tuning is <b>copied</b> per server rather than shared. Two servers holding the
    /// same object would have looked fine until the day changing one silently changed the other.</para>
    /// </summary>
    private void MigrateToShipProfiles()
    {
        foreach (var server in Servers)
        {
            if (server.Ships.Count > 0) continue;

            server.Ships.Add(new ShipProfile
            {
                // Named for what it is rather than "Ship 1": whatever you were flying when this
                // build first ran is the ship these settings were tuned for.
                Name = "Original setup",
                Tuning = CloneTuning(Bot),
                Weapons = server.Weapons ?? [],
                Slots = server.Slots ?? [],
                WeaponHexes = server.WeaponHexes ?? 4,
            });
            server.SelectedShip = 0;

            server.Weapons = null;
            server.Slots = null;
            server.WeaponHexes = null;
        }

        Bot = null;
    }

    /// <summary>
    /// A deep-enough copy of a tuning: every value, and fresh collections rather than shared ones.
    /// Round-tripping through the serializer keeps this honest when properties are added later —
    /// a hand-written copy is a list that silently stops being complete.
    /// </summary>
    public static BotTuning CloneTuning(BotTuning? source)
    {
        if (source is null) return new BotTuning();
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<BotTuning>(json, JsonOptions) ?? new BotTuning();
    }

    /// <summary>Copies the current ship's setup under a new name, so a second hull starts from
    /// something that works rather than from defaults.</summary>
    public ShipProfile DuplicateCurrentShip(string name)
    {
        var from = CurrentServer?.CurrentShip;
        var copy = new ShipProfile
        {
            Name = name,
            Tuning = CloneTuning(from?.Tuning),
            WeaponHexes = from?.WeaponHexes ?? 4,
            // Slots and ability ids are deliberately NOT copied. They belong to the hull that
            // learned them -- slot 4 on a Vanir is not slot 4 on a Raptor -- and carrying them
            // over would have the new ship firing the old one's guns by id.
        };
        CurrentServer?.Ships.Add(copy);
        return copy;
    }

    /// <summary>
    /// Moves settings whose MEANING changed, not just their default.
    ///
    /// Only exact old defaults are touched. A number you typed in yourself is a decision and
    /// stays put, even when the units under it have shifted — the alternative is a config file
    /// that silently rewrites your choices every time the code learns something.
    /// </summary>
    /// <remarks>Runs per ship, because there is a tuning per ship now. Every one of them may have
    /// come from an old file, and a ship added later starts at current defaults and is untouched
    /// by all of it.</remarks>
    private void MigrateTuning()
    {
        foreach (var server in Servers)
            foreach (var ship in server.Ships)
                MigrateOneTuning(ship.Tuning);
    }

    private static void MigrateOneTuning(BotTuning t)
    {
        // Was a distance from the rock's CENTRE, now a gap to its surface. 179 from the centre of
        // a typical rock is about 120 from its face, so an untouched default keeps its behaviour.
        if (Math.Abs(t.AsteroidStandoff - 179f) < 0.01f) t.AsteroidStandoff = 120f;

        // A flat 130u made a 38u rock into a 168u no-go sphere. The margin is for the ship, and
        // it is now floored by the ship's own size instead of guessing large.
        if (Math.Abs(t.CollisionMargin - 130f) < 0.01f) t.CollisionMargin = 70f;

        // Renamed when the settings moved onto BotTuning. Only an older file carries the old key.
        if (t.AutoRepairShip is { } legacyRepair)
        {
            t.AutoRepair = legacyRepair;
            t.AutoRepairShip = null;
        }

        // An older bot.json only ever held one resource. Promote it to a one-entry priority list
        // the first time, so the setting you picked keeps meaning what it did.
        if (t.WantedResources.Count == 0
            && Enum.TryParse<ResourceType>(t.WantedResource, out var legacyResource)
            && legacyResource != ResourceType.Any)
        {
            t.WantedResources.Add(legacyResource);
        }
        t.WantedResource = null;

        // Filtered against what a rock can actually hold: an earlier build offered cubits, uranium
        // and plutonium, so a saved list can still rank things that will never match. Left in
        // place they would occupy priority slots above resources that do exist.
        t.WantedResources.RemoveAll(r => !Resources.IsMinable(r));
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
            foreach (var ship in server.Ships)
                foreach (var slot in ship.Slots)
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
