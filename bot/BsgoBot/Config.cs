using System.Text.Json;

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
    /// <summary>Slot id, which is also the ability id used to fire it (client:
    /// ShipAbility.ServerID => slot.ServerID). 0 means the hex isn't bound to a slot yet.</summary>
    public ushort SlotId { get; set; }

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

/// <summary>Everything you can tune about how the farm loop behaves.</summary>
public sealed class BotSettings
{
    /// <summary>Reach assumed for a weapon the server never published a range for.</summary>
    public float FallbackRange { get; set; } = 3000f;

    /// <summary>Cadence assumed for a weapon with no cooldown stat.</summary>
    public int FallbackFireIntervalMs { get; set; } = 900;

    public bool AutoApproach { get; set; } = true;
    public bool AutoLoot { get; set; } = true;
    public float LootRange { get; set; } = 600f;

    /// <summary>Use the boost gear on long approaches. Costs tylium.</summary>
    public bool UseBoost { get; set; } = true;

    /// <summary>Boost only while this much further out than the weapon reach we're heading for.</summary>
    public float BoostMargin { get; set; } = 1500f;

    /// <summary>Throttle used when the ship's Speed stat is unknown and you've never flown
    /// at full throttle yourself.</summary>
    public float FallbackSpeed { get; set; } = 100f;

    /// <summary>Your ship's real top speed in the regular gear, typed in. Beats the published
    /// stat, the fallback and anything watched off your own throttle. 0 = work it out.</summary>
    public float TopSpeedOverride { get; set; }

    /// <summary>Your ship's speed in the boost gear, typed in. Without this, a server that
    /// publishes no BoostSpeed stat means the bot never boosts at all. 0 = work it out.</summary>
    public float BoostSpeedOverride { get; set; }

    /// <summary>Steer and brake around solid objects that aren't the target.</summary>
    public bool AvoidCollisions { get; set; } = true;

    /// <summary>Clearance added to an obstacle's own radius before it counts as in the way.</summary>
    public float CollisionMargin { get; set; } = 130f;

    /// <summary>Distance to hold from an asteroid, in units from its centre. 0 = work it out.</summary>
    public float AsteroidStandoff { get; set; } = 179f;

    /// <summary>Distance to hold from a planetoid. They are far larger than asteroids.</summary>
    public float PlanetoidStandoff { get; set; } = 1200f;

    /// <summary>Where to stop on a Go to / Follow run from the contacts list, in units from the
    /// contact's centre. Floored by the contact's own radius, so a planetoid still gets room.</summary>
    public float FollowDistance { get; set; } = 350f;

    /// <summary>Stop fighting below this fraction of hull.</summary>
    public float RetreatHull { get; set; } = 0.25f;

    /// <summary>Shoot back at hostiles while mining instead of ignoring them.</summary>
    public bool DefendSelf { get; set; } = true;

    /// <summary>A hostile nearer than this counts as a threat. Anything targeting us always does.</summary>
    public float ThreatRange { get; set; } = 1500f;

    /// <summary>On low hull, fly away from the threat rather than just cutting the engines.</summary>
    public bool FleeWhenHurt { get; set; } = true;

    /// <summary>When fleeing, run to a friendly outpost rather than into open space.</summary>
    public bool FleeToOutpost { get; set; } = true;

    /// <summary>Cast the self-repair module (Strike Damage Control and friends) when hurt.</summary>
    public bool UseRepairAbility { get; set; } = true;

    /// <summary>Hull fraction below which the repair module is cast.</summary>
    public float RepairAtHull { get; set; } = 0.8f;

    /// <summary>Keep clear of enemy weapon platforms and outposts.</summary>
    public bool AvoidHostileStations { get; set; } = true;

    /// <summary>How far to stay from an enemy emplacement. A conservative guess — the server
    /// never publishes their reach, so raise it if something still reaches you.</summary>
    public float HostileStationKeepOut { get; set; } = 2500f;

    /// <summary>While flying in, hold each weapon until its own optimal range.</summary>
    public bool HoldFireUntilOptimal { get; set; } = true;

    public bool AttackPlayers { get; set; }

    /// <summary>Also order a mining ship to the rock, which costs resources.</summary>
    public bool UseMiningFacility { get; set; }

    /// <summary>Fire combat guns at asteroids alongside the mining laser.</summary>
    public bool FireGunsWhileMining { get; set; } = true;

    /// <summary>Stop scanning once this many confirmed rocks are queued up.</summary>
    public int ScanQueueDepth { get; set; } = 2;

    /// <summary>How long a scan result is trusted before the rock is worth re-scanning.</summary>
    public int ScanFreshnessSeconds { get; set; } = 900;

    /// <summary>NPC kinds to hunt, by <c>SpaceEntityType</c> name. Empty means all of them.</summary>
    public List<string> Prey { get; set; } = [];

    /// <summary>Resource to mine, by <c>ResourceType</c> name. "Any" takes whatever is nearest.</summary>
    public string WantedResource { get; set; } = "Any";
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

    public BotSettings Bot { get; set; } = new();

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

    private static string FilePath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "bot.json");

    public static Config Load()
    {
        Config cfg;
        try
        {
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config()
                : new Config();
        }
        catch
        {
            cfg = new Config();
        }

        cfg.SeedDefaults();
        return cfg;
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
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
