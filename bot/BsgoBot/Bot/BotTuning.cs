using System.Text.Json.Serialization;
using BsgoBot.Protocol;
using BsgoBot.World;

namespace BsgoBot.Bot;

/// <summary>
/// Everything you can tune about how the farm loop behaves, in one object.
///
/// Split out of <see cref="FarmBot"/> because it was 77 properties living on the same class
/// that flies the ship, each of which had to be hand-copied out of <see cref="BotSettings"/>
/// one line at a time.
///
/// <para>It is one object so it can be <b>swapped</b>. A Raptor and a Vanir disagree about
/// nearly every number in here — standoffs, braking, threat range, scan reserve — so a ship
/// change is meant to be an assignment to <see cref="FarmBot.T"/>, not 77 of them.</para>
/// </summary>
public sealed class BotTuning
{
    /// <summary>Turns off the injected card requests, leaving passive sniffing alone. The
    /// requests are ordinary client traffic, but they are still traffic we invented.</summary>
    public bool FetchCatalogue { get; set; }

    public FarmMode Mode { get; set; } = FarmMode.Combat;

    /// <summary>
    /// Last-resort reach, for a server that publishes neither slot stats nor a catalogue.
    ///
    /// Nearly dead, and deliberately so. Reach now comes from the live stat stream, then the
    /// catalogue, then what you declared — three real sources, of which the catalogue alone
    /// covers every fitted slot without you typing anything. This number is only consulted when
    /// all three are empty, and with <see cref="RequireKnownReach"/> on that case holds fire
    /// instead of shooting on a guess.
    ///
    /// It still sizes a couple of approach decisions, which is why it has not been deleted.
    /// </summary>
    public float FallbackRange { get; set; } = 3000f;

    /// <summary>
    /// Hold fire when a weapon's reach is unknown, rather than assuming
    /// <see cref="FallbackRange"/>.
    ///
    /// The asymmetry is the whole argument: an over-estimate makes the server refuse the cast
    /// without saying so — spent cooldown, spent power, silent failure, and in one case a
    /// scanner that looked like it was out of consumables when it was simply being aimed 1,000u
    /// beyond its reach. An under-estimate just flies closer than it needed to.
    /// </summary>
    public bool RequireKnownReach { get; set; } = true;

    /// <summary>
    /// How many one-shot abilities may be cast in a single 250ms tick.
    ///
    /// Not a throttle for its own sake. Firing every gun in one pass put nine casts on the wire
    /// inside three milliseconds, and bsgo.fun closed the connection immediately afterwards,
    /// repeatably. Whatever the server's exact rule, no human client produces that pattern.
    /// Two per tick is eight per second, which a person holding down the fire keys can match.
    /// </summary>
    public int MaxCastsPerTick { get; set; } = 2;

    /// <summary>Used when a weapon has no cooldown stat.</summary>
    public int FallbackFireIntervalMs { get; set; } = 900;

    /// <summary>Fly to targets that are out of range instead of just reporting them.</summary>
    public bool AutoApproach { get; set; } = true;

    /// <summary>Drop into the boost gear on long approaches. Costs tylium; the server puts you
    /// back in the regular gear by itself when the hold runs dry.</summary>
    public bool UseBoost { get; set; } = true;

    /// <summary>
    /// Seconds of boost-speed travel to leave for shedding the boost, on top of the braking zone.
    ///
    /// This replaces a flat 1,500u margin, which was asking the wrong question. "Am I far enough
    /// out to boost" is not a fixed distance, it is "is there room to run fast and still stop" —
    /// the braking zone plus the room it takes to come down off boost. The flat number was about
    /// three times the real requirement, so with a rock's standoff at ~170u nothing closer than
    /// 1,670u ever boosted, and no asteroid hop in a belt is that long. Every mining approach ran
    /// at cruise; the only boosts in a 48-minute session were two retreats across the sector.
    /// </summary>
    public float BoostShedSeconds { get; set; } = 1.5f;

    /// <summary>
    /// Throttle used when the server never published the ship's Speed stat AND you have never
    /// flown at full throttle yourself. Servers that clamp will cut it down to the real maximum.
    /// </summary>
    public float FallbackSpeed { get; set; } = 100f;

    /// <summary>
    /// Your ship's real top speed, typed in. Beats everything else — the published stat, the
    /// fallback, and anything watched off your own throttle.
    ///
    /// Worth having because none of the automatic sources is reliable: the Speed stat is not
    /// published on every server, and watching your throttle only ever sees whatever you last
    /// happened to fly at. 0 leaves it on the automatic sources.
    /// </summary>
    public float TopSpeedOverride { get; set; }

    /// <summary>
    /// Your ship's speed in the boost gear, typed in. Beats the published BoostSpeed stat.
    ///
    /// This is never sent as a throttle — the gear applies it. It decides whether boosting is
    /// worth engaging, and it sizes the braking and obstacle-lookahead distances so they match
    /// the speed actually being flown. 0 leaves it on the published stat, and if the server
    /// doesn't publish one, the bot never boosts at all.
    /// </summary>
    public float BoostSpeedOverride { get; set; }

    /// <summary>Request and take loot from wrecks and cargo that come within reach.</summary>
    public bool AutoLoot { get; set; } = true;

    /// <summary>Loot anything closer than this. Cargo objects override it with their own radius.</summary>
    public float LootRange { get; set; } = 600f;

    /// <summary>Stop fighting below this fraction of hull.</summary>
    public float RetreatHull { get; set; } = 0.25f;

    /// <summary>
    /// Shoot back while mining. Without this the bot mines through an NPC attack run and dies
    /// holding station on a rock — it has guns, it just wasn't looking.
    /// </summary>
    public bool DefendSelf { get; set; } = true;

    /// <summary>A hostile closer than this is a threat. Anything actively targeting us counts
    /// at any distance.</summary>
    public float ThreatRange { get; set; } = 1500f;

    /// <summary>On low hull, actually run — away from the threat, at full throttle. Stopping
    /// dead in front of something shooting at you is not a retreat.</summary>
    public bool FleeWhenHurt { get; set; } = true;

    /// <summary>Run for a friendly outpost when hurt, rather than just away. Something that
    /// out-runs you can be out-run to a station; open space has nowhere to arrive at.</summary>
    public bool FleeToOutpost { get; set; } = true;

    /// <summary>Cast the repair module when the hull drops below this fraction.</summary>
    public float RepairAtHull { get; set; } = 0.8f;

    /// <summary>Use the repair module at all. It costs power the guns would otherwise get.</summary>
    public bool UseRepairAbility { get; set; } = true;

    /// <summary>Cadence for a repair ability the server published no cooldown for. Strike Damage
    /// Control reloads in 30 seconds, so this errs on the side of that.</summary>
    public int RepairIntervalMs { get; set; } = 30000;

    /// <summary>Shoot other players too, not just NPCs.</summary>
    public bool AttackPlayers { get; set; }

    /// <summary>
    /// Stay away from enemy weapon platforms and outposts, and never pick a rock or a target
    /// inside their reach. They out-range and out-gun a strike ship, they never lose interest,
    /// and unlike an NPC you cannot out-run one you have already flown up to.
    /// </summary>
    public bool AvoidHostileStations { get; set; } = true;

    /// <summary>
    /// How far to stay from an enemy emplacement. This is a deliberately conservative guess, not
    /// a number read off the wire: the server publishes slot stats for YOUR ship only, so the
    /// bot cannot know a given platform's actual reach. Raise it if something still shoots you.
    /// </summary>
    public float HostileStationKeepOut { get; set; } = 2500f;

    /// <summary>
    /// Which NPC kinds to hunt. Empty means all of them. Populated from the toolbar so you
    /// can farm fighters and leave the cruisers alone (or the reverse).
    /// </summary>
    public HashSet<SpaceEntityType> Prey { get; set; } = [];

    /// <summary>
    /// Only mine asteroids whose scan says they hold this resource. <see cref="ResourceType.Any"/>
    /// takes whatever is nearest. Unscanned asteroids are still tried, because the bot has no
    /// way to scan one yet — until it can, this filter only bites on rocks YOU scanned.
    /// </summary>
    /// <summary>
    /// Which resources to mine, best first. Empty means "whatever is nearest".
    ///
    /// The order is the priority: while any rock of the first entry is confirmed and reachable,
    /// nothing further down the list is chosen. It only decides what to fly to NEXT, though — a
    /// rock already being worked is finished rather than abandoned the moment something
    /// higher-ranked respawns, because the damage already put into it would be thrown away.
    /// </summary>
    /// <remarks>Settable, unlike the get-only collection this used to be: bot.json is now
    /// deserialized straight into this object, and System.Text.Json skips a get-only collection
    /// rather than populating it — which would silently drop a saved resource list on load.</remarks>
    public List<ResourceType> WantedResources { get; set; } = [];

    /// <summary>
    /// Superseded by <see cref="WantedResources"/>, kept only so an existing bot.json still means
    /// what it meant. Read once by <c>Config.MigrateTuning</c>, then cleared — so it survives
    /// exactly one save and then stops cluttering the file.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WantedResource { get; set; }

    /// <summary>
    /// What <see cref="AutoRepair"/> was called in bot.json before the settings moved here.
    /// Migration only, never read by the bot, and dropped from the file once folded in.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoRepairShip { get; set; }

    /// <summary>Also order a mining ship to the asteroid (costs resources) as well as
    /// firing your own mining laser.</summary>
    public bool UseMiningFacility { get; set; }

    /// <summary>How far inside the optimal range to sit. 0.6 means "60% of optimal" — close
    /// enough that drifting doesn't push us back out of the accurate band.</summary>
    public float CloseInFactor { get; set; } = 0.6f;

    /// <summary>Never try to sit closer than this. Flying into an object isn't an attack run.</summary>
    public float MinimumStandoff { get; set; } = 150f;

    /// <summary>
    /// How many seconds of travel still counts as "here" when picking the next rock. 0 falls back
    /// to the scanner's reach alone, which is the old behaviour.
    ///
    /// <para>Within this radius a confirmed rock is preferred outright over a nearer unconfirmed
    /// one — worth it when the trip is short, ruinous when it is not. Expressed in seconds rather
    /// than units so it scales with the hull: at 30 seconds a Raptor ranges ~1,600u for a known
    /// rock and a Vanir ~800u, which is the same decision measured in the currency that actually
    /// matters, which is time.</para>
    /// </summary>
    public float LocalTravelSeconds { get; set; } = 30f;

    /// <summary>
    /// Leave rocks alone if a hostile that MOVES — a drone, an NPC fighter — is within this far of
    /// them. 0 turns it off.
    ///
    /// <para>Separate from <see cref="HostileStationKeepOut"/> because the two want opposite
    /// treatment. A platform is a place: back out of its envelope and the problem is solved. A
    /// drone is not a place, and backing away from one solves nothing because it follows. The only
    /// move that helps against something mobile is to not have parked next to it — so this steers
    /// target selection rather than triggering an escape.</para>
    ///
    /// <para>Deliberately wider than a drone's own reach: twenty seconds motionless beside a rock
    /// is a long time to be inside anything's envelope, and a rock with company is rarely worth
    /// the trade when the belt has hundreds more.</para>
    /// </summary>
    public float HostileShipKeepOut { get; set; } = 2000f;

    /// <summary>Treat enemy PLAYERS as things to stay away from too, not just NPCs. Off by
    /// default: on a busy server it can empty the map of workable rocks.</summary>
    public bool AvoidPlayers { get; set; }

    /// <summary>
    /// This ship's half-size in units — bow to centre, or centre to wingtip, whichever is larger.
    /// 0 works it out.
    ///
    /// <para>The single most important number about a hull that the server never sends. Reply.WhoIs
    /// carries a radius for rocks and planets and for no ship at all, so the bot has only ever been
    /// able to infer this — and inferred it as ~35u for an Advanced Vanir, which is a Raptor. Every
    /// clearance sphere, every braking distance and every standoff floor is built on it, so one
    /// wrong number here flies a line ship into a belt like a strike craft.</para>
    ///
    /// <para>Left at 0 the bot measures the spread of the hull's own hardpoints, which is a lower
    /// bound: the ship carries on past its outermost gun. If it still clips things, type the real
    /// figure — it beats every guess. Per ship, like everything else here.</para>
    /// </summary>
    public float HullRadius { get; set; }

    /// <summary>
    /// The <b>closest</b> the ship will get to an asteroid's <b>surface</b>. The rock's own radius
    /// is added on top, so one number works for a 30u pebble and a 400u boulder alike.
    ///
    /// A floor, not a destination. Where the ship actually parks is the top of the mining
    /// weapon's optimal band, because accuracy is flat across that band and closing spends travel
    /// for nothing — this number only stops the band from putting the hull inside a rock. Raise it
    /// if the ship keeps clipping things; it cannot make the ship sit further out than its guns.
    ///
    /// It used to be measured from the centre, which made it a setting you could not feel: it was
    /// floored by radius + margin, so on anything bigger than the number you typed the floor won
    /// and the value did nothing at all. 0 falls back to the derived standoff.
    /// </summary>
    public float AsteroidStandoff { get; set; } = 120f;

    /// <summary>
    /// How far outside a body's clearance sphere to park, as a multiple of it.
    ///
    /// Exists because "just outside" and "exactly on the edge" behave completely differently: a
    /// ship parked on the boundary flips between "holding station" and "inside an obstacle" on
    /// noise alone.
    /// </summary>
    public float StandoffMargin { get; set; } = 1.15f;

    /// <summary>Same, for planetoids — which are enormous, and mined by ordering a mining ship
    /// rather than by shooting, so this is not clamped to weapon reach.</summary>
    public float PlanetoidStandoff { get; set; } = 1200f;

    /// <summary>How many times an object's reported radius to treat as solid. The radius the
    /// server publishes is a bounding figure, not the visual hull, so leave real margin.</summary>
    public float RadiusClearance { get; set; } = 3f;

    /// <summary>Ceiling on the braking zone. Full throttle right up to the stopping point means
    /// coasting straight through it, but the zone itself is worked out from speed.</summary>
    public float BrakingDistance { get; set; } = 700f;

    /// <summary>Seconds of travel to spend braking. The zone is this times the ship's top speed,
    /// so a fast ship gets room to stop and a slow one doesn't crawl for a minute.</summary>
    public float BrakingSeconds { get; set; } = 1.6f;

    /// <summary>Floor on the braking zone, for ships slow enough that the seconds figure would
    /// leave no room to shed speed at all.</summary>
    public float MinBrakeDistance { get; set; } = 120f;

    /// <summary>Slowest we'll crawl in on the final approach.</summary>
    public float MinApproachSpeed { get; set; } = 8f;

    /// <summary>
    /// Steer around solid objects that are not the thing we're flying to.
    ///
    /// Every heading the bot sends points straight at its target, and the braking ramp is
    /// worked out from the distance to that target alone — so nothing in between was ever
    /// considered. The case that kills is re-targeting: a rock scans empty, stops being a
    /// candidate, and the next tick picks a rock somewhere else and opens the throttle while
    /// the abandoned one is still directly ahead at close range.
    /// </summary>
    public bool AvoidCollisions { get; set; } = true;

    /// <summary>Clearance to add to an obstacle's own radius, in units, for everything that is
    /// not an asteroid or a planetoid — ships, stations, debris. The published radius is a
    /// bounding figure for the object, and says nothing about our own hull.</summary>
    public float CollisionMargin { get; set; } = 130f;

    /// <summary>
    /// Room to leave around an asteroid, on top of 90% of its radius — which is the collider the
    /// server actually builds for it.
    ///
    /// Deliberately small. Rocks are what the ship spends its life threading between, they are
    /// mostly tiny, and every unit here is added to a sphere the bot must stay out of, must brake
    /// for, and must steer around. Floored by our own hull radius, which is the only part of this
    /// that is genuinely non-negotiable.
    /// </summary>
    public float AsteroidCollisionMargin { get; set; } = 40f;

    /// <summary>
    /// Room to leave around a planetoid, on top of its scaled radius.
    ///
    /// Deliberately large, and for the opposite reason: there are a handful of them, none of them
    /// are on the way to anything, and the ship approaches them at cruise. A flat margin that
    /// suits a 40u rock is a rounding error on a 1,500u body.
    /// </summary>
    public float PlanetoidCollisionMargin { get; set; } = 500f;

    /// <summary>How much of a planetoid's published radius to treat as solid. Above 1 because
    /// nothing about a planetoid's stated size is conservative.</summary>
    public float PlanetoidClearanceFactor { get; set; } = 1.25f;

    /// <summary>
    /// Fly through any asteroid whose collision would cost less than this fraction of maximum
    /// hull, instead of steering around it. 0 turns it off and dodges everything, as before.
    ///
    /// <para>The server charges half the rock's hull points for a collision and applies <b>no
    /// speed term at all</b>, so the choice is simply: pay that hull, or pay the seconds it takes
    /// to fly around. On a line ship the hull is much the cheaper of the two — a Vanir carries
    /// ~4,500 points and recovers 35 a second, so a small rock costs it about three seconds of
    /// regeneration, while turning a 27 m/s hull at 22 degrees a second around the same rock
    /// costs far longer. On a strike ship the same fraction is a handful of points, so it keeps
    /// dodging almost everything. One number, right on both hulls.</para>
    ///
    /// <para>Only asteroids qualify. Planetoids, stations and ships are always avoided.</para>
    /// </summary>
    public float IgnoreCollisionHullFraction { get; set; } = 0.05f;

    /// <summary>
    /// Size to assume for a planetoid the server has not published a radius for.
    ///
    /// Deliberately large. The radius arrives in a WhoIs body and for scenery it often never
    /// arrives at all, and the old behaviour on silence was to assume ZERO — which made a body
    /// two thousand units across into a 500u sphere and flew the ship straight through it.
    /// Guessing high costs a wider berth around one object; guessing low costs the ship.
    /// </summary>
    public float PlanetoidAssumedRadius { get; set; } = 1500f;

    /// <summary>Size to assume for an asteroid with no published radius. Small, because rocks are
    /// small and are what the ship spends its life threading between — but not zero.</summary>
    public float AsteroidAssumedRadius { get; set; } = 60f;

    /// <summary>How far ahead to look for obstacles, in seconds of travel at top speed. This has
    /// to cover the turn as well as the stop: the heading only updates a few times a second, so
    /// a deflection decided one ship-length out never lands.</summary>
    public float CollisionLookaheadSeconds { get; set; } = 5f;

    /// <summary>
    /// How long a continuous detour may hold the approach watchdog off before the target counts
    /// as unreachable after all.
    ///
    /// Generous enough to fly the long way around the largest body in a sector, short enough that
    /// a rock which cannot be reached — one tucked against a planetoid, or inside it — is given up
    /// on rather than orbited indefinitely.
    /// </summary>
    public float DetourPatienceSeconds { get; set; } = 45f;

    /// <summary>
    /// Wait for a weapon's own optimal range before firing it, while still flying in. A cannon
    /// quoted at 250u but reaching 600u otherwise empties the power pool on long shots during
    /// every approach. Ignored once we've stopped closing — see <see cref="FireAll"/>.
    /// </summary>
    public bool HoldFireUntilOptimal { get; set; } = true;

    /// <summary>
    /// Shoot rocks with your combat guns as well as your mining laser. Cannons damage asteroids
    /// perfectly well, so the only reason to turn this off is to keep the power pool for the
    /// laser and the scanner. Positioning is unaffected — the laser still sets the standoff.
    /// </summary>
    public bool FireGunsWhileMining { get; set; } = true;

    /// <summary>
    /// Answer a threat with your combat guns without breaking off the rock.
    ///
    /// Needs a ship whose guns are not all mining gear — a line or escort hull with a couple of
    /// cannons alongside the lasers. On one of those, breaking off a rock to deal with a drone
    /// costs the whole approach and re-approach for a fight the ship could have had standing
    /// still. With this on it simply shoots back and keeps mining.
    ///
    /// Turning it off restores the old behaviour: a threat takes the whole ship until it is
    /// dead or gone. That is still what happens anyway when the guns that mine are the only guns
    /// aboard, or the threat is beyond their reach — see <c>ReturnFireAsync</c>.
    /// </summary>
    public bool FightWhileMining { get; set; } = true;

    /// <summary>
    /// Only scan when the answer would change what we do — i.e. when a resource filter is set.
    /// A Mineral Analysis Module costs 50 power at level 1 against a 100-point pool, which is
    /// several seconds of mining lasers. Paying that for information nobody reads is the
    /// difference between farming and drifting with a flat battery.
    /// </summary>
    public bool ScanOnlyWhenFiltering { get; set; } = true;

    /// <summary>Fraction of the power pool to keep back for weapons when deciding to scan.</summary>
    public float ScanPowerReserve { get; set; } = 0.25f;

    /// <summary>Cap on ids in one area cast. The client sends everything in range with no limit,
    /// but a sane ceiling keeps one message from getting absurd in a crowded belt.</summary>
    public int MaxAreaScanTargets { get; set; } = 32;

    /// <summary>Cadence for the scan sweep when the scanner ability has no cooldown stat.</summary>
    public int ScanIntervalMs { get; set; } = 1200;

    /// <summary>How long to wait for a scan reply before asking about that rock again.</summary>
    public int ScanRetrySeconds { get; set; } = 20;

    /// <summary>
    /// Unanswered casts at ONE rock before that rock is condemned as gone.
    ///
    /// The server answers a scan at any rock that exists and silently swallows a cast at one
    /// that doesn't, so silence concentrated on a single rock is evidence about the rock, not
    /// the scanner. Two casts a retry apart is ~40s of proof.
    /// </summary>
    public int ScanStrikesBeforeGone { get; set; } = 2;

    /// <summary>How long a rock condemned as unscannable or unworkable stays off the menu.
    /// Long, because the only cure is the server respawning the rock.</summary>
    public int MuteRockSkipMinutes { get; set; } = 30;

    /// <summary>
    /// How long the mining loop may sit engaged on one rock with no shot possible — waiting for
    /// a scan, no reach known for any gun, parked outside every firing band — before the rock is
    /// given up on. These holds used to have no clock at all: each returned before any watchdog
    /// armed, and the stall watchdog only counts time while the guns actually fire, so a rock
    /// that could never be worked parked the ship at full power indefinitely.
    /// </summary>
    public float HeldFirePatienceSeconds { get; set; } = 30f;

    /// <summary>
    /// How long a scan result is trusted before the rock is worth re-scanning. Asteroid resources
    /// respawn on a server-side timer and can come back as something else.
    ///
    /// Three minutes was less than it takes to break one rock open, so by the time the bot looked
    /// up from the first target it had forgotten the other four it had already paid to identify
    /// and started the sweep again. A scan is only wrong if the rock respawns, which needs it to
    /// be emptied first — so trusting one for a working session is the cheaper mistake.
    /// </summary>
    public int ScanFreshnessSeconds { get; set; } = 900;

    /// <summary>
    /// How many confirmed rocks to keep queued up. At or above this, the sweep stops: power is
    /// worth more in the lasers than in identifying a rock we won't get to for minutes.
    ///
    /// Only consulted for an <b>area</b> scanner, where one cast identifies a whole field and a
    /// queue is a real thing to have. A single-target scanner earns one rock per cast, so it
    /// scans when there is nothing confirmed to shoot and stays quiet otherwise.
    /// </summary>
    public int ScanQueueDepth { get; set; } = 2;

    /// <summary>
    /// How long the guns may fire at a rock with nothing whatsoever to show for it before the
    /// rock is given up on.
    ///
    /// Neither its hull falling nor a single unit of ore reaching the hold, for this long, means
    /// we are not mining it — most often because it no longer exists. An asteroid we really are
    /// working reports one or the other within a second or two, so this can be short without ever
    /// firing on a rock that is merely tough.
    /// </summary>
    public float MiningStallSeconds { get; set; } = 20f;

    /// <summary>
    /// How old the server's last statement of where we are may be before a distance is no longer
    /// worth acting on.
    ///
    /// Only some messages carry our real position — SyncMove, the Rest/Teleport/Warp maneuvers, a
    /// WhoIs. A normal approach is Directional maneuvers, heading and march speed only, so in
    /// between the ship's position is integrated: flown straight, at the ordered speed, from the
    /// last fix. The real ship arcs through its turn and takes time to reach that speed, so the
    /// model runs ahead of it, and the error grows for as long as the flight lasts.
    ///
    /// That error is what parked the ship out of reach of a rock the status line said it was
    /// mining: the modelled distance crossed inside mining range, the throttle was cut, and the
    /// lasers fired at something the server considered too far away — silently, because an
    /// out-of-range cast is refused without a reply. Nothing recovered until the 20s stall
    /// watchdog gave up on a perfectly good rock.
    ///
    /// Four seconds because the model is accurate for a second or two, and <see
    /// cref="SpaceObj.PredictedPosition"/> stops advancing at three regardless.
    /// </summary>
    public float SelfPositionTrustSeconds { get; set; } = 4f;

    /// <summary>
    /// How long to sit still waiting for a fresh fix before flying on with the estimate anyway.
    ///
    /// Stopping is what asks the question: the ship coming to rest makes the server broadcast a
    /// Rest maneuver, which states a position outright. But a server that never sends one must
    /// not be able to park the bot indefinitely — a stationary ship's estimate stops drifting in
    /// any case, so past this point the estimate is the best there is and working on it beats
    /// waiting for something that isn't coming.
    /// </summary>
    public float SelfPositionWaitSeconds { get; set; } = 6f;

    /// <summary>
    /// The distance at which a rock is worth half its ore. Lower keeps the ship local; higher
    /// lets it range further for a big find. See <see cref="RockValue"/>.
    ///
    /// 1000 is set so that a rock at 2,000u needs five times the ore to be worth the trip — 3,000
    /// beats a nearby 500 — while one at 5,000u needs twenty-six times, which nothing realistically
    /// has. That is the line between "worth a detour" and "worth crossing the sector".
    /// </summary>
    public float RockTravelPenalty { get; set; } = 1000f;

    /// <summary>
    /// Scans cast with no reply at all before the resource filter is abandoned and the bot mines
    /// whatever it can reach. Higher than the 3 that triggers the "out of power cells" warning,
    /// so the warning always lands first and you get a chance to fix it.
    /// </summary>
    public int ScanFailuresBeforeUnfiltered { get; set; } = 5;

    /// <summary>Spacing between scanner-identification probes, so a ship full of utility
    /// slots doesn't fire all of them in one tick.</summary>
    public int ProbeIntervalSeconds { get; set; } = 3;

    /// <summary>Where to stop, in units from a contact's centre, on a fly-to or follow run.</summary>
    public float FollowDistance { get; set; } = 350f;

    /// <summary>
    /// Give up a one-shot <b>Go to</b> that has made no ground for this long.
    ///
    /// Only a Go to. A <b>Follow</b> never gives up, however far behind it falls: something that
    /// is outrunning you now can turn, stop, dock or lose its boost a minute later, and a chase
    /// that quits the moment the gap widens is a chase that never catches anything. It says it is
    /// losing ground and keeps flying.
    /// </summary>
    public int FollowStallSeconds { get; set; } = 30;

    /// <summary>
    /// Whether the bot may send a dock request at all.
    ///
    /// On, now that the sequence is right. It was briefly off for good reason: three dock
    /// requests had ever been sent — 02:18:54, 02:34:54 and 13:37:15 on 27 Jul — and the server
    /// hung up 78ms, 364ms and 80ms later, with no case of one working.
    ///
    /// Neither the message nor the range was ever the problem. A real dock captured off the wire
    /// is <c>022D000100004A00000000</c>, which is byte-for-byte what the bot sent, and the same
    /// outpost accepted a manual dock from 791u while the bot was asking from 248u. What was
    /// missing was the LockTarget in front of it — see <see cref="LockBeforeDockAsync"/>.
    ///
    /// Kept as a switch because it is the one action with a proven history of ending sessions.
    /// Turning it off costs nothing but the last step: the retreat still runs to the outpost and
    /// shelters under its guns, which is the part that saves the ship.
    /// </summary>
    public bool AllowDocking { get; set; } = true;

    /// <summary>How close to get before asking to dock, when nothing has been learned yet.</summary>
    public float DockApproach { get; set; } = 250f;

    /// <summary>Give up on a dock run after this long.</summary>
    public int DockTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// The shortest time we will spend at a refuge before its hull trend is allowed to send us
    /// away. Hysteresis, not a deadline: one burst of damage on arrival must not abandon an
    /// outpost that is about to let us in.
    /// </summary>
    public float DockGiveUpSeconds { get; set; } = 10f;

    /// <summary>
    /// How much hull we will lose at a refuge before deciding it is not sheltering us.
    ///
    /// This replaced a flat 10s timeout, which was wrong in exactly the case it was written for:
    /// a dock cooldown after combat can run to tens of seconds, and a short timer abandons a good
    /// outpost while its countdown is still ticking. Whether the shelter is working is a thing we
    /// can measure — 0.10 is ten points of hull lost since the best reading since arriving.
    /// </summary>
    public float RefugeBleedFraction { get; set; } = 0.10f;

    /// <summary>
    /// Get the ship back out of the hangar by itself: answer the death screen, buy the hull
    /// condition back, launch, and carry on farming.
    ///
    /// Off means a death ends the session in every practical sense — the farm loop keeps ticking
    /// against a ship that is not in the sector and does nothing at all until someone presses
    /// Undock.
    /// </summary>
    public bool AutoUndock { get; set; } = true;

    /// <summary>Buy the ship's condition back before launching, with titanium. Dying always costs
    /// condition, and a wrecked hull launches with a fraction of its stats.</summary>
    public bool AutoRepair { get; set; } = true;

    /// <summary>
    /// How long to sit in the hangar before launching.
    ///
    /// Not politeness: the client has its own death sequence to play out, the repair has to be
    /// asked for and answered, and the server will not launch a ship it still thinks is dead. Six
    /// seconds is enough for all three without making a death cost a minute of farming.
    /// </summary>
    public int UndockDelaySeconds { get; set; } = 6;

    /// <summary>How long to wait before asking to launch again when the first ask changed nothing.</summary>
    public int RelaunchIntervalSeconds { get; set; } = 15;

    /// <summary>Wait this long after anchoring before asking to launch, so a carrier you boarded
    /// on purpose is not immediately thrown off it.</summary>
    public int UnanchorDelaySeconds { get; set; } = 4;

    /// <summary>
    /// Restrict hunting, locking and firing to contacts inside your own detection radii.
    ///
    /// <b>Off</b>, which is how the bot has always behaved: WhoIs reports objects far beyond
    /// every detection ring, so the bot happily engages things the client draws nothing for.
    /// That was suspected of causing the combat disconnects and turned on — but the theory was
    /// never actually tested, because the run that failed next was still the previous build. So
    /// it goes back to off rather than staying on unearned.
    ///
    /// Worth revisiting on its own merits: engaging a contact 9,000u away with a 1,500u DRADIS
    /// is a long flight to something that may not be there by the time we arrive.
    /// </summary>
    public bool HuntOnlyVisible { get; set; }

    /// <summary>
    /// How far to scan when nothing has published the scanner's reach.
    ///
    /// Deliberately short. The server checks the scan against the ability's own
    /// <c>ObjectStat.MaxRange</c> and simply skips targets beyond it without a word
    /// (bsgocore <c>ResourceScanAction.internalProcess</c>), so overstating the reach produces
    /// silent nothing — which is exactly what a 3,000u guess did.
    /// </summary>
    public float ScanReachFallback { get; set; } = 600f;

    /// <summary>
    /// Ask the server to stream the target's hull and power.
    ///
    /// Was briefly disabled on the theory that it caused the combat disconnects, being the one
    /// message combat sends and mining does not. A session that sent none of them and dropped
    /// anyway settled that: it is innocent, so it is back on. It supplies the target's hull
    /// readout and <c>TargetId</c>, which is how the bot knows something has locked us.
    /// </summary>
    public bool SubscribeToTarget { get; set; } = true;

    /// <summary>How far past an obstacle's clearance to get before we stop calling ourselves
    /// inside it. Pure hysteresis: it exists so leaving is a decision, not a boundary case.</summary>
    public float EscapeClearance { get; set; } = 1.25f;

}
