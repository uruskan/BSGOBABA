# BSGO — game mechanics that matter

Everything here is verified against **this repo's server source** (`bsgocore/`, `server/`) or the
**decompiled client** (`client-src/`), with wiki figures marked as such. Server code beats the
wiki: it's what actually runs. Wiki beats memory.

File references are `path:line` and clickable.

---

## 1. Power is the whole game for a mining build

Hull and power arrive as **absolute points**, not fractions — clamped server-side against
`MaxHullPoints` / `MaxPowerPoints`. A strike ship reading `495/495` hull and `150` power is
normal. (`bsgocore/.../ShipSubscribeInfo.java`, `SpaceSubscribeInfo` lines 188–202.)

**Every ability silently fails when power is short.** `AbilityAction.preFun`:

```java
if (this.casterStats.getPp() < this.ppCost) return false;   // no reply, no error
```

No message goes back to the client. A browned-out ship is indistinguishable from broken gear.
Same for a missing consumable (`checkConsumablesSatisfied`) — silent refusal.

### Drain figures

| Thing | Power | Rate | Sustained |
|---|---|---|---|
| 'Gopher' Light Mining Cannon | 2 | 0.5s reload | **4/sec** |
| MEC-A6 'Fang' Light Autocannon | 1 | 0.5s reload | **2/sec** |
| Mineral Analysis Module (L1) | 50 | 1s reload | one-shot |
| Auxiliary Power Module | 5 → restores 25 | 60s reload | **+0.33/sec net** |

Ship recharge is ~5–6/sec. **Three mining cannons = 12/sec against ~5.5/sec regen.** You cannot
run three continuously on a strike hull. This is not a bug or a bad build; it's the design.

> Wiki, independently: *"The power costs of the asteroid scanner and mining guns make it
> relatively ineffective to use more than two mining guns and the scanner regardless of which
> strike craft you use."*

**Scanner upgrade is the highest-value purchase in a mining build**: 50 → 5 power across its 15
levels. That single change removes the power problem.

---

## 2. Mining damage — why mining cannons work

`DamageCalculator.calculateDamageMining`:

```java
final float damageMining = enemyShip.getSpaceEntityType().isOfType(SpaceEntityType.Asteroid, SpaceEntityType.Comet)
        ? itemBuffAddStats.getStatOrDefault(ObjectStat.DamageMining) : 1f;
final float dmgLow  = DamageLow  * damageMining;
final float dmgHigh = DamageHigh * damageMining;
```

- The multiplier applies **only to Asteroid and Comet**. Not Planetoid, not ships.
- Two separate damage paths: `FireMiningAction` → `dealDamageFromMining` (multiplier applies);
  `FireCannonAction` → `dealDamageFromAbility` (no multiplier).
- `getStatOrDefault` defaults to **0f**, so a weapon with no `DamageMining` stat would do zero
  damage *through the mining path*. Regular cannons don't use that path, so they still hurt rocks.

**Effective damage on a rock:**

| Weapon | Raw | × Mining | vs asteroid | per power |
|---|---|---|---|---|
| Gopher | 1–4 (avg 2.5) | ×5 | **12.5** | 6.25 |
| Fang autocannon | 1–10 (avg 5.5) | ×1 | 5.5 | 5.5 |

Mining cannon is ~2.3× better per shot and slightly better per power. Regular cannons **do**
break rocks — just slower. Both are viable; the bot falls back to cannons if no laser is fitted.

### Mining cannons are flat across ship sizes (wiki)

| Colonial | Class | Reload | Damage | DPS | Mining | Range | Cost |
|---|---|---|---|---|---|---|---|
| 'Gopher' Light | Strike | 0.5s | 1–4 | +5.0 | ×5.0 | 0–600 | 5,000 Tyl |
| 'Mole' Medium | Escort | 0.5s | 4–10 | +5.0 | ×4.0 | 0–900 | 10,000 Tyl |
| 'Badger' Heavy | Line | 4.0s | 14–28 | +5.3 | ×5.0 | 0–1350 | 10,000 Tyl |

Cylon equivalents: 'Gouger' / 'Dredger' / 'Excavator', identical stats.

**DPS is 5.0 / 5.0 / 5.3.** A heavy battery does not break rocks faster than a light one. Bigger
ships buy you *range*, not speed of mining.

---

## 3. Scanning

The **only** way to learn a rock's contents is `Reply.Scan`, sent by `ResourceScanAction`. Nothing
else broadcasts it:

- Asteroid `WhoIs` carries only `creatingCause`, owner card guid, world card guid, position,
  radius, rotationSpeed. The world card is picked at random (`AsteroidRing.java:87`) and is
  **unrelated to contents** — the model tells you nothing.
- `LootAssociations` is read only by the scan action, the on-destroy loot handler, planetoid
  mining, and debug. Never pushed to clients.

Scan reply carries `(asteroidId, resource guid, count, isMinable, price, cooldown)`.

**Contents are not fixed.** `AsteroidResourceSpawn.spawn()` assigns them on a respawn timer
(`asteroidDesc.respawnResourceTime()`) and re-rolls via `itemPicker.getRandomItem()`. A rock can
be empty → water → mined out → titanium. **Scans expire**; caching them forever is wrong.

`isMinable` in the scan reply means *"can a mining ship be ordered here"* and is set **only for
planetoids** (`ResourceScanAction.java:102-107`). Ordinary asteroids always report `false`. Do not
read it as "worth shooting".

Colours: red = empty, yellow = tylium, purple = titanium, blue = water.

### The two analysis modules (Strike class, verified from in-game tooltips)

| | Mineral Analysis Module | Experimental Mineral Analysis Module |
|---|---|---|
| Range | **0–2000** | 0–300 |
| Targets | one asteroid | everything in the radius |
| Reload | 1 sec | 1 sec |
| Power | 50 | 50 |
| Consumable | none | **1 power cell per scan** |
| Cost | 2,000 Tyl | 5,000 Tyl + 4,000 cubits |

Medium/large computer slots get the **Array** and **Grid** with longer range.

**Which to use:** cost is *per cast*, so the area module wins only where several rocks sit inside
300u — realistic for a big hull parked in a belt, rare for a strike ship. The 2000u single-target
module lets you identify rocks *without flying to them*, which is what a resource filter wants.

### Area casts are normal traffic

`ShipAbility.Cast` (client, `client-src/ShipAbility.cs:237`):

```csharp
case ShipAbilityAffect.Area:
    DoCast(GetObjectsWithinAOE().ToArray(), card.Launch);
```

`GetObjectsWithinAOE` walks every loaded object, keeps those passing `CheckTarget` and inside
range, sends them all. So a multi-id cast is exactly what the real client does.

**But only for Area abilities.** `AbilityAction.preFun`:

```java
if (affect == ShipAbilityAffect.Selected && targetSpaceObjects.size() > 1) {
    log.warn("Cheat, selected target size is higher than 1! cheaterID: {} size: {}", ...);
    return false;
}
```

Sending multiple ids to a **Selected** ability is rejected *and logged as cheating with your
player id*. Never batch unless the ability is proven Area.

⚠️ `bsgocore` fills `toCallOn` **only** from client-supplied ids (`AbilityCastRequestQueue.java:97`)
— there is no server-side area fill. On that server an Area ability cast with zero targets scans
nothing and still charges power.

---

## 4. Consumables (ammo)

`ShipSubscribeInfo.applyAbilitySlotStats`:

```java
tmpBonus = applyStatsMultToIfBonusExistsInApplyOn(consumableCard.getItemBuffAdd(), baseStats);
...
applyStatsAddTo(consumableBonusStats, ability.getItemBuffAdd());
```

and:

```java
if (toApplyOnStats.containsKey(bonusStat.getKey()))
    rv.setStat(key, abilityStat * bonusStat.getValue());   // % of the ability's OWN stat
```

**A cell's bonus only applies to a stat the ability already has.** Consequences:

| Cell | Bonus | On Aux Power (base +25) | On Analysis Module | Price |
|---|---|---|---|---|
| Light Standard | — | 25 | *(no effect)* | 50 Tyl |
| Light Improved | +15% | 28.75 | *(no effect)* | 125 Tyl |
| Light Advanced | +30% | 32.5 | *(no effect)* | 200 Tyl |

The Analysis Module has only Range / Reload / PowerPointCost — no power-output stat for the bonus
to multiply. **Cells still gate the scan** (you need ≥1 in the hold), but premium cells are wasted
there. Feed the scanner Standard; save Advanced for the Aux Power Module.

Value: Standard is **2.5 Tyl per power restored** vs Advanced's 7.3. Standard wins ~3×.

---

## 5. Ships

### Advanced strike hulls (wiki)

Base-hull comparison (for the **Advanced** figures, and every other strike, see `WIKI.md`):

| | Adv Mk II | Adv Mk III | Adv Mk VII | Raptor (base) |
|---|---|---|---|---|
| Weapon | 4 | 4 | **5** | 3 |
| Hull | 2 | 3 | 3 | 2 |
| Engine | **5** | 4 | 3 | 2 |
| Computer | 2 | 2 | 3 | **4** |
| Hull pts | 585 | 680 | **720** | 500 |
| Power | 150 | 150 | 150 | 150 |
| Recharge | 5/s | 5.5/s | **6/s** | 5.5/s |
| Speed | **60** | 57.5 | 55 | 52.5 |
| Boost | **110** | 100 | 85 | 77.5 |
| Dradis | 2,000 | 2,000 | 2,000 | **3,000** |
| Visual | 200 | 200 | 200 | **500** |
| Cost | 30k cubits | 30k cubits | 30k **merits** | 75k Tyl |

### Is bigger better for mining? No.

The wiki's mining page argues this directly, and the numbers back it:

- **Mining DPS is flat** across Strike/Escort/Line (5.0 / 5.0 / 5.3).
- **Power scales, but so does equipment cost** — *"larger size ships have more power and power
  regeneration but the mining guns and sensors cost more power to use in an almost perfect
  counter."* You still can't run more than ~2 mining guns.
- **Range advantage is cancelled by speed** — a fast strike closes the gap sooner than a slow
  hull shoots further.
- **Escorts and Lines are trivially spotted** across a system and get ambushed.
- **Strike equipment is cheaper to upgrade**, so you reach high levels far sooner — and upgrade
  level is what actually fixes power.

> Wiki verdict: *"Two upgraded mining guns and an upgraded asteroid scanner is generally the most
> efficient setup"*, and the **Advanced Viper Mk II / Advanced Raider** is arguably the best
> mining ship — for stealth, speed between clusters, and cheap upgrades.

The **Raptor** is the other strong pick — but not for the reasons above. Corrected against the
wiki's own ship pages (see `WIKI.md`):

- The recharge table below is the **base** Raptor. The **Advanced Raptor is 175 power / 6 per
  second**, with **4 weapon and 5 computer slots** — tied with the Adv Viper Mk VII on regen, and
  ahead of it on pool and computer slots.
- Since a power-limited miner's ore rate is proportional to regen, the Advanced Raptor mines
  **20% faster than the Adv Viper Mk II** the wiki recommends. Its regen, not its slots, is why.
- **Its 3,000 Dradis and 500 visual are worth nothing to a bot.** Dradis is a client-side display
  filter (`DradisHelper`); the bot never applies it and already sees the whole sector. Do not
  count those stats when picking a hull for this bot.

### Planetoid mining

Planetoids are ≥15× bigger than the largest asteroid and **cannot be destroyed by cannons**. You
scan one, then pay **100 cubits** to call a Mining Ship that extracts automatically — even while
you're offline — until it's destroyed or the rock is exhausted. Yields 50,000+ in threat-20
systems.

Cost: a mining ship **appears on the sector map for both factions**, and destroying it is a daily
assignment. It is a PvP magnet by design. Asteroid mining is invisible and safer.

### Sector threat level

Higher threat = bigger rocks, more resources, nastier NPCs. Threat 20 is the richest; 1–10 is
safe and poor.

---

## 6. Protocol facts worth remembering

- **`SetSpeed` carries a number, not an intent.** The mode byte is written but no server reads it
  — the client resolves `SpeedMode.Full` into `Game.Me.Stats.MaxSpeed` itself
  (`ShipControlsBase.cs:119`) and sends that float. Sending `Full, 1f` means *one unit per second*.
- **`SetGear(Boost)`** sets speed from the `BoostSpeed` stat and multiplies acceleration by
  `AccelerationMultiplierOnBoost`. Boost burns tylium via `BoostCostTimer`; the server drops you to
  Regular when the hold runs dry.
- **`SetGear(Regular)`** re-applies the last stored throttle — so set speed *before* leaving boost.
- **Undock is `JumpIn`** (opcode 61). There is no separate undock message.
- **Docking is validated twice**, and both failures are logged as cheating with your player id:
  `ownerCard.isDockable()` and `distance > ownerCard.getDockRange()`.
- **`LockTarget` does nothing but record the target id** (`GameProtocol.java:1038`). It does not
  trigger a scan.
- **Client frames batch multiple messages** behind one protocol id; server→client frames carry
  exactly one.
- **Object type lives in the id**: `type = objectId & 0x1F000000`. Faction `0xC0000000`, group bit
  `0x20000000`.
- **Accuracy is flat at or below optimal range**, then falls linearly to `minHitChanceOutsideOpt`
  at max range (`HitchanceBasedOnThrottle.getChanceToHit`). Closing inside optimal gains nothing;
  sitting beyond it loses a lot.
