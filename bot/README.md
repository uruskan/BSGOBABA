# BSGO Farm Bot

A man-in-the-middle farm bot for a **private** BSGO server. It sits between `bsgo.exe`
and your own game server, watches the traffic to build a live map, and injects game
commands so the bot can fight/mine while the real client stays open and renders normally.

```
bsgo.exe ──► bsgobot (127.0.0.1:27050) ──► your server
   ▲                    │
   │ renders normally   │ injects LockTarget / Cast / Toggle / Mining / Loot / steering
   └────────────────────┘
```

Frames are forwarded **byte-for-byte unmodified**, so the client's own login, catalogue
and chat are never disturbed. The bot only observes and adds.

## Why a proxy rather than a headless client

You log in normally, with your own account, in the real client. The bot never handles
credentials and never needs the login handshake. It also gets the entire world state for
free, because the server is already streaming it to your client.

## Where the protocol came from

Nothing here is guessed. `client-src/` (in the parent folder) is the decompiled
`Assembly-CSharp.dll` from the 2019 client — unobfuscated Mono IL. Every opcode and every
field order in `Protocol/`, `World/` and `Bot/` was transcribed from it. The client binary
is the authority: both server implementations must conform to it, so this bot stays valid
regardless of which server you finish.

Key facts recovered from the client:

| Fact | Source |
|---|---|
| Frame = `[uint16 len BE][byte protocolId][uint16 msgType][payload]` | `Connection.RecvCurrentMessage`, `BgoProtocolWriter.WriteDataLength` |
| Only the length prefix is big-endian; everything else little-endian | `BgoProtocolReader.ReadBufferSize` vs `BinaryReader` defaults |
| **A client frame can hold many messages**; a server frame holds one | `GameProtocol.UpdateMessage` vs `ProtocolManager.Update` |
| Object type / faction / group are encoded in its id | `SectorFactory.CreateSpaceObject`, `SpaceObject.ExtractFaction` |
| `Reply.WhoIs` body layout differs per object type | `SpaceObject.Read` and its 20+ overrides |
| `PlayerProtocol Reply.Slots` states the whole loadout | `PlayerProtocol` `Reply.Slots` → `ShipSlot.Read` |
| A slot's **type** (gun/hull/computer…) is never on the wire | `ShipSlotCard` lives in the ship's catalogue card |
| Player names arrive on the Subscribe protocol, not with the ship | `SubscribeProtocol` `Reply.PlayerName` |
| Static objects carry their position **only** in `WhoIs` | `Asteroid.Read`, `DebrisPile.Read`, `CargoObject.Read`, … |
| Ships carry position **only** in `Move` / `SyncMove` maneuvers | `Ship.Read` has none; `RestManeuver.Read` has it |
| `MovementFrame` = 15 floats + 1 mode byte | `MovementFrame.Read` |
| `RemovingCause.Death/Hit/Collected` carry a trailing uint32 | `GameProtocol` `Reply.ObjectLeft` |
| Ability id == ship slot id | `ShipAbility.ServerID => slot.ServerID` |
| Auto abilities toggle; the rest cast per shot | `ShipAbility.DoCast` |
| Per-slot ranges/cooldowns arrive as `SlotStat` updates | `SpaceSubscribeInfo.Read`, `MyShipStats` |
| **The server sends the whole sector; the _client_ hides most of it** | `DradisHelper`, `DradisUpdater` |

## What the bot works out for itself

| Thing | How |
|---|---|
| **Your ship** | The player id from `+userID` (confirmed by `PlayerProtocol Reply.ID`) matched against the `playerId` inside each `PlayerShip` WhoIs |
| **Your position** | Your ship's `SyncMove` / `Move` frames and its own `WhoIs`. Only `SyncMove`, `Rest`/`Teleport`/`Warp` and `WhoIs` state it outright — steering maneuvers give heading and speed, so between them it is dead reckoning, and the bot tracks how old the last real fix is |
| **Everyone's position** | `WhoIs` for static objects, movement maneuvers for ships, linear dead reckoning between updates |
| **Friend or foe** | Faction and group bits of the object id, using the client's own `RelationHelper` rule |
| **Your weapons** | Per-slot stats when the server sends them, and/or watching you fire (all three fire opcodes) |
| **Your actual range** | `CannonMaxRange` / `MissileMaxRange` / `MiningMaxRange` / `MaxRange` from the slot stat stream |
| **Your fire rate** | `CannonCooldown` / `MissileCooldown` / `Cooldown` per weapon |
| **Target health** | `SubscribeInfo` on the current target, then `Reply.Info` |
| **Your slot list** | `PlayerProtocol Reply.Slots` — slot id, installed system guid, loaded consumable, broken or not |
| **Other players' names** | `SubscribeProtocol Reply.PlayerName`, which your client asks for by itself |

Learned weapons are saved per server profile in `bot.json`, so "fire once manually" is
once *ever*, not once per session — and on a server that publishes slot stats, never.

## Running it

From the project root:

```powershell
.\start-all.ps1
```

That starts MongoDB, then the game server, then the bot — in that order, waiting for each.
Then, in the bot window:

1. The proxy **auto-starts**. Confirm the stats panel shows `upstream ... [UP]`.
2. Click **Launch Game** (launches `bsgo.exe` pointed at the proxy, not the server).
3. Fly into a sector and click **Go Farm**.

Order matters: the client connects the instant it launches, so the proxy has to be
listening on 27050 first. `start-all.ps1` and the auto-start handle that for you.

### Doing it by hand

The server must not sit on 27050 — the client hardcodes that port (`LoginScreen.cs:317`),
so the proxy owns it and forwards upstream.

- C# server: `serverPort` in `Server/Server.cs`
- bsgocore: `GAMESERVER_PORT` in `.env`

Then run `bin\Debug\net9.0-windows\bsgobot.exe` (or `dotnet run --project BsgoBot`).
Everything else — ports, client path, account, settings — lives in `bot.json` next to the exe.
There is no `bot.json` in the repo on purpose: a session token is a credential. The app writes
one on first run, and `bot.example.json` shows the shape.

### Catching a session from a launcher

**Catch session** in the Connection card handles the case where you log in through someone's
launcher rather than typing an account into a profile.

The launcher hands its own `bsgo.exe` a one-shot session token on the command line, along with
the server, your player id and the client version. The bot needs all four to relaunch through the
proxy, and none of them are on the wire — the launcher's client connects straight to the live
server, so the proxy never sees that session at all. The command line is the only place they
exist, so that is where the bot reads them from (`Bot/SessionCatcher.cs`).

Click it, then log in normally. When the launcher's client appears the bot:

1. reads `+gameServer`, `+userID`, `+session` and `+version` off it,
2. **closes that client**, because the token is single-use and the proxied relaunch has to be the
   thing that spends it,
3. files it all as the `Live (captured)` server profile, selects it, and syncs your client's
   `Version` to whatever the launcher used.

Then click **Launch game**. A client already pointed at `127.0.0.1` is ignored — that one is the
bot's own relaunch, not the launcher.

This replaces `capture-session.ps1`, which did the same job through WMI in a second window.

## Reading the panel

The right-hand side has two blocks. The top one is counters. The bottom one is the
**diagnostics** block, and it answers directly why the bot is or isn't doing something:

```
my player id   5085935
my ship        #41000007 Colonial/Group0
my position    10430, -12, -8801
hull / power   87% / 64%

combat reach   1800u
mining reach   no laser known
  #3 Combat Toggle 1800u 1.5s [slot stats + your shot]

nearest hostile  BotFighter #85000009 at 1240u
nearest rock     none located
objects        184 known, 31 without a position
```

"Range" is straight-line distance in world units between your ship and the target, and
"combat reach" is the longest `MaxRange` among the weapons the bot will use. When the server
doesn't publish slot stats, that falls back to the **Fallback range** box in the toolbar.

## The three centre views

The middle column has tabs: **Map**, **Contacts** and **Loadout**. The log stays underneath all
three, and the bot reopens on whichever one you used last.

## Loadout — telling the bot what you are flying

The bot works most of your ship out on its own, but two things it cannot:

1. **Which slot is which hex.** Nothing on the wire ties a slot id to a position in the game's
   own UI. `Reply.Slots` gives ids and item guids; the *layout* lives in the ship's catalogue
   card, which the bot deliberately does not read.
2. **What a slot that does no damage is.** A damage-control module, an armour plate and the
   resource scanner all publish no damage and no weapon range. They are indistinguishable from
   the stat stream alone — which is why the bot has to probe for the scanner by firing things.

So the Loadout tab draws your ship the way the game does — four hexes above the hull, the
ability bar along the bottom — and you fill them in. Click a hex and say what is in it: name,
category, level, and what you want the bot to use it *for*.

Binding a hex to a real ability id, in whichever direction suits you:

- **Bind by firing** — arm it, then press that ability in the real client. The id goes past on
  the wire and lands in the field. This is proof, not a guess.
- **Test fire** — cast the id you have typed and watch which hex sweeps its cooldown in game.

What you declare and what the server publishes are kept apart, and each wins where it should:

| | Wins | Why |
|---|---|---|
| **Role** (combat / mining / scanner / repair / utility) | **You** | You can read the card; the stats can only see whether it advertises damage |
| Range, reload, power cost | **The server** | Those are the numbers it will actually enforce; yours fill the gaps on a server that publishes none |

The table under the diagram shows both, and names which source each row is flying on. A slot you
declared and then refitted is flagged: the item guid the server reports no longer matches the one
you described, so the card you typed in is stale.

Declaring a slot also stops the scanner probe from test-firing it, which is the fastest way to
keep it from spending your consumables to learn what you already know.

Everything lives per server profile in `bot.json`, under `Slots`.

## Contacts — the sector as a list

A sorted, filterable table of everything the server has told us about: type, name, side, id,
range, visibility band, health, and one line of whatever else matters for that kind of contact —
a rock's scanned contents, a wreck's interaction, who is targeting you.

- Click a column head to sort; click again to reverse. Unlocated contacts always sort last.
- The chips filter by kind; the box filters by name, type or id.
- Player names are real names, harvested from `SubscribeProtocol` — your client asks the server
  for them whenever a player appears, so they arrive without the bot requesting anything.
Selection is shared with the map — click a row and the dot gets a ring, click a dot and the row
highlights.

### What you can do to a contact

| Button | Effect |
|---|---|
| **Pin as target** | Holds it ahead of the hunting rules until it dies, leaves, or you unpin. Outranks the prey list and the "attack players" switch: pointing at something explicitly is a clearer instruction than a checkbox. |
| **Go to** | Fly there and stop. Ends itself on arrival. |
| **Follow** | Fly there and stay, re-closing whenever it pulls away. The same button stops it. |
| **Lock** / **Loot** / **Dock here** / **Ask WhoIs** | The raw client requests, aimed at the selected row |

**Go to** and **Follow** are runs, like docking: they take the ship over and stop farming, because
two things cannot steer at once. They sit *below* the hull and enemy-station guards, so a run
still patches your hull and still refuses to fly you into an outpost's envelope.

Where they stop is `FollowDistance` (350u, in `bot.json`), floored by the contact's own radius —
every distance on the wire is centre-to-centre, and a planetoid is not a point.

The two differ in one place, deliberately. A **Go to** that has made no ground for
`FollowStallSeconds` gives up, because a one-shot trip that cannot arrive can never finish. A
**Follow never gives up**, however far behind it falls: something outrunning you now can turn,
stop, dock or drop out of boost a minute later, and the only way to be there when it does is to
still be behind it. It says it is losing ground and keeps flying.

## The tactical display

The server streams **every** object in the sector. Your game client then throws most of them
away locally, in `DradisHelper`, against three radii from your own ship's stats. The bot never
applies that filter, so it can draw the whole sector and band each contact by how your client
is treating it:

| Band | Meaning | Drawn as |
|---|---|---|
| `VISUAL` | inside `DetectionVisualRadius` — shown even when cloaked | bright dot with a halo |
| `DRADIS` | inside `DetectionInnerRadius` — on your radar | solid dot with a halo |
| `MAP` | inside `DetectionOuterRadius`, or an always-visible type | dimmer solid dot |
| `DARK` | **beyond every radius — your client draws nothing** | hollow ring, labelled, amber |

`DradisHelper.IsAlwaysInMapRange` exempts asteroids, asteroid bots, planets, planetoids,
comets and sector events, so those never fall into `DARK` — that band is genuinely the
contacts the game is refusing to show you. Cloaked contacts drop out of `DRADIS`/`MAP` into
`DARK` exactly as the client's own comparison does. The `SECTOR` panel counts them as
`client-dark`.

The view is an orbiting projection of the sector plane:

- **drag** to orbit (yaw) and tilt, **wheel** to zoom, **double-click** to reset
- height above the plane is a stalk down to a shadow, so altitude is readable at a glance
- rings for weapon reach, `DRADIS` and `MAP`, with a readout in the top-right corner
- the `CONTACTS` legend is **clickable** — click a band to hide or show it
- the closest few `DARK` contacts are labelled with type and distance

Projection is orthographic rather than perspective on purpose: distances stay directly
comparable across the whole view, which is the point of a tactical map.

If your server never publishes the detection radii, the panel says so and shows a single
`UNKNOWN` band instead of inventing one.

## Modes

- **Combat** — picks the nearest object that is a valid enemy by the client's own faction
  rule, flies to it if it's out of reach, locks it, subscribes to its stats, and fires every
  known combat weapon on its own cooldown. Toggle weapons are toggled on once and retargeted
  with `UpdateAbilityTargets`, exactly as the client does with beams.
- **Mining** — picks the best asteroid (see below), closes to the standoff, locks it, and
  fires your mining laser *and* your cannons. Optionally also orders a mining ship, which
  costs resources.

Both modes take loot automatically, and give up on a target they've failed to close on for
30 seconds.

## How the bot decides

The rules below live in `Bot/FarmBot.*.cs` — one `partial class` per concern: `Mining`,
`Combat`, `Firing`, `Navigation`, `Hangar`, `Follow`, `Traffic`, `Diagnostics`, plus the tick
loop itself in `FarmBot.cs`. Every tunable number is on `BotTuning`, reached as `bot.T`.

Where a number is a **guess** rather than something read off the wire, it says so — those are
the ones to tune first.

**Settings are per ship.** A `ShipProfile` carries a `BotTuning`, the slot descriptions and the
learned ability ids, and the SHIP dropdown switches between them. A Raptor and a Vanir disagree
about nearly every number here, so one global set could only ever be right for one of them.

### Which weapons fire

Roles come from the server's per-slot stat stream: a slot with `MiningMaxRange`/`DamageMining`
is `Mining`, one with `CannonMaxRange`/`DamageHigh` is `Combat`, one with damage but no range
is `Utility`, and the scanner is confirmed by watching a scan reply come back for the rock you
cast at. An ability you've only ever been *seen* firing stays `Unknown` and is eligible for
both shooting roles.

- **Combat mode** fires everything in `For(Combat)` — which includes `Unknown`.
- **Mining mode** fires the mining lasers *plus* the combat guns (`Guns on rocks`, on by
  default). Cannons break rocks perfectly well. The lasers alone still decide *where to sit*,
  because a 600u cannon's reach would otherwise park the ship outside a 300u laser's range.
- **Repair** abilities are never fired at anything. They're learned by watching you cast an
  ability **on your own ship** — nothing else in the game does that — and cast at yourself
  when hurt.

### When it opens fire

A weapon fires when the target is inside its `MaxRange`, outside its `MinRange`, and the power
pool covers its `PowerPointCost`. While still flying in, it *additionally* waits for its own
`OptimalRange` (`Hold for optimal`). A Tornado-P reaches 600u but is quoted at 250u, so
opening up at 600 spends most of a power bar on the worst end of the accuracy curve. Once the
ship has stopped closing — auto-approach off, or already parked — the gate lifts, because a
weapon holding out for a range it will never reach is a weapon that never fires.

### When it scans

All of these must hold, or the sweep does nothing:

1. The scanner ability is known (else it probes utility slots to find it).
2. Its cooldown has elapsed.
3. A resource filter is set — with `MINE: Any`, nothing reads the answer.
4. **Fewer than `ScanQueueDepth` (2) confirmed rocks are queued.** A confirmed rock is one
   with a fresh scan, holding the resource you asked for, not empty, not on cooldown, and not
   inside an enemy station's envelope.
5. Power is above `cost + 25%` of the pool, so a scan can't starve the lasers.

A scan is trusted for `ScanFreshnessSeconds` (900). Past that the rock counts as unknown
again, because the server respawns asteroid resources on a timer and may pick a different one.

### How it approaches

Standoff is `AsteroidStandoff` past a rock's surface (default 120u, its radius added on top),
otherwise the shortest `OptimalRange` among the guns in play, floored by the target's own radius ×
`RadiusClearance` — every distance on the wire is centre-to-centre, and a rock is not a point.

Throttle is full until `BrakingSeconds` (1.6) of travel from the stopping point, then tapers as
`√t` so it holds speed through most of the zone and brakes hard at the end.

Boost engages whenever there is room to use it and still arrive under control — the braking zone
plus `BoostShedSeconds` (1.5) of boost travel to come down off it, both measured at boost speed.
On a typical ship that is around 450u to a rock, so ordinary asteroid hops boost. It drops back to
cruise before braking starts, and instantly if anything is in the way. The diagnostics `boost`
line shows the distance it works out to for your ship.

Obstacle avoidance sizes bodies by what they are, not by one flat number:

| Body | Treated as | Knob |
|---|---|---|
| Asteroid | `radius × 0.9 + max(40u, hull)` | `AsteroidCollisionMargin` |
| Planetoid | `900u + max(120u, hull × 2)` | `PlanetoidCollisionMargin` |
| Ships, stations, debris | `radius + max(130u, hull)` | `CollisionMargin` |

The ×0.9 is the collider the server actually builds for a rock, so the margin only has to cover
your own hull. A flat margin made small rocks four or five times their real size — enough that a
ship in a dense belt is permanently inside somebody's exclusion sphere, braking and steering
around rocks it had already cleared.

A planetoid's 900u is not read off the wire and is not scaled by anything. Every planetoid on the
server gets the same hard-coded 900u sphere (`SpaceObjectFactory.createPlanetoid`); the "radius"
the wire carries is the client's model *scale factor*, around 1, and trusting it produced a 501u
clearance against a 900u wall.

### How it steers around things

When something solid blocks the direct line, the heading swings out to that body's **tangent** —
the shallowest heading whose entire ray clears the sphere — computed as `asin(1.1 × clearance /
distance)` off the line to its centre, on whichever side the path already leans. It is recomputed
from scratch every tick, so the path curves around the obstacle and snaps back to the target the
moment it is no longer in front. There are no stored waypoints to go stale.

Closer to the wall than that 1.1 margin allows, the turn goes to 108° — past the beam, so the
heading is mostly *around* the body with a component pointing back *out* of it. A flat 90° is the
true tangent and always clears, but it holds the distance constant, so a ship on that heading
circles the body forever instead of leaving.

Inside the clearance sphere there is no "around", only "out": the heading points straight away from
the centre until the ship is clear by `EscapeClearance` (1.25×), which is hysteresis so that
leaving is a decision rather than a boundary case.

> **This replaced a deflection that could not work, and the failure was invisible for months.**
> The old rule aimed at a *point* beside the obstacle — outside the sphere, but reached by a line
> that was not. It left the nose `atan(1.25c / (d + c))` off the direct line when clearing the
> sphere needs `asin(c / d)`; setting the first at least the second solves to **d ≥ 4.56 × c**.
> For a rock (c ≈ 100u) that is ~460u, usually satisfied, and a clip is cheap when it is not. For a
> planetoid (c = 1,400u on a line hull) it is ~6,400u — but `BlockerAhead` never sights one beyond
> about 2,800u, so **against a planetoid the dodge had never once produced a clear line, at any
> range, in any session.** The ship deflected, flew into the wall anyway, braked to
> `MinApproachSpeed`, ground along the surface, penetrated, was shoved out radially, re-aimed, and
> went back in. The 2026-07-29 log shows it exactly: 849u of room, then 455, 199, 47, then thirteen
> consecutive ten-second windows at 0u, then "inside the clearance", then round again — 324 such
> windows in one night.

Braking for an obstacle buys no damage relief — the server's collision formula has no speed term at
all — so its only value is the seconds it buys the turn, and it is skipped outright when the turn
already fits (`BrakingBuysTheTurn`, asteroids only; large bodies subtend too wide an angle for that
test to mean anything). A **planetoid** additionally never brakes below half top speed: its wall
costs no hull whatsoever, so once the turn is ordered there is nothing left to buy, and 8u/s
alongside a 1,400u sphere is a quarter of an hour of sidling.

An asteroid that has spawned inside a planetoid — the resource spawner has no exclusion for the
collider, so this is common — makes that one planetoid **transparent** for as long as it stays the
target. Diving is the honest answer: the direct line always ends in the no-go zone, so avoidance
would orbit the wall until the watchdog gave up on a perfectly good rock, while planetoid contact
costs nothing but a shove once a second. The test is against the clearance sphere *plus our own
standoff*, because what makes a rock unreachable is not where it sits but whether there is anywhere
to park that the avoidance will permit.

### Where it thinks it is

The server states your position only in some messages (`SyncMove`, `Rest`/`Teleport`/`Warp`, a
`WhoIs`); an ordinary flight is heading-and-speed updates, so in between, position is *estimated*.
That estimate drifts while the ship is turning or accelerating.

Before the bot commits to "I've arrived" — cutting the throttle and opening fire — it checks
whether the ship has flown since the server last said where it is. If so it stops and waits up to
`SelfPositionWaitSeconds` (6) for a fresh fix, because coming to rest is what makes the server
send one. A parked ship is never delayed by this: it cannot have drifted. If the server never
answers, it flies on the estimate and says so.

The diagnostics panel shows the error bar directly:

```
position fix   1.8s old, trusted, 3 stop(s) to re-confirm
```

Without it the bot could park motionless next to nothing, status reading `Mining … 146u / 600u`,
while the rock sat behind it — until the 20s stall watchdog gave up on a perfectly good rock.

### What it measures

The diagnostics panel reports three things no item card can tell you, all from the wire:

| Line | Where it comes from |
|---|---|
| `power regen` | Sampling `PowerPoints` over time. The server **never sends regen as a rate** — only updated values — so timing the climb is the only way to know it. Intervals are discarded if anything fired in the last 1.5 s, if a toggle weapon is live, if the pool was capped, or if two samples were more than 6 s apart. |
| `mining rate` | Fitted mining weapons' real `PowerPointCost` / `Cooldown` / damage from the slot-stat stream, against measured regen. Says outright whether you're **power-limited** and how many of your guns the recharge actually feeds. |
| `mined … ore/hour` | `PlayerProtocol Reply.HoldItems` — the server stating what reached your hold. Counts Tylium, Titanium and Water only, so loot and purchases don't inflate it. |
| `time split` | Firing / travelling / holding / idle. |

Ore per hour is the only honest way to compare two hulls, because it already contains the
travel tax — a ship with double the damage at the rock and half the speed can easily come out
behind. **Reset meter** zeroes all of it, so a refit is a clean experiment: reset, mine, compare.

### When it runs

| Trigger | Response |
|---|---|
| Hull below `RepairAtHull` (80%) | Cast the repair module at yourself, on its own reload |
| Hostile within `ThreatRange` (1500u), or anything locked onto you, while mining | Break off and shoot back (`DefendSelf`) |
| Hull below `Retreat at hull %` | Run — to a friendly outpost if one isn't past the threat, else directly away, full throttle and boost |
| Arrived at that outpost | Select it, then dock (`AllowDocking`, on by default). With docking off it shelters under the outpost's guns instead — which is the part that saves the ship |
| Waiting at the outpost with something still shooting | **Circles it** at boost speed instead of parking — inside dock range so retries stay valid, and a much harder target. It never sits at zero throttle under fire |
| The dock still isn't landing | It keeps circling for as long as the hull holds — a post-combat dock cooldown can be tens of seconds. Only if the hull drops `RefugeBleedFraction` (10 points) below its best does it give up on that refuge and run |
| Docked | Repair with titanium, wait `UndockDelaySeconds`, `Room.Quit` back out, resume farming |
| Inside `HostileStationKeepOut` of an enemy platform/outpost | Guns off, nose out, full throttle — this outranks farming |

Enemy weapon platforms and outposts are never targeted (even with `Attack players` on, unless
you put them in the prey list), and no rock or NPC inside their envelope is picked as a target.

> **`HostileStationKeepOut` (2500u) is a guess.** The server publishes slot stats for *your*
> ship only, so the bot cannot know a given platform's real reach. If something still reaches
> you, raise it.

### How it relaunches

Out of the sector — docked, dead, or freshly logged in — one state machine owns the ship
(`FarmBot.Hangar.cs`), in the order the server needs: answer the death screen, repair, wait
`UndockDelaySeconds`, then `Room.Quit`, asking again every `RelaunchIntervalSeconds` until the
ship is back in space.

Three rules bound it. All three were bought with the 2026-07-28 crash: the server left a death
unresolved, the bot re-launched at a client that had already loaded space every 45 seconds for
half an hour until the client died — and the counters that survived that session then got the
next two sessions closed by the server mid-login, in 0 seconds each.

- **The client's own `Game/JumpIn` ends the asking.** It means the client left the room and its
  space level is loaded, so the launch is the server's to finish — anything injected after it
  (another `Room.Quit`, our own `JumpIn`, a re-answered death screen) lands in a client that is
  already in space and only desyncs it. The bot waits up to 15s (`SpawnWaitSeconds`) for the
  server to spawn the ship; healthy launches take 0.1–6.3s.
- **A launch the server never finishes is counted, not retried forever.** No spawn inside the
  15s → the sequence restarts once from `Room.Quit`; after 3 failures (`MaxSpawnWaitFailures`)
  the farm stops and the log says to relog and press Go farm. Grinding on is proven to end with
  a dead client, and a wedge like this is only ever fixed server-side.
- **Nothing is injected without a live, settled session.** A session's end wipes the whole
  hangar sequence — death screens, ask counters, spawn waits — and the machine refuses to run
  until the next client is connected and relaying, with the undock delay counting from that
  moment. The player id alone is no proof of a session: it is seeded from the profile and
  survives a disconnect, which is exactly how "attempt 2" got injected into a login handshake.

## Settings

Toolbar controls, persisted to `bot.json`:

| Control | Effect |
|---|---|
| Fly to target | Steer and open the throttle toward out-of-range targets |
| Auto loot | Request and take loot from wrecks and cargo in reach |
| Attack players | Include player ships, not just NPCs |
| Guns on rocks | Fire combat cannons at asteroids alongside the mining laser |
| Hold for optimal | Wait for each weapon's optimal range while closing |
| Avoid stations | Keep clear of enemy weapon platforms and outposts |
| Self repair | Cast the learned repair ability when hurt |
| Fallback range | Reach assumed for weapons the server gave no range for |
| Retreat at hull % | Run below this fraction of hull |
| Hold off rock | Distance to hold from an asteroid centre |
| Mine | Resource filter; `Any` disables scanning entirely |

More lives in `bot.json` than on the toolbar — `ScanQueueDepth`, `ScanFreshnessSeconds`,
`HostileStationKeepOut`, `RepairAtHull`, `ThreatRange`, `BrakingSeconds`, `FollowDistance`,
`AllowDocking`, `AsteroidCollisionMargin`, `PlanetoidCollisionMargin`, `SelfPositionTrustSeconds`
and the rest are all editable there. Your declared loadout lives there too, per server profile,
under `Slots`.

## Status

Working: proxy relay, batched-frame decode, self-identification, full per-type `WhoIs`
decode, movement tracking with dead reckoning, faction-aware targeting, multi-weapon firing
with real ranges and cooldowns, optimal-range fire discipline, toggle weapons,
approach/steering, target health, auto-loot end to end (`Loot` → `Reply.Loot` →
`TakeLootItems`), resource scanning with a queue gate, self-defence, self-repair, fleeing to
a friendly outpost, enemy-emplacement avoidance, orbiting 3D tactical display with
client-visibility banding and click-to-select, sortable contacts list with real player names,
declared hex loadout with slot-list decode, pinned targets, go-to and follow runs,
launcher session capture, diagnostics panel, docking (select-then-request, with the server's
own docking countdown respected) and the repair-and-relaunch cycle behind it.

Not implemented: sector-to-sector jumping, selling cargo while docked, group/wing behaviour,
missile and flare evasion, power-restore consumables (they cost cubits), server-side
cancellation of a docking countdown.

## Scope

This targets a server you run yourself. Don't point it at someone else's.
