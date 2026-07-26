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
`ShipSlotCard` inside the ship's catalogue card, which this bot does not read. Hence the panel.

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
