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

**That second step is a guess, and guesses may not destroy anything.** On an account owning a
Vanir and a Raptor, "most fitted slots" answers *Vanir* whichever hull is in space. The refit
sweep — which withdraws a declaration when the slot's catalogue guid disagrees with what you
described — was reading it, so every declaration belonging to the other ship disagreed and all of
them were withdrawn, including the scanner's role and its published reach. The bot then had no
scanner and went back to test-firing utility slots hunting for one.

Anything destructive asks `ConfirmedLoadout` instead, which returns the id-matched entry or
nothing at all. A guess may inform a picture; it may not invalidate something you typed.

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

Speed is always sent **before** gear, because `SetGear(Regular)` re-applies the stored throttle.

Boost engages while the target is further out than the **runway** — the room needed to arrive
under control, measured at boost speed:

```
runway = standoff + clamp(BoostSpeed × BrakingSeconds, 120, 700) + BoostSpeed × BoostShedSeconds
```

Both terms are measured in the *fast* gear on purpose: the question is "is there room to run fast
and still stop", so it cannot be answered against the cruise the ship happens to be in while
asking. Boost is also dropped outright the moment anything is in the way (`blocker is not null`),
and the braking zone and obstacle lookahead are both sized from `SpeedInGear(gear)`, so they widen
with it.

> This replaced a flat `BoostMargin` of 1,500u, which asked the wrong question. A rock's standoff
> is ~170u, so nothing closer than 1,670u ever boosted — and no asteroid hop in a belt is that
> long. In one 48-minute session every mining approach (441u … 1,382u) ran at cruise, and the only
> two boosts were retreats across the sector. The real requirement is ~450u on the same ship, so
> the margin was about three times the thing it was guarding against. **A safety number nobody has
> measured tends to be sized by imagination, and imagination is expensive.**

### Where the ship thinks it is

Only some messages state our position outright: `SyncMove`, the `Rest` / `Teleport` / `Warp`
maneuvers, and a `WhoIs`. A normal approach is a stream of `Directional` maneuvers — heading and
march speed, no position — so **between fixes the ship's position is integrated, not known**. The
model flies the ordered heading in a straight line at the ordered speed while the real ship arcs
through its turn and takes time to reach it, so the error grows for as long as the flight lasts.

`WorldState.MyFixAt` records when the server last *stated* where we are, as distinct from where
dead reckoning has got to. `MyFixAgeSeconds` is therefore the error bar on every distance the bot
acts on, and the diagnostics panel prints it:

```
position fix   1.8s old, trusted, 3 stop(s) to re-confirm
position fix   6.2s old, FLOWN SINCE — distances unproven, 3 stop(s) to re-confirm
```

Before the mining loop commits to "I have arrived" — cutting the throttle, locking, opening fire —
it tests that belief. If the ship has flown since the last fix it stops and waits up to
`SelfPositionWaitSeconds` (6) for a fresh one. **Stopping is not a delay, it is the question:**
coming to rest is what makes the server broadcast a `Rest` maneuver, and a `Rest` states a
position. It is also exactly what the caller was about to do anyway.

Two rules keep this from becoming a stutter:

- The test is "have we flown since the server last told us", **not** "is the fix old". A ship
  parked on a rock for two minutes has a two-minute-old fix and is precisely where it says.
- A server that never answers cannot park the bot: after the wait it flies on the estimate and
  says so once.

> This was the fix for a ship sitting motionless with the status line reading
> `Mining #070000ED — 146u / 600u` while the rock was behind and to the left. The modelled
> distance crossed inside mining range before the real one did, the throttle came off, and the
> lasers fired at something the server considered out of reach — silently, because an out-of-range
> cast is refused without a reply. Nothing recovered until the 20s stall watchdog gave up on a
> rock that was never the problem.

`ApplyWhoIs` also updates `MyPosition` when the WhoIs is about our own ship. It used to write the
object's position and leave `MyPosition` on the older reading — a free fix thrown away, and the
first one to arrive after a jump.

### Standoff — where it stops

| Target | Distance |
|---|---|
| Asteroid | the mining weapon's **optimal band**, floored by `AsteroidStandoff` + the rock's radius and by its clearance sphere, clamped to `reach × 0.95` |
| Planetoid | `PlanetoidStandoff` (default **1200u**), not clamped, floored by its clearance |
| Moving target | `optimal × CloseInFactor` (0.6), floored by `radius × 3 + 150` |
| Static target | full `optimal`, floored the same way |

`AsteroidStandoff` is a **floor**, not a destination. It used to be the destination, which flew a
Vanir to 307u of a rock its Badgers reach at 1,350u — a thousand units of travel into a rock
field for nothing. Accuracy is flat at or below optimal (GAME.md §6), so a shot from the edge of
the band lands as often as one from arm's length, and the band is where to sit. Strike ships are
unaffected in kind: a Gopher's 350u optimal is under the floor on any decent rock.

An unpublished optimal keeps the old close-in behaviour. `PreferredRange` falls back to **max**
range when optimal is unknown, and max is the one answer that must never be chosen — hit chance
falls off past optimal, so parking at 95% of reach is parking where the shots miss.

### Firing arc

In range is not the same as able to shoot. Every weapon has an arc the server enforces
(`Algorithm3D.isWeaponPositionInRange` takes the ability's `Angle`), and an out-of-arc cast is
refused in **exactly the same silence** as a cast at a rock that no longer exists. The two are
indistinguishable from the reply, so a rock 500u off the beam was being written off as gone.

Holding station therefore means holding the nose on the target: an out-of-arc rock keeps
steering at the standoff already occupied, which turns the ship without driving it anywhere. The
stall watchdog checks the arc before condemning anything. The narrowest fitted arc decides it —
a gun set is only on target when all of them bear.

This needs `WorldState.MyFacing`, which comes from the heading itself rather than the velocity
vector: mining is done stopped, and a ship at rest has no velocity to infer a facing from.

### Clearance — how big a thing is treated as

Parking beside a body and threading between bodies are different questions, so `ClearanceOf` (the
no-go sphere used by collision avoidance, braking and the standoff floor) is **not** the ×3
`RadiusClearance` used for standoffs. It is also split by type, because one number cannot serve a
range spanning two orders of magnitude:

| Body | Clearance | Why |
|---|---|---|
| Asteroid | `radius × 0.9 + max(AsteroidCollisionMargin 40, our hull)` | the server builds an asteroid's collider as `radius × 0.9` (`SpaceObjectFactory.createAsteroid`), so the published radius is already generous; the margin only has to cover our own hull |
| Planetoid | `900 + max(PlanetoidCollisionMargin 120, our hull × 2)` | the server's collider is a **fixed 900u sphere** for every planetoid; the wire radius is a model scale factor, not a size — see the planetoid section below |
| Everything else | `radius + max(CollisionMargin 70, our hull)` | ships, stations, debris |

**"Our hull" is measured, not published.** `Reply.WhoIs` carries a radius for asteroids,
planetoids, planets, triggers and volumes — and for **no ship of any kind, ours included**. Real
collision runs off per-prefab collider templates that never reach the wire and are not spheres
(Galactica's is a box of 200 × 75 × 600 half-extents). So it is worked out:

```
HullRadius (typed in)                              — wins outright, but see below
max(card.Radius, furthest hardpoint) × HullMargin  — the normal path
```

The **hardpoint spread** is the real measurement: the World card lists every mount with a
`LocalPosition` relative to centre, and the server computes weapon range from those same
positions. It is a **lower bound** — the hull carries on past its outermost gun.

The **margin** is what corrects for that, and it scales with class, so it is a multiplier rather
than a flat addition: a strike craft is barely longer than the span of its own mounts and gets
**1.0**; an escort `HullMarginEscort` (1.3); a line or capital hull `HullMarginLine` (1.6),
because a line ship is mostly hull with a few guns on it.

Class comes from the hull card's `Tier` (1 strike, 2 escort, 3 line, 4 capital), overridable with
`ShipTierOverride` for when card fetching is off. **An unknown class applies no margin** — that
keeps the old behaviour rather than inflating every clearance on a guess.

`HullRadius` exists but is a poor thing to ask for: the game never shows a half-size anywhere, so
the source that beats every guess is one the player generally cannot supply. Prefer setting the
class.

The `card.Radius` fallback deserves suspicion. In `ReadWorld` that field sits in the presentation
block beside `SystemMapTexture`, `FrameIndex` and `ForceShowOnMap`, so it is very likely a map
icon scale rather than a hull dimension — it reads ~8u for a Raptor and ~35u for a Vanir, both
smaller than the asteroids being dodged. If the diagnostics line says the radius was used rather
than the hardpoints, treat the number as meaningless.

The `× 2` above was calibrated when that number was always ~35, so `35 × 2` landed on the 70u the
margin already defaulted to and the term never bit. With a real half-size it does, and one radius
is what the geometry asks: a hull rotating on the spot sweeps a sphere of its own half-length.

### Planetoids — a fixed 900u wall, and when to dive through it

Three facts from the server and client sources, each carrying a prediction that the logs can
falsify. If flying near planetoids goes wrong again, check these first — one of them being wrong
for this server build is the likely cause.

**1. Every planetoid's collider is a 900u sphere at its centre.** Hard-coded in bsgocore
`SpaceObjectFactory.createPlanetoid`: `new SphereCollider(transform, zero, 900)` — the same 900
whatever the body looks like. The visible surface is the *model*, scaled by the wire "radius"
(see 2), so what you can fly through on screen and what stops you were never going to match.
*Prediction: the ship is stopped at ~900u from a planetoid's centre and nowhere else. A grind at
some other centre distance means this constant is wrong.*

**2. The radius on the wire is a scale factor, not a size.** Client `Planetoid.Read` feeds it
straight into `localScale` — it is a number like 0.8, and treating it as units is what produced
the old `Inside Planetoid #0E000007's clearance (501u)` lines: a clearance of half the real
wall, so the bot ground against "empty" space. `RadiusOf` now ignores it for planetoids and uses
the 900 constant. *Prediction: every planetoid dodge log now quotes a clearance of 900 + margin
(~1,020u on a strike hull, more on a line hull). A 501u figure coming back means the constant
stopped being applied.*

**3. Hitting a planetoid costs nothing but time.** `CollisionResolution` has **no damage path**
for ship × planetoid — contact answers with a `PulseManeuver` shove along the surface normal,
rate-limited to once a second (10 ticks) and capped at `boostSpeed × 4`. A ship that keeps its
throttle open out-pushes a once-a-second pulse more often than not — which is why the old bot
got inside "3/5 of the time" by accident. *Prediction: diving costs zero hull. Hull dropping on
planetoid contact means this is wrong and diving must stop.*

Two behaviours follow:

- **Navigation** treats the planetoid as the 900u sphere plus a rock-sized margin — no more
  oversized 1,500u+ guesses, no more undersized 501u ones. Braking stays unconditional for
  planetoids (the turn-fits test is a fair proxy for a rock and badly wrong for a body this
  size).
- **A rock inside the sphere is dived for, on purpose** (`TargetBuriedIn` / `_diveThrough`).
  Rocks do spawn inside planetoids, and a target inside the clearance sphere is one the
  avoidance can never reach — the direct line always ends in the no-go zone, so the ship orbits
  the wall until the watchdog skips a perfectly minable rock. Since contact is free (fact 3),
  the honest answer is to make that one planetoid transparent — no dodge, no escape — and keep
  the throttle open. The log says `... sits inside Planetoid #...'s collider — diving straight
  in`. The 30s approach watchdog stays armed as the backstop for when the shove wins anyway;
  fleeing (`RunInDirection`) never dives.

### Collisions are worth costing, not always avoiding

`0.5 × the asteroid's max hull points`, reduced by armour only where armour exceeds the
collision's armour piercing of 50 — which a 40-armour line hull does not
(`DamageCalculator.calculateDamageFromCollision`). **There is no speed term anywhere in it.**

Two things follow. Braking buys no damage relief at all, only the seconds a turn needs — so it
now happens only when it buys that turn, and only for asteroids (the angle test is a fair proxy
for a rock and badly wrong for a planetoid, which keeps braking unconditionally). And whether a
rock is worth avoiding is a comparison, not a yes/no: under `IgnoreCollisionHullFraction` of max
hull it is flown through. A Vanir carries ~4,500 hull and recovers 35/s, so a small rock costs it
three seconds of regeneration while turning a 27 m/s hull around it costs far longer. The
threshold is a fraction of our own hull, so it scales across ships without a second setting.

Hull points arrive only for objects we have **subscribed** to — in practice the rock being mined
— so the rocks actually in the way publish a radius and nothing else. The radius-to-hull
conversion is measured off the rocks we do know, applied at the 90th percentile, and nothing is
skipped until five rocks have been measured. Never skipped on a guess.

> A flat `+70u` made an 18u pebble an 88u no-go sphere — five times its own size — and in a belt
> of those the ship is permanently inside somebody's, braking and steering around rocks it had
> already cleared. The same `+70u` on a 1,500u planetoid is 4% of its radius, which is no margin
> at all on something you meet at 80u/s. Same constant, opposite errors.

### Braking

Speed tapers over the last `BrakingDistance` (700u), arriving at `MinApproachSpeed` (8u/s).
**Heading is rate-limited to 400ms; the throttle is not** — braking must react every tick.

> This was the fix for flying into asteroids at full speed. Full throttle to the stop point then
> cutting the engine just means coasting through the rock.

### Watchdog

A target we haven't closed on for 30s is skipped for 2 minutes — geometry, anchoring or a tow.

---

## Choosing a rock, and keeping it

Two different questions, and conflating them causes churn.

**Choosing** prefers a confirmed-and-wanted rock over a nearer unconfirmed one, within
`LocalRadius`. That radius used to be the scanner's reach, on the reasoning that everything
inside it is knowable from where we stand — an argument about *knowledge* deciding *travel*. The
two only coincide on a ship whose scanner and legs are matched: a declared 4,000u scanner made
the whole belt "local", so a confirmed rock 3,500u out beat an unscanned one 200u away, which at
27 m/s is a two-minute flight to save one scan. It is now also capped by how far the hull travels
in `LocalTravelSeconds`, so it scales with speed.

Choosing also skips rocks with a **hostile that moves** within `HostileShipKeepOut` — drones and
NPC fighters, kept apart from `AvoidHostileStations` because the two want opposite handling. A
platform is a *place*: back out of its envelope and it is solved. A drone is not a place, and
backing away solves nothing because it follows. So it steers selection rather than triggering an
escape; being shot at remains `IsThreat`'s business. Judged on predicted positions, and measured
**drone-to-rock**, not drone-to-ship — the question is whether the rock has company, since that
is where the ship parks motionless for twenty seconds.

**Keeping** is deliberately looser and goes straight to `MiningCandidate`, without the keep-out. A
drone's whereabouts changes every second, so applying it to retention made a rock thirty seconds
into being broken stop qualifying because something drifted past — and the ship left, banking
nothing, with the answer flipping back moments later. A worked rock is finished, not churned.

Abandoning a live target logs a line naming it and how much damage went into it. That was silent,
which is why a rock left half broken read identically to one that was finished.

## Farm loop (250ms tick)

```
proxy connected? → stats sweep (2s) → in the sector? no → HangarTick, return
  → know my ship? → know my position?
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
2. **Target** — and the rule depends on which scanner you have, because the two are not the same
   problem:
   - **Single-target** (one rock per cast): **nearest first**. At any moment the bot knows the
     contents of one or two rocks out of hundreds, so ranking that by richness is ranking a
     sample of two — and an unscanned rock has a count of zero and can never win. Mine the
     nearest wanted rock; failing that, go identify the nearest unknown one.
   - **Area** (a whole field per cast): **richest per unit of travel**,
     `ResourceCount / (1 + (distance/RockTravelPenalty)²)`. The penalty is **squared**: at the
     default 1000u a rock 2,000u out needs 5× the ore to win and one 5,000u out needs 26×, which
     nothing has. Worth a detour, not worth crossing the sector. Bounded to the scanner's own
     reach whenever anything qualifies inside it.
3. **Resource priority** — `WantedResources` is an ordered list, not a set. The highest-ranked
   resource with anything confirmed and reachable wins outright. It decides where to go **next**;
   a rock already being worked is finished rather than abandoned when something better respawns.
   Selecting all three is not a filter — it is the default written longhand, and is treated as
   such.
4. **Hold fire on unknown rocks** when a resource filter is set — breaking open a titanium rock in
   water mode is exactly what the filter is meant to prevent. Holding also lets power rebuild for
   the scan. Abandoned, loudly, if the scanner stops answering: an unenforceable filter is a
   reason to mine unfiltered, not a reason to park.
5. **Fire** — mining lasers if known, otherwise your cannons.
6. **Roam** when nothing qualifies. Almost everything `MiningCandidate` rejects is a belief that
   goes stale, so those are dropped and the ship flies to the nearest rock it knows nothing
   about, at any distance. Skips recorded by the *mining watchdog* are exempt — those are
   measured, not believed.
7. **Stall watchdog** — `MiningStallSeconds` (20) of being in position with lasers on a rock and
   neither its hull falling nor ore reaching the hold means the rock is gone. Progress is measured
   on results, never on casts sent: the server refuses a cast at a vanished object silently.

Scanning is skipped entirely when `MINE = Any` (`ScanOnlyWhenFiltering`) — 50 power for an answer
nothing reads. Scans also require `cost + ScanPowerReserve × maxPower` free.

Scan results expire after `ScanFreshnessSeconds` (180) — contents respawn and re-roll.

---

## Cost of a tick

Two 250ms timers run at once: the farm loop, and the UI refresh. Both walk the sector. That is
affordable only as long as walking the sector is O(n) — and it is very easy to make it O(n²)
without noticing, because nothing about the call site looks expensive.

**`WorldState.Snapshot()` is a deep copy of every object in the sector.** It is the right thing
to hand to a predicate, and the wrong thing to call *from inside* one.

That is exactly what `InStationDanger` did. It asks "is this one object inside an enemy station's
envelope", and to answer it called `HostileStations()`, which took a full `Snapshot()`. Its
callers — `MiningCandidate`, `CombatCandidate`, `Roam` — are predicates handed to
`_world.Nearest(...)` and `Snapshot().Count(...)`, i.e. run **once per object**. So one "which
rock should I mine" pass over 200 contacts made 200 complete copies of the world, each taking the
world lock.

Eight times a second, that is enough to:

- peg the UI thread so the window cannot be dragged or raised;
- starve the relay's decode thread of the world lock, so incoming frames back up, the ship stops
  responding to the player, and **the server eventually closes the connection** — which shows up
  in the log as `the server errored: An established connection was aborted by the software in
  your host machine`, and looks exactly like the bot's own traffic being rejected.

The fix is a 500ms memo on `HostileStations()`. Stations do not move; staleness costs nothing.

Two rules came out of it:

1. **Nothing called per-object may call `Snapshot()`.** Hoist it, or cache it with a short TTL.
2. **A panel that is not visible does no work.** `RefreshUi` already gated the map, contacts and
   loadout on `Visible`; `Diagnostics()` — the most expensive of the lot, several sector walks and
   fifty lines of formatting — was the one that was missed.

Lock discipline that falls out of the same problem: **never hold a lock across `yield return`.**
An iterator holds the monitor from the first `MoveNext` until the enumerator is disposed, so
`CombatLog.Describe()` held the combat log's lock across arbitrary caller code, and would have
held it forever if anyone abandoned the enumerator. It returns a finished list now.

---

## Docking

**Dock** stops the farm, picks the nearest friendly/neutral Outpost or Cruiser, flies there with
the same braking approach, **selects it**, and only then asks — rate-limited to one attempt per 4s.

The selection is the part that matters, and it cost three sessions to learn.

### The dock that hung up the server

Every dock request the bot had ever sent ended the session:

```
02:18:54.398  Retreat: dock requested at #4A000001 from 248u.
02:18:54.476  Session ended — the server closed the connection      (+78ms)
02:34:54.008  Retreat: dock requested at #4A000001 from 246u.
02:34:54.372  Session ended — the server closed the connection      (+364ms)
13:37:15.735  Retreat: dock requested at #4A000001 from 249u.
13:37:15.815  Session ended — the server closed the connection      (+80ms)
```

Three for three, no counter-example. Two theories died on the evidence before the right one:

- **Not the message.** A real dock captured off the wire (`DumpDockFrame`, below) is
  `022D000100004A00000000` — byte-for-byte what the bot sends, and what
  `GameProtocol.RequestDock` writes: `ushort 45`, `uint32 objectID`, `float delay`.
- **Not the range.** Every dockable object on this server publishes `OwnerCard.DockRange = 1000`
  (decoded straight out of the cached catalogue, `CardView.Owner` = 29), and a manual dock
  succeeded from **791u** while the bot was being refused from 248u.

What differed was the sequence. `SpaceLevel.Dock()` can only ever dock `GetPlayerTarget()`, so a
real dock is always a `LockTarget` followed by a `Dock` — and the captured trail shows exactly
that, a `LockTarget` for the outpost twenty seconds ahead of the request. `FleeTick`, meanwhile,
cleared `_target` and `_lockedTarget` as its **first act on every tick**, so the bot asked the
server to dock a station it had never told that server it had selected. No client can produce
that, and this server answers it by hanging up rather than refusing.

`LockBeforeDockAsync` now sends the lock, lets it settle 600ms, and docks on a later tick. The
retreat keeps a lock held on the refuge and only clears locks on other things.

> The lesson generalises past docking: **a message being byte-perfect says nothing about the state
> the server expects to be in when it arrives.**

### Reading a real one

Press dock in the client and the bot prints the request as it left, plus its context:

```
YOUR DOCK — raw frame, 11b: 022D000100004A00000000
YOUR DOCK — that frame holds 1 message(s): Dock(45) 8b
YOUR DOCK — what the client sent in the seconds before it: ... -20.0s LockTarget, -1.4s SetGear, -0.0s Dock
```

The trail is a 16-entry ring of the client's own Game requests. The frame list matters separately:
a client frame can hold several messages back to back, so "what was batched with the dock" and
"what preceded it" are different questions.

### The countdown

`Reply.DockingDelay` (95) carries a float — the delay the server is imposing. The client disables
its DOCK button for exactly that long and offers `CancelDocking` (102) instead. The bot knew the
opcode and did nothing with it; it now waits the countdown out, and **no dock request goes out
inside the window**, because a second request there is another thing the real client cannot send.
The dock-run timeout stands down while a countdown ticks, so a long post-combat delay cannot
abandon a dock that is about to land.

We do not model a server-side *cancel* of a countdown. If one is cancelled we wait out a dead
timer and retry after it expires — self-healing, at the cost of one countdown.

### Where to dock, and how far out

`CardView.Owner` (29) is now parsed — `bool IsDockable`, `float DockRange`, `byte Level`, six
bytes, straight out of the client's `OwnerCard.Read`. It replaces two guesses:

| Question | Was | Now |
|---|---|---|
| Can this be docked at? | the object's **type** — `Outpost or Cruiser` | the card's `IsDockable`, falling back to the type when no card has arrived |
| From how far? | `max(DockApproach 250, radius × 3 + 150)` | `DockRange × 0.9` (1000u on every dockable object here), then the range learned from your own docking, then the old guess |

> The bot's own comment claimed this "isn't on the wire". It was, and 292 of these cards were
> already sitting unparsed in the catalogue cache — the client fetches them itself. A claim about
> the wire is checkable; this one cost a retreat that flew to a body it could not enter.

### Never stationary under fire

Waiting at a refuge takes one of two forms:

| Situation | What the ship does |
|---|---|
| Nothing chasing us | Park. Costs no tylium, and the hull comes back just as fast |
| Something shooting at us | **Circle it** at `OrbitAsync`, at running speed, with the boost lit |

Circling keeps the station's guns between us and the threat, keeps the ship inside dock range so
every retry stays valid, and makes it a far worse target than a parked one. Docking while moving
is fine, and that is measured rather than assumed: a manual dock landed from 791u while the ship
was under way, and the client's `CanDock` tests only relation and range — no speed condition
appears anywhere in it.

The orbit is a tangent plus a radial correction:

```
outward = normalize(me - station)
tangent = normalize(outward × up)          // level circle; falls back to another axis at the poles
error   = (distance - radius) / radius
heading = tangent - outward × error        // the correction; a pure tangent is a chord, and spirals out
```

The radius sits between the station's clearance sphere (below it the collision avoidance fights
the orbit) and `DockRange × 0.9` (above it the dock requests stop being valid) — typically
350–800u. It goes through the same obstacle deflection as any other heading.

**When to stop waiting is a measurement, not a clock.** The refuge is abandoned only if the hull
has fallen `RefugeBleedFraction` (0.10) below its best reading since arriving, something is still
shooting, no server countdown is running, and at least `DockGiveUpSeconds` (10) has passed as
hysteresis. It is then added to a per-sector refused set so it is not chosen again, and the
retreat falls through to running away.

> The first cut of this was a flat 10-second timeout, which is wrong in exactly the case it was
> written for: a dock cooldown after combat can run to tens of seconds, and a short timer abandons
> a good outpost while its countdown is still ticking. Circling removes the urgency that made a
> timer seem necessary at all — if the hull is holding, there is no reason to be anywhere else,
> however long the door takes.

> Without any of this, "arrived" was terminal. A friendly **Cruiser** satisfied the old type-based
> dockability test, so in a sector with no outpost the bot flew to one, cut the throttle, asked to
> dock something that would not take it, and was killed at zero speed by the droid it was running
> from. `FleeTick` exists because holding station under fire killed a Raptor; holding station at a
> door that will not open is the same mistake wearing a hat.

### The rest

`AllowDocking` (default on) is the switch. Off, the retreat still runs to the outpost and shelters
under its guns — the part that actually saves the ship; docking was only ever the last step.

It also **learns**: when you dock manually the bot records the distance and uses 90% of it if no
card is available.

**Undock** sends `Room.Quit`; the client sends `JumpIn` itself once its space level has loaded.

---

## Dying, repairing, launching again

A death used to end the run in every practical sense: the ship left the sector, the farm loop kept
ticking against a ship that wasn't there, and nothing moved until someone pressed Undock. There is
now a state machine for everything that happens outside the sector, and it runs **above** every
flying decision — none of them apply to a ship in a hangar.

```
not in the sector?
  → death screen pending?   → SelectRespawnLocation(first), wait 2s
  → condition short / died? → RepairAll(shipId, useCubits: false)
  → waited UndockDelay?     → JumpIn, and again every RelaunchInterval until we're flying
```

Three messages, all transcribed from the client:

| Message | Wire | Why it is needed |
|---|---|---|
| `Game` reply `RespawnOptions` (99) | two equal-length `uint32` lists — sectors, and the carrier player each belongs to (0 = a station) | **The server will not launch a dead ship.** Until something answers, the player stays dead, so waiting for a hangar to appear waits forever. |
| `Game` request `SelectRespawnLocation` (70) | `sectorId, carrierPlayerId` | Answers it. The bot takes the first option. |
| `Player` request `RepairAll` (26) | `shipId, useCubits` | The damage window's "repair all" — hull condition *and* every fitted system in one message. Repairing the hull alone launches a ship with dead slots. |

**Condition is not hull.** The hull bar refills by itself; condition (`Player` reply `ShipInfo`
(11): `shipId, float`) does not, and dying is what empties it. Full condition is stated in the
ship's own catalogue card, so `Condition` reads `now / max` only once both have arrived — and the
repair is asked for anyway after a death, because dying always costs condition and a server that
sends no `ShipInfo` would otherwise never trigger one.

**Titanium, never cubits.** Cubits are bought with money; nothing the bot decides on its own can
spend them. If the condition hasn't moved 8s after asking, the log says so once — the causes are
no titanium, a cubits-only hull, or a server that ignores `RepairAll`.

### Anchored is a third state

Not the hangar, and not flying: riding another player's carrier, which is where the death screen
puts you if you take its first option. The client's view of it is total — `Reply.Anchor` (Player
52, `uint32 carrier`) does `SetPlayerShip(carrier)`, i.e. the carrier *becomes* your ship, and the
whole ability bar and every flight control switch off.

The bot parsed neither `Anchor` (52) nor `Unanchor` (53, `uint32 ship, byte reason`), so it saw a
ship id and a position, called that flying, and ran the full farm loop from inside somebody's
Brimir: throttle, heading, a repair cast, and finally a `Dock` request. The server closed the
connection on the frame after it.

| State | Undock is |
|---|---|
| anchored to a carrier | `Game/77 RequestUnanchor` |
| in a station hangar | `Room/5 Quit` |
| flying a carrier yourself | `Game/79 RequestLaunchStrikes` (not implemented — the bot doesn't fly one) |

`UndockButton.Undock` tests them in exactly that order, and anchoring is the **first** branch —
so the hangar message is the wrong one for a state the bot did not know it could be in.

The death screen now prefers an option whose `CarrierPlayerId` is 0, i.e. a place of our own,
falling back to the first offer only when no station is on the list.

**Docking by hand stops the farm.** A `RemoveMe(Dock)` within 60s of *your own* `Dock` request is
you parking the ship, and relaunching it would be the bot undoing an instruction. Only the real
client's traffic reaches that check — injected frames go straight to the server — so the bot's own
dock runs can't be mistaken for yours.

---

## Settings

Toolbar: mode (Combat/Mining) · Fly to target · Boost · Auto loot · Attack players · Auto undock ·
Repair in hangar · Fallback reach · Retreat at hull · Hold off rock · Mine (resource)

`bot.json` holds the rest. Notable:

| Setting | Default | Meaning |
|---|---|---|
| `AsteroidStandoff` | 120 | gap to a rock's **surface** (its radius is added on top) |
| `CollisionMargin` | 70 | clearance on top of a **ship's or station's** radius, floored by 2× our own hull |
| `AsteroidCollisionMargin` | 40 | clearance on top of `radius × 0.9` for a rock, floored by our own hull |
| `PlanetoidCollisionMargin` | 120 | clearance on top of the fixed 900u planetoid collider, floored by 2× our own hull |
| `StandoffMargin` | 1.15 | park this far outside the clearance sphere, never on it |
| `PlanetoidStandoff` | 1200 | hold distance from a planetoid |
| `SelfPositionTrustSeconds` | 4 | how old a position fix may be before arrival is re-confirmed |
| `SelfPositionWaitSeconds` | 6 | how long to sit still waiting for that fix before flying on the estimate |
| `AllowDocking` | true | let the bot send dock requests at all |
| `DockGiveUpSeconds` | 10 | shortest stay at a refuge before its hull trend may send us away |
| `RefugeBleedFraction` | 0.10 | hull lost at a refuge before deciding it is not sheltering us |
| `FallbackSpeed` | 100 | throttle when Speed stat unknown |
| `BoostShedSeconds` | 1.5 | seconds of boost travel left for shedding boost, on top of the braking zone; the two together set the distance past which boosting is worth it |
| `RetreatHull` | 0.25 | disengage below this **fraction** |
| `ScanOnlyWhenFiltering` | true | don't scan when mining anything |
| `ScanPowerReserve` | 0.25 | keep this fraction of max power back |
| `ScanFreshnessSeconds` | 900 | how long a scan is trusted before the rock counts as unknown again |
| `MaxAreaScanTargets` | 32 | cap on ids in one area cast |
| `AutoUndock` | true | answer the death screen and launch again by itself |
| `AutoRepairShip` | true | buy condition back with titanium before launching |
| `UndockDelaySeconds` | 6 | hangar dwell — client death sequence, respawn, repair |
| `RelaunchIntervalSeconds` | 15 | gap before asking to launch again |

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
| **Server dropped us six seconds after a respawn** | the death screen's first option was another player's carrier; anchoring was never parsed, so the bot flew, cast and finally sent `Dock` from inside it | a state you do not model is a state you will act wrongly in |
| Undock did nothing, forever | injected `Game.JumpIn`; the UNDOCK button actually sends `Room.Quit`, and the client sends `JumpIn` itself once the space level has loaded | find the message the *button* sends, not the one whose name matches |
| **Held fire at full power "waiting for the scan"** | the wait was gated on a scanner *existing*; the scan was refused because its reach is unknown, so the give-up counter (unanswered casts) never moved | whatever stops the request must also stop the wait for its reply |
| Wedged against big asteroids | inside a rock's clearance sphere the gap is 0, so the brake taper pinned the throttle to `MinApproachSpeed` — a crawl, in the one state where the ship is definitely in the wrong place | getting out outranks arriving |
| **Mined a rock, then fled from it** | the asteroid standoff was `Max(setting, ClearanceOf(rock))`, so the ship parked *exactly* on the clearance sphere — the moment that rock stopped being the target it counted as an obstacle we were inside | never park on a boundary you also test against |
| "Hold off rock" did nothing | measured from the rock's **centre** and floored by radius + margin, so on any rock bigger than the number you typed the floor won | a setting you cannot feel is a setting that is wrong |
| Contact buttons silently did nothing | `_ = what(id)` started the task without awaiting it, so "no session" was thrown *inside* the task and the surrounding try/catch never saw it | fire-and-forget discards failures as well as results |
| Nine casts in three milliseconds | `FireAll` fired every gun in one pass | no human client produces that pattern |
| **UI unmovable, ship uncontrollable, server hung up** | `InStationDanger` took a full world `Snapshot()` per call, and its callers run it once per object — O(n²) deep copies, 8×/second, holding the world lock the decode thread needs | a predicate must never do work proportional to the whole set |
| Diagnostics rebuilt 4×/s while off screen | the one panel in `RefreshUi` not gated on `Visible` | if three of four calls have a guard, the fourth is a bug, not a style |
| Ghost rock re-targeted forever | the mining watchdog skipped it, then `Roam` cleared *all* skips and re-picked it, because roaming deliberately ignores skips | a measurement and a stale belief cannot share a list |
| Roamed to a rock the bot did not consider its target | `Roam` bypassed `ResolveTarget`, leaving `_target` at 0, so the scan gate could not see it and `DropTarget` had nothing to clear | one code path per "what am I working on" |
| Watchdog condemned perfectly good rocks | with `RequireKnownReach` on and no published ranges, `FireAll` held fire on every laser, but the stall watchdog was told "in position and working it" | measure the guns' ability to fire, not the intent to |
| Scanner aimed at a guess | `RequireKnownReach` governed the guns but not the scanner, which still fell back to 3,000u — manufacturing the silent refusals that read as a flat battery | a rule with an exception nobody wrote down is a rule that is not applied |
| Hit-rate table described the mining lasers | every cast was booked as a pending shot, including lasers at rocks, which never resolve and vastly outnumber gun shots | a sample has to be drawn from the population you are measuring |
| Toggle laser cast at instead of switched on | the catalogue states range, reload, power and role, but nothing we have transcribed says cast-vs-toggle; `RefreshFromCatalogue` assumed `Cast` silently | flag a guess as a guess (`KindAssumed`), and let observation settle it |
| Em dashes became `â€"` | `Widgets.cs` was re-saved as UTF-8-read-as-CP1252, with a BOM added | check the bytes, not the rendering |
| **Every dock request the bot ever sent hung up the server** | it asked to dock a station it had never sent a `LockTarget` for; the client can only dock its *selected* target | a byte-perfect message says nothing about the state the server expects to receive it in |
| Docked "too far out" was blamed for it | the comment said `OwnerCard.DockRange` "isn't on the wire" — it is, in `CardView.Owner` (29), and reads 1000 on every dockable object here | a claim about the wire is checkable; check it before building a workaround on it |
| **Parked motionless "mining" a rock that was behind it** | `DistanceToMe` dead-reckoned the *target* but used a raw `MyPosition` that only advances when a maneuver arrives, so the modelled arrival preceded the real one | if you extrapolate one side of a comparison, extrapolate both |
| Could not cross its own asteroid belt | one flat `CollisionMargin` for bodies spanning two orders of magnitude — five times a pebble's size, 4% of a planetoid's | a constant that is right in the middle of a range is wrong at both ends |
| **Died at zero speed to the droid it was fleeing** | in a sector with no outpost a friendly Cruiser passed the type-based dockability test; the ship parked at its door and asked forever, because "arrived" had no failure case | any state you can enter under fire needs a way out that isn't success |
| Dockability and dock range were both guessed | `CardView.Owner` was never parsed, though 292 of them sat in the cache and the code's own comment said the data "isn't on the wire" | check the claim before building the workaround |

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

---

## Two servers at once

There are two live servers, both on port 27050, both running the same client build:
**PlayBSGO** (152.53.148.101, client under `C:\Program Files\BSGO Launcher\client\live`) and
**BSGO.fun** (94.16.108.128, client under `C:\Program Files (x86)\BSGOFUN\client\live`).

The client hardcodes the port but takes any IP in `+gameServer`, so each bot instance owns its
own loopback address. Running one server is unchanged — start the exe, pick the server. For
both at once, the **Second bot** button opens another window on the other `bot*.json` next to
the exe (several match → a menu; none → one is cloned from the current config on the next free
loopback address). Under the hood it spawns the exe with `--config <file>`, which resolves
relative to the exe folder:

- `bot.json` → proxy on `127.0.0.1:27050` → PlayBSGO
- `bot.bsgofun.json` → proxy on `127.0.0.2:27050` → BSGO.fun

Captured sessions are filed **by host** into whichever profile matches, creating one when none
does — a login on one server can no longer overwrite the other's profile (which is how BSGO.fun's
entry was once lost). While an instance is parked on a live server its session catcher ignores
logins for any other live server, so two watching instances never steal or kill each other's
launcher client. A capture updates the client version only for installs on the path the launcher
actually ran.
