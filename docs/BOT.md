# BSGO Farm Bot — how it works and what was fixed

Companion to [GAME.md](GAME.md), which holds the game/protocol facts this behaviour is built on.
For the proxy architecture see [`bot/README.md`](../bot/README.md).

---

## Design rule

**Nothing is hardcoded from the catalogue.** Ability ids, ranges, cooldowns, power costs, speeds
and slot roles are per-ship and per-server, so the bot learns them from traffic:

| Source | Strength | Gives |
|---|---|---|
| **You, in the loadout panel** | settled | what a slot *is* and what to use it for |
| Per-slot stat stream (`StatUpdateType.SlotStat`) | authoritative | ranges, cooldowns, damage, power cost |
| Ship-wide stats (`StatUpdateType.Stat`) | authoritative | max speed, boost speed, max hull/power, detection radii |
| `PlayerProtocol Reply.Slots` | authoritative | the slot list itself: id, installed system guid, loaded consumable, broken or not |
| Server replies (`Reply.Scan`) | proof | which ability is the scanner |
| Your own outgoing casts | weak | ability exists; what you aimed it at |

Weapons are **already auto-discovered** from slot stats — "fire once manually" is only a fallback
for servers that don't publish them.

The design rule still holds with a declared loadout: nothing is hardcoded *from the catalogue*,
because a declaration is per server profile and typed by the person flying the ship. It is not the
bot assuming what a Tornado-P is; it is you saying which slot yours is in.

---

## Weapon roles

`WeaponRole`: `Unknown` · `Combat` · `Mining` · `Scanner` · `Utility` · `Repair`

- `Unknown` — seen you fire it, no stats. Still fired (an ability you used does *something*).
- `Utility` — stats exist but show no damage and no weapon range. **Never fired as a weapon**, but
  it's the pool the scanner probe draws from.
- `Scanner` — proven by a `Reply.Scan` correlated to a cast. Outranks stat classification.

Slot stats override anything inferred, except a confirmed scanner (that came from a reply, not a
guess) and except demoting an already-classified weapon to `Utility` on a partial stat sweep.

### A declared role is final

`Weapon.RoleFromUser` is the top of that precedence chain. `RefreshFromStats` will not revise it,
`MarkScanner` will not override it, and `ProbeCandidates` skips it entirely — so declaring your
loadout also stops the scanner probe from spending consumables on slots you have identified.

The reason the user outranks the server here, and only here, is that the question is genuinely
unanswerable from the wire. Damage control, an armour plate and the scanner all publish no damage
and no weapon range. Their stats are identical. Only the item card tells them apart, and only you
can see it.

Numbers go the other way: `Weapon` keeps `Stat*` and `User*` separately and reads
`Stat ?? User`. What the server publishes is what it will enforce when the bot pulls the trigger,
so it wins; what you type fills the gaps on servers that publish nothing.

---

## Reading the loadout off the wire

`PlayerProtocol Reply.Slots` (client: `ShipSlot.Read`) is the only message that states the slot
list outright:

```
uint16 shipId, uint16 count,
count x { uint16 slotId, item, uint32 consumableGuid, bool inoperable }
```

The `uint16` in front of each entry is the item's own `ServerID`, which the server sets to the
slot id — the same shape `ItemFactory.ReadItemWithID` uses for inventory lists, which is why it
sits outside the item body.

Everything else the bot knows about slots is inferred, and every inference has the same blind
spot: it only ever sees the slots that publish something. A hull-repair module that costs power
and deals no damage appears in neither the stat stream nor your fire traffic until you press it.
`Reply.Slots` shows it from login.

What it does **not** carry is the slot's *type* — gun, hull, computer. That lives in
`ShipSlotCard` inside the ship's catalogue card — which the bot now **does** read, see
[Catalogue](#catalogue) below. The panel remains as an override for servers that publish no
catalogue, and for the one question a card genuinely cannot answer: which slot is which in
*your* UI.

### Ability ids start at zero

On this server they do, and a real mining laser sits at id **0**. That broke everything that
used `0` to mean "this hex is not bound yet" — the id could not be typed, bound by firing,
saved, or test-fired. `SavedSlot.SlotId` is now an `int` with **-1** for unbound, and
`SavedSlot.Bound` is the test. Old profiles migrate on load: a hex holding `0` with no name and
no role was certainly unbound and becomes `-1`; one that was actually described keeps its id.

### Which ship's slots

`Reply.Slots` is keyed by a ship id that does not have to match `Reply.ActiveShip`, and
`Reply.AddShip` creates a hangar entry per ship you own. Matching on the id alone therefore
picks an entry that may hold nothing, and every slot then reads as empty — which in turn made
`CastAllowed` refuse every weapon on the ship.

`MyLoadout` now prefers, in order: the id match **if it has fitted slots**, then whichever entry
has the most fitted slots, then the old rules. `HangarSummary()` reports all of them, and the
first slot list that actually has content is dumped to the log with each slot's id, system guid
and resolved ability action.

### Declaring a loadout switches off the guessing

`WeaponBook.For(role)` treats a roleless remembered ability as a weapon **only while nothing has
been declared**. Ability ids persist in `bot.json` across refits and across ships, so that
fallback had turned into a dozen stale ids being fired at every target. Declare one slot and the
declaration becomes the list.

`Fill from game` in the slot editor reads the numbers out of the catalogue rather than asking
you for them — see below.

---

## Catalogue

The server ships a card database to the client on demand: `CatalogueProtocol Reply.Card` carries
`uint32 cardGuid, uint16 cardView, <body>`. All of it passes through the proxy, so the bot reads
it for free — and asks for more, because the client only ever fetches what it is about to draw.

One guid answers to several **views**. The guid arrives in every `WhoIs` (the second one —
`SpaceObject.BaseRead`'s `objectGUID`), so seeing an object is enough to look it up:

| View | Carries |
|---|---|
| `Ship` (10) | tier, roles, durability, **every slot's type**, immutable slots, and the full stat block — MaxHullPoints, Avoidance, ArmorValue, Accuracy |
| `World` (4) | prefab name, radius, and `SpotDesc[]`: each hardpoint's local position and rotation |
| `ShipSystem` (2) | the installable item, and the ability guids it grants |
| `ShipAbility` (6) | `ItemBuffAdd` — the exact block the server reads for cooldown, power cost, ranges, firing **angle** and damage |
| `ShipList` (25) | a faction's whole roster, as guid pairs |

`StaticCards` hardcodes the roster guids — Colonial `73551268`, Cylon `188756164` — so two
requests at login enumerate every player-flyable hull, and each of those cascades into its world,
system and ability cards.

This is why the bot no longer has to be shot by something to learn what it is.

### Rules it follows

- **Raw first.** Every body is cached verbatim under `cards/<host>_<port>.json`, whether or not a
  parser exists for that view. A layout transcribed later applies to cards already on disk.
- **Per server.** Card guids only mean something on the server that issued them. Mixing two
  servers' catalogues would produce confidently wrong numbers, which is worse than none.
- **Leftover bytes void the parse.** If a body doesn't consume exactly, the values are discarded
  and only the bytes kept. An appended field is harmless and an inserted one corrupts everything
  after it, and nothing in the message distinguishes them — so a suspect hull figure never
  reaches a combat decision. Same rule `WhoIsReader` applies to half-parsed objects.
- **Small batches, slow clock.** Requests drain on their own 2s timer, ≤24 per message, off the
  farm loop — it must keep working while you fly manually. Replies also reach the real client,
  which simply caches a card it did not ask for.

`FetchCatalogue = false` turns off the injected requests and leaves passive sniffing running.
`PrefetchRosters()` pulls both faction rosters — thousands of cards, so it is opt-in and meant
for a quiet moment at dock, never a login.

### Filling a slot from the catalogue

`slot id → fitted system guid → ShipSystem card → ShipAbility card`, and the ability's
`ItemBuffAdd` is the same block the server reads when deciding whether a shot is in range and
what it costs. Not an estimate that agrees with the game — the numbers the game enforces.

Worked example, a level 6 mining laser as the server describes it:

| Field | Typed by hand | Catalogue |
|---|---|---|
| Max range | 350 | **600** |
| Optimal range | 250 | **305.6** |
| Min range | 150 | **0** |
| Reload | 0.5 | 0.5 |
| Power | 2 | 2 |
| Role | *guessed* | `ActionType 8` = FireMining |

It also carries the firing arc (37.5°) and accuracy, neither of which the form ever asked for.
Every wrong number above costs range the ship actually has.

`Fill from game` fills blanks only; Shift-click overwrites.

---

### Scanner identification

Nothing on the wire names the scanner — it has no damage stat, so it looks like any other utility
slot. So the bot **probes**: casts each unclassified ability once at a rock, 3s apart, and keeps
whichever one the server answers with a scan.

Guards:
- Only utility slots that **publish a `MaxRange`** are probed. Scanning reaches out to a rock; a
  self-buff or charge item has no range. This stops it burning your limited consumables.
- A slot the stats already proved is a weapon can never be relabelled by a stray scan reply.
- Scanning one rock by hand identifies it instantly — same mechanism, no probing needed.

### Area vs single-target

Decided from **proof only**, because guessing wrong writes a cheat entry in the server log:

- more than one id in one cast → **Area**
- exactly one id while ≥2 valid rocks were in reach → **Selected**

Until proven, the bot stays on the safe single-target path. Once Area is proven it batches every
rock in radius into one cast (capped at `MaxAreaScanTargets`, default 32), exactly as
`GetObjectsWithinAOE` does.

---

## Movement

### Speed

`TopSpeed` = `max(ObjectStat.Speed, fastest throttle we've watched you send)`, falling back to
`FallbackSpeed` (100). Sent as `SetSpeed(Abs, TopSpeed)`.

Boost engages while `distance > standoff + BoostMargin` (1500u), drops to Regular on arrival.
Speed is always sent **before** gear, because `SetGear(Regular)` re-applies the stored throttle.

### Standoff — where it stops

| Target | Distance |
|---|---|
| Asteroid | `AsteroidStandoff` (default **179u**), clamped to `reach × 0.95` |
| Planetoid | `PlanetoidStandoff` (default **1200u**), not clamped |
| Moving target | `optimal × CloseInFactor` (0.6), floored by `radius × 3 + 150` |
| Static target | full `optimal`, floored the same way |

Explicit numbers beat derived ones — the published radius is a bounding figure, not the visual
hull. Accuracy is flat at or below optimal, so a low standoff costs nothing (see GAME.md §6).

### Braking

Speed tapers over the last `BrakingDistance` (700u), arriving at `MinApproachSpeed` (8u/s).
**Heading is rate-limited to 400ms; the throttle is not** — braking must react every tick.

> This was the fix for flying into asteroids at full speed. Full throttle to the stop point then
> cutting the engine just means coasting through the rock.

### Watchdog

A target we haven't closed on for 30s is skipped for 2 minutes — geometry, anchoring or a tow.

---

## Farm loop (250ms tick)

```
proxy connected? → stats sweep (2s) → know my ship? → know my position?
  → docking?  → DockTick, return
  → hull < RetreatHull?  → disengage
  → AutoLoot  → sweep loot in reach
  → Mining or Combat tick
```

### Combat

Target = nearest matching `CombatCandidate` (NPC combatants by default; players only with
`AttackPlayers`; `Prey` narrows by type; enemy or neutral relation).

Fires at anything inside max range, but **keeps closing to the standoff while firing** rather than
parking at the edge of range.

### Mining

1. **Scan sweep** — one cast per cooldown, or one batched area cast.
2. **Target** — prefers rocks with a fresh scan, ranked `ResourceCount / (1 + distance/1000)`
   (richest per unit of travel). Falls back to unscanned rocks so the ship keeps moving into
   scanner range instead of parking.
3. **Hold fire on unknown rocks** when a resource filter is set — breaking open a titanium rock in
   water mode is exactly what the filter is meant to prevent. Holding also lets power rebuild for
   the scan.
4. **Fire** — mining lasers if known, otherwise your cannons.

Scanning is skipped entirely when `MINE = Any` (`ScanOnlyWhenFiltering`) — 50 power for an answer
nothing reads. Scans also require `cost + ScanPowerReserve × maxPower` free.

Scan results expire after `ScanFreshnessSeconds` (180) — contents respawn and re-roll.

---

## Docking

**Dock** stops the farm, picks the nearest friendly/neutral Outpost or Cruiser, flies there with
the same braking approach, and requests only once genuinely close — rate-limited to one attempt
per 4s, because an over-range attempt is logged as cheating.

It **learns**: when you dock manually the bot records the distance and uses 90% of it thereafter.

**Undock** sends `JumpIn`. One message, no approach.

---

## Settings

Toolbar: mode (Combat/Mining) · Fly to target · Boost · Auto loot · Attack players · Fallback reach
· Retreat at hull · Hold off rock · Mine (resource)

`bot.json` holds the rest. Notable:

| Setting | Default | Meaning |
|---|---|---|
| `AsteroidStandoff` | 179 | hold distance from a rock |
| `PlanetoidStandoff` | 1200 | hold distance from a planetoid |
| `FallbackSpeed` | 100 | throttle when Speed stat unknown |
| `BoostMargin` | 1500 | boost only beyond standoff + this |
| `RetreatHull` | 0.25 | disengage below this **fraction** |
| `ScanOnlyWhenFiltering` | true | don't scan when mining anything |
| `ScanPowerReserve` | 0.25 | keep this fraction of max power back |
| `ScanFreshnessSeconds` | 180 | how long a scan is trusted |
| `MaxAreaScanTargets` | 32 | cap on ids in one area cast |

---

## Bugs fixed (and what they teach)

| Bug | Cause | Lesson |
|---|---|---|
| Ship crawled at 1.0 u/s | `SetSpeed(Full, 1f)` — no server reads the mode byte, the client resolves Full to MaxSpeed itself | read the *server's* handler, not the enum name |
| Flew into asteroids and died | full throttle to the stop point; throttle updates stuck behind the 400ms steering limit; centre-to-centre distance ignored radius | arriving is a manoeuvre, not a teleport |
| Fired from 600u and missed | used `MaxRange` as both "can shoot" and "how close to get"; optimal was 250 | reach ≠ preferred range |
| Mining mode never fired | mining demanded a Mining-role weapon; an autocannon breaks rocks fine | don't refuse work over a label |
| Mining laser never learned | `Observe()` hardcoded `Role = Combat` for anything you fired | don't assume a default that can't be corrected |
| Scanner never found | `RefreshFromStats` **dropped** slots it couldn't classify, so the scanner never entered the book and the probe searched an empty list | "not a weapon" ≠ "not worth recording" |
| **Retreat never triggered** | `MyHull < RetreatHull` compared 495 points against 0.25 | never compare a ratio with a magnitude |
| Would have rejected every rock | gated on scan's `isMinable`, which is planetoid-only | check what a flag *means*, not what it's called |
| Would mine the wrong resource | filter allowed unscanned rocks to be shot | a filter must gate the action, not just the search |
| `.exe` needed a "runtime install" | `dotnet build --no-incremental` while the bot ran wiped the output dir, deleting `runtimeconfig.json` but failing to restore it | plain `dotnet build` fails harmlessly; the clean flag doesn't |
| **Server dropped the connection ~50s into every fight** | `For(role)` treated every roleless remembered ability as a weapon, so a dozen ids left over from other ships were fired at each target — including slots holding an engine or an armour plate | a fallback for "we know nothing" must switch off the moment we know something |
| Ability id 0 could not be bound, saved or test-fired | `0` was the sentinel for "this hex is unbound", and ability ids on this server start at 0 — so a real mining laser was unrepresentable | a sentinel has to be a value the data cannot take |
| Every weapon refused, mining included | `CastAllowed` trusted a slot list in which nothing was fitted — it was the wrong hangar entry | a list that describes nothing gets to veto nothing |
| Wrong ship's slots | `MyLoadout` matched on ship id, then fell back to "the only hangar entry", neither of which checks the entry has any hardware in it | prefer the record that has content over the one with the matching key |
| Contact buttons silently did nothing | `_ = what(id)` started the task without awaiting it, so "no session" was thrown *inside* the task and the surrounding try/catch never saw it | fire-and-forget discards failures as well as results |
| Nine casts in three milliseconds | `FireAll` fired every gun in one pass | no human client produces that pattern |

---

## Combat telemetry

Two messages the server was already sending and the bot was throwing away.

**`Reply.CombatInfo`** — one per hit involving us:

```
bool dmgIsFromMe, uint32 objectId, float value, byte flags   (1 = destroyed, 2 = critical)
```

`value` is **signed**: negative is damage, positive is a repair. The client branches on the sign
(`GameProtocol.cs:807`), which is the only reason we know the positive case exists.

**`Reply.WeaponShot`** — broadcast for every discharge in the sector, including fights we are not
in: `uint32 shooter, uint16 hardpointHash, uint32 target, byte fxType`.

`CombatLog` turns those into, per **ship class** rather than per contact — individual NPCs are
disposable, their card guid is not:

- damage dealt and taken, crits, kills, deaths
- **damage per kill**, measured. A card's hull figure is not what a fight costs; armour and
  resistances sit in between
- **hit rate by range**. `NoteShotFired` records the distance at the moment of firing, because
  the damage report carries no range. A hit resolves against the oldest pending shot at that
  target; silence past 2.5s books a miss — a miss produces no message at all, so silence is the
  only evidence there is
- **incoming hits bucketed by our own throttle**, to test whether avoidance really does scale
  with speed on this server
- **median enemy re-target interval**, from watching who shoots whom

Nothing here is a constant borrowed from another server's source. That distinction is the whole
point: `bsgocore` is a hypothesis generator, never a number.

---

## When the session drops

The proxy used to log `Client disconnected` whichever side hung up, which made every theory
about disconnects unfalsifiable. It now names the side and, when the **server** was the one that
closed, dumps the last 16 frames we injected:

```
Session ended — the server closed the connection after 48s (265 up, 17026 down, 79 injected).
Last 16 frame(s) we injected, oldest first — the last one is the prime suspect:
    19:32:58.436 Game/21 (11b) 021500040001000B000045
```

Server-side means the cause is something we sent. Client-side means the game exited and says
nothing about our traffic.

Everything also goes to `bin/Debug/net9.0-windows/logs/bot-<date>.log`. The panel keeps a bounded
buffer and used to be unselectable; it is a real text control now — drag-select, Ctrl+C, Ctrl+A,
and a right-click menu.

### Guards this produced

| Guard | Rule |
|---|---|
| `MaxCastsPerTick` | 2 casts per 250ms tick. Firing every gun at once put nine on the wire inside 3ms |
| `CastAllowed` | refuses ids absent from the server's slot list, and empty or broken slots — unless the whole list is unfitted, in which case it is ignored |
| catalogue role check | refuses to cast anything whose `ActionType` is not offensive, so a scanner or a repair module is never aimed at a target |

---

## Diagnostics

The panel is the first place to look:

```
my ship        #4101940A Colonial/Group0
hull           430 / 495 (87%)
power          64 / 150 (43%)
throttle       55u/s (ship stat), boost 85u/s
flying         55u/s in Regular
combat reach   750u, sit at 180u + target size
hold off       asteroid 179u, planetoid 1200u
mining reach   600u
scanner        ability #7, area, reach 300u, costs 50 power — ready
rock contents  9 known, 145 unknown (12 scans sent)
```

`scanner  not found — 3 ability(s) left to test` means probing is still running. If it ends with
*"none of yours answered a scan"*, no scanner is fitted.

Three casts with no reply logs **"most likely out of power cells"** — a missing consumable is
refused with no reply at all, so running dry is otherwise invisible.

---

## Building

```powershell
# close bsgobot.exe first — it locks its own .exe
dotnet build "bot\BsgoBot\BsgoBot.csproj"
```

Never use `--no-incremental` while it's running (see the bug table).
