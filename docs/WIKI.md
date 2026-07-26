# BSGO Wiki Reference

Transcribed from the [Battlestar Galactica Online Wiki](https://bsgo.fandom.com) on 26 July 2026.
This is the *game design* reference — ship and item stats as the original game shipped them.
For protocol and server-behaviour facts recovered from the decompiled client, see `GAME.md`.

---

## ⚠️ Read this before trusting a number

**The wiki describes the original game. You play on BSGOFUN, a private server with its own
tuning.** They do not agree, and we have proof:

| 'Gopher' Light Mining Cannon, level 4 | Wiki | Live (in-game card) |
|---|---|---|
| Min damage | 1.33 | 1.33 ✓ |
| **Max damage** | **7.33** | **5.33 ✗** |
| **DPS** | **8.66** | **6.70 ✗** |
| **Optimal range** | **300.01** | **283.33 ✗** |

Min damage matches; max damage and DPS scale more slowly on the live server, and optimal range
is one step behind. So:

- **Level-1 base stats** are broadly reliable — they match what we see.
- **Upgrade curves are not.** Read levels off the in-game card, never off this file.
- **Slot layouts and ship stats have matched exactly** everywhere we've checked (the Advanced
  Raptor's 175 power / 6 regen / 4 weapon / 5 computer is confirmed in-game).

Also note the wiki is patchy: several weapon sections are literally marked `(tbc)`, and the
entire **Equipment** category is **three pages**. Module stats are *not on this wiki* — see
[Modules](#modules-the-big-gap).

---

## Contents

1. [Strike ships](#strike-ships)
2. [Weapons](#weapons)
3. [Mining](#mining)
4. [Skills](#skills)
5. [Modules — the big gap](#modules-the-big-gap)
6. [Boosters and consumables](#boosters-and-consumables)
7. [Escort and line ships](#escort-and-line-ships)
8. [What this wiki does not have](#what-this-wiki-does-not-have)

---

## Strike ships

Colonial and Cylon ships are **statistically identical** in matched pairs — the wiki says so
outright on the Weapons page, and every pair we checked confirms it. Pick by faction, not stats.

### Colonial strike — advanced

| | Adv Viper Mk II | Adv Viper Mk III | Adv Viper Mk VII | **Adv Raptor** | Adv Rhino |
|---|---|---|---|---|---|
| Role | Interceptor | Multi-Role | Multi-Role | **Command** | Assault |
| Cylon counterpart | Adv Raider | Adv War Raider Mk II | Adv Cylon War Raider | Adv Heavy Raider | Adv Marauder |
| Cost | 30k cubits | 30k cubits | 30k **merits** | 30k cubits | 40k cubits |
| **Weapon slots** | 4 | 4 | **5** | 4 | **5** |
| Hull slots | 2 | 3 | 3 | 2 | **5** |
| Engine slots | **5** | 4 | 3 | 2 | 2 |
| **Computer slots** | 2 | 2 | 3 | **5** | 2 |
| Hull points | 585 | 680 | 720 | 650 | **750** |
| Hull recovery | 4.5/s | 5/s | 5.25/s | 5/s | 5.5/s |
| Durability | 9,000 | 9,000 | 9,000 | 10,000 | **11,000** |
| Armor | 5 | 5 | 5 | 5 | **10** |
| Critical defense | 80 | 80 | 80 | **100** | **120** |
| **Power** | 150 | 150 | 150 | **175** | 150 |
| **Power recharge** | 5/s | 5.5/s | **6/s** | **6/s** | 5/s |
| Speed | **60** | 57.5 | 55 | 52.5 | 50 |
| Boost speed | **110** | 100 | 85 | 77.5 | 75 |
| Boost cost | 0.5 Tyl/s | 0.5 Tyl/s | 0.75 Tyl/s | 0.6 Tyl/s | 0.7 Tyl/s |
| Turning | **55°/s** | 52.5°/s | 50°/s | 47.5°/s | 45°/s |
| Avoidance | 510 | 510 | 510 | 500 | 490 |
| Firewall / Emitter | 100 | 100 | 100 | **200** | 150 |
| Dradis range | 2,000 | 2,000 | 2,000 | **3,000** | 2,500 |
| Visual range | 200 | 200 | 200 | **500** | 250 |
| FTL range | 4.5 LY | 4.5 LY | 4.5 LY | **5.5 LY** | 5 LY |

**Role bonus — Command (Raptor / Heavy Raider):** jump transponder power costs reduced by 50
(25 on the base Raptor).

### Colonial strike — base, and what advancing buys

| | Viper Mk II | Viper Mk VII | **Raptor** |
|---|---|---|---|
| Cost | Starter | 45k cubits | 75,000 Tylium |
| Weapon / Hull / Engine / Computer | 3 / 2 / 4 / 2 | 4 / 3 / 3 / 3 | 3 / 2 / 2 / **4** |
| Hull points | 450 | 585 | 500 |
| Hull recovery | 2.5/s | 4.5/s | 3/s |
| Durability | 4,500 | 9,000 | 5,000 |
| **Power** | **100** | 150 | 150 |
| **Power recharge** | 5/s | 5/s | 5.5/s |
| Speed / Boost | 55 / 90 | 55 / 85 | 52.5 / 77.5 |
| Dradis / Visual | 2,000 / 200 | 2,000 / 200 | **3,000 / 500** |

**Advancing the Raptor** (30,000 cubits) gains: +1 weapon slot, +1 computer slot, +150 hull
points, +2/s hull recovery, +5,000 durability, **+25 power, +0.5/s power recharge**.

**Advancing the Viper Mk VII** (30,000 merits) gains: +1 weapon slot, +135 hull points,
+0.75/s hull recovery, **+1/s power recharge**.

Pre-installed kit: Viper Mk II ships with an Autopilot Module, a **Mineral Analysis Module**
and 2× Fang. The Raptor ships with 1× Fang and 1× "Lightning" launcher — **no scanner**.

### Cylon strike — advanced

Mirror images of the Colonial table above:

| | Adv Cylon Raider | Adv Cylon War Raider | **Adv Heavy Raider** |
|---|---|---|---|
| Colonial counterpart | Adv Viper Mk II | Adv Viper Mk VII | **Adv Raptor** |
| Weapon / Hull / Engine / Computer | 4 / 2 / 5 / 2 | 5 / 3 / 3 / 3 | 4 / 2 / 2 / **5** |
| Hull points | 585 | 720 | 650 |
| **Power / recharge** | 150 / 5￼/s | 150 / **6/s** | **175 / 6/s** |
| Speed / Boost | 60 / 110 | 55 / 85 | 52.5 / 77.5 |

### Stealth ships (Force Recon)

Cannot equip normal strike equipment — they have an exclusive item set. No transponder arrays,
no nuclear weapons.

| | Raven Mk VI-R | **Raven Mk VI-R/A** | Malefactor Type-1 | Malefactor Type-2 |
|---|---|---|---|---|
| Gun / Launcher | 2 / 0 | 2 / **1** | 2 / 0 | 2 / 1 |
| Hull / Engine / Computer | 2 / 2 / 1 | 2 / 3 / 2 | 2 / 2 / 1 | 2 / 3 / 2 |
| Hull points | 350 | 420 | 350 | 420 |
| **Power / recharge** | 120 / **3/s** | 150 / **3.6/s** | 120 / 3/s | 150 / 3.6/s |
| Speed / Boost | 70 / 90 | **85 / 110** | 70 / 90 | 85 / 110 |
| Armor | **0** | **0** | 0 | 0 |
| Cost | — | 45,000 merits | — | — |

Top speed reaches ~200 m/s with the right engine fit. **Power recharge is the worst of any
strike (3.6/s)** — a terrible miner. Update 53 removed one launcher slot.

**Stealth mode** breaks brackets, drops enemy targets, hides you from DRADIS and breaks missile
lock. It is *not* invisibility — you can still be found and selected by eye. It is cancelled by:
taking damage, firing or toggling weapons, activating boost, or entering threat. Every ship has a
**Visual Range** stat that reveals stealth ships inside it — which is what the Raptor's 500 m is
for.

---

## Weapons

### Light autocannons (strike)

All cost 10,000 Tylium, all take light rounds as ammunition, all have 5 armor piercing, 400
accuracy, 1.00 power cost, 75° firing arc, 0 min range, and 2,500 → 5,000 durability over 10
levels. `-P` variants are identical but trade the upgrade curve for **critical offense
(100 → 150)** instead of optimal range.

| | Fang / Aggressor | **Tornado / Lasher** | Hawk / Disabler |
|---|---|---|---|
| Damage (L1) | 1–10 | 1–10 | 1–10 |
| Damage (L10) | 2–20 | 2–20 | 2–20 |
| **DPS (L1 → L10)** | 11 → 22 | **13.75 → 27.5** | 9.16 → 18.33 |
| **Reload** | 0.50 s | **0.40 s** | 0.60 s |
| **Max range** | 750 | **600** | **900** |
| Optimal (L1 → L10) | 300 → 350 | 250 → 350 | 350 → 550 |
| Power cost | 1.00 | 1.00 | 1.00 |

Fang is the balanced default, Tornado the close-range brawler, Hawk the stand-off gun.

### Mining cannons

**Flat DPS across all three hull classes** — a heavy battery does *not* break rocks faster.
Bigger ships buy **range**, not mining speed.

**All figures below are from the live in-game store cards at level 1** — the wiki's mining
table is wrong in three places and is not used here.

| | **'Gopher' / 'Gouger'** | 'Mole' / 'Dredger' | 'Badger' / 'Excavator' |
|---|---|---|---|
| Class | **Strike** | Escort | Line |
| Reload | 0.5 s | **1.5 s** † | 4.0 s |
| Damage (L1) | 1–4 | 4–10 | 14–28 |
| DPS | 5.0 | **4.7** † | 5.3 |
| **Mining multiplier** | ×5 | **×5** † | ×5 |
| Armor piercing | 5 | 25 | 50 |
| Range | 0–600 | 0–900 | 0–1350 |
| Optimal (L1) | 250 | 350 | 550 |
| Accuracy | 400 | 350 | 125 |
| **Power cost** | **2** † | **12** † | **50** † |
| Firing arc | 75° | 180° | 180° |
| **Multi-targeting** | no | no | **yes** |
| Durability | 2,500 | 7,500 | 17,500 |
| Cost | 5,000 Tyl | **7,500 Tyl** † | 10,000 Tyl |

† **Wiki errors, corrected from the store cards.** The wiki gives the Mole ×4 mining (**really
×5**), 0.5 s reload (**really 1.5 s**), 5.0 DPS (**really 4.7**) and 10,000 Tyl (**really
7,500**); it leaves *every* power cost blank. The ×4 is the damaging one — it makes escorts look
20% worse than they are and inverts the conclusion below.

### Which mining cannon actually mines fastest

Every mining fit is power-limited, so sustained rate is `regen × damage-per-energy`.

| | Gopher | Mole | Badger |
|---|---|---|---|
| Avg damage × ×5 | 2.5 × 5 = **12.5** | 7 × 5 = **35** | 21 × 5 = **105** |
| Power per shot | 2 | 12 | 50 |
| **Damage per energy** | **6.25** | **2.92** | **2.10** |
| Damage / second | 25.0 | 23.3 | 26.3 |
| Energy / second | 4 | 8 | 12.5 |

Raw mining DPS really is near-flat (25 / 23.3 / 26.3) — the wiki is right about that. But bigger
guns are **~3× less power-efficient**, while bigger hulls carry **up to 5× the regen**. The
regen scales faster, so the "almost perfect counter" does not hold:

| Hull | Class | Regen | Cannon | **Sustained rock-damage/s** |
|---|---|---|---|---|
| **Advanced Vanir** | Line | **30/s** | Badger | **63.0** |
| Advanced Jotunn | Line | 25/s | Badger | 52.5 |
| **Advanced Glaive** | Escort | 15/s | Mole | **43.8** |
| **Adv Raptor / Adv Heavy Raider** | Strike | 6/s | Gopher | **37.5** |
| Adv Viper Mk VII / Adv Cylon War Raider | Strike | 6/s | Gopher | 37.5 |
| Adv Viper Mk III | Strike | 5.5/s | Gopher | 34.4 |
| Glaive | Escort | 12/s | Mole | 35.0 |
| Adv Viper Mk II / Adv Cylon Raider | Strike | 5/s | Gopher | 31.3 |
| Adv Raven / Malefactor | Strike | 3.6/s | Gopher | 22.5 |

**Bigger is faster at the rock — the wiki's "is bigger better for mining? No" is wrong.** An
Advanced Vanir out-mines an Advanced Raptor by **68%**, and that ignores the Badger's
**multi-targeting**, which damages several asteroids per shot and is not counted anywhere above.

Slot count stays irrelevant: at 63 damage/s the Vanir is only feeding ~2.4 of its 8 Badgers.
Every hull is power-limited to roughly 1.5–2.5 guns, so the wiki's *"you can't run more than
~2 mining guns"* holds for **all** classes.

### The catch: this measures damage at the rock, not ore per hour

Everything above assumes you are parked and firing. Travel is where big hulls give it back:

| | Adv Raptor | Adv Glaive | Adv Vanir |
|---|---|---|---|
| Speed | **52.5 m/s** | 37.5 | **27.5** |
| Boost speed | **77.5** | 57.5 | 42.5 |
| Boost cost | **0.6 Tyl/s** | 1.8 | **5.4** (9×) |
| FTL cost | **30 Tyl/LY** | 80 | **250** (8×) |
| Level required | 1 | 10 | 20 |
| Entry cost | 30k cubits | 50–60k cubits | 60k cubits |

A Vanir crawls between rocks at **half** a Raptor's speed and burns 8–9× the fuel doing it. How
much of the 68% survives depends entirely on how much of your cycle is spent flying, which
depends on belt density — so it is a measurement, not a calculation. Add that lines and escorts
are visible across a system and are the preferred prey of every PvP player in it.

**For a bot:** the throughput case for a line ship is real and large, but so is the travel tax,
and the bot cannot talk its way out of an ambush.

Gopher full curve (L1 → L10, **wiki values — see the warning at the top**):
damage 1–4 → 2–14, DPS 5 → 16, optimal 250 → 400. Power cost stays **2.00 at every level**.

> **This is the single most important fact for a power-limited miner:** damage per shot rises
> with level while power cost does not. Upgrading a Gopher directly increases **ore per power
> point**, which is the only thing that matters once regen is your bottleneck.

### Missile launchers (strike)

| | HD-70 "Lightning" / "Bereaver" | HD-96 "Nova" |
|---|---|---|
| Damage | *(wiki blank)* | 125–250 |
| DPS | — | +9.6 |
| Range | — | 200–600 |
| Reload | — | 12.0 s |

HD-82 "Longbow" is listed with no stats at all.

### Escort / line / capital weapons

The wiki lists names only — **every stat field is blank**:

- **Escort cannon:** MEC-E12 "Claw", MEC-E13 "Hurricane", MEC-E17 "Falcon"
- **Escort launcher:** HD-M50 "Thunderbolt"
- **Line, capital, Basestar:** marked `(tbc)` — nothing at all

### Battlestar Pegasus (Colonial only, fixed at level 15, not upgradeable)

| | Cannon Battery | Point Defence | Missile Battery |
|---|---|---|---|
| Damage | 325–410 | 8–12 | 600 flat |
| Armor piercing | 40 | 5 | 50 |
| Max range | 4,100 | 1,600 | 3,900 |
| Optimal | 2,500 | 1,000 | — |
| Accuracy | 130 | **600** | — |
| Reload | 4.25 s | 0.5 s | 5 s |
| Power | 25 | 4 | 40 |
| Firing arc | 180° | **360°** | 180° |
| Durability | 50,000 | 50,000 | 50,000 |

Since Update 55.5 the cannon battery cannot target escorts.

---

## Mining

### Resources

| Resource | Use | Exchange |
|---|---|---|
| **Tylium** | Fuel + basic currency. At 0 Tylium you cannot FTL jump. | — |
| **Titanium** | Repairs hull and installed systems. | Buy at 1 cubit / 10 Ti; sell at 1 Tyl / 2 Ti |
| **Water** | **No direct use — the only thing sellable for cubits.** | **1 cubit / 5 water** |

Sell water to Starbuck (Galactica CIC), Caprica Six (Basestar), or any Outpost Quartermaster.
Water is the most fought-over resource in the game; even same-faction players will jump a water
rock.

### Scan colours

| Colour | Contents |
|---|---|
| **Red** | Empty |
| **Yellow** | Tylium |
| **Purple** | Titanium |
| **Blue** | Water |

### The scanner

**Mineral Analysis Module** (Colonial) / **Mineral Analysis Cluster** (Cylon). Goes in a
**computer slot**.

| | Mineral Analysis Module | Experimental Mineral Analysis Module |
|---|---|---|
| Range | **0–2000** | 0–300 |
| Targets | one asteroid | everything in radius |
| Reload | 1 s | 1 s |
| Power | 50 | 50 |
| Consumable | none | **1 power cell per scan** |
| Cost | 2,000 Tyl | 5,000 Tyl + 4,000 cubits |

> **"It costs 50 units of power to operate at level 1; this is lowered dramatically with each
> level, down to 5 power per operation."**
>
> 50 → 5 power is a **90% cut**. At level 1 a single scan costs more than 8 seconds of a
> 6/s ship's entire regen. This is the highest-value upgrade in any mining build, full stop.

Medium and large computer slots get the longer-ranged **Array** and **Grid** variants.

### Asteroid mining

- Asteroid HP runs up to ~2,000, scaling with **sector threat level**. Threat 20 = biggest,
  richest, most numerous rocks, and the nastiest NPCs. Threat 1–10 is safe and poor.
- Resources are **invisible to other players** — asteroid mining does not show on anyone's
  sector map. Your only exposure is being seen on Dradis or by eye.
- Since **Update 50**, asteroid yield is **no longer shared** with your squadron. It all goes to
  whoever broke the rock.

### Planetoid mining

- Planetoids are **≥15× bigger** than the largest asteroid and **cannot be destroyed by cannons**.
- Scan, then pay **100 cubits** to call a Mining Ship. It extracts automatically — *even while
  you are offline* — until destroyed or the rock is exhausted.
- Yields **50,000+** in threat-20 systems, against ~7,000 at threat 4.
- **It is a PvP magnet by design.** A mining ship appears on the sector map for *both factions*,
  and destroying one is a daily assignment. An enemy NPC spawns within seconds of the call and
  keeps respawning.
- Squadron yield **is** split (asteroid yield is not), and only members present at the moment the
  ship was called get a share.

### The wiki's own fit advice

> *"The power costs of the asteroid scanner and mining guns make it relatively ineffective to use
> more than two mining guns and the scanner regardless of which Cylon/Colonial strike craft you
> use."*
>
> *"Two upgraded mining guns and an upgraded asteroid scanner is generally the most efficient
> setup given the power costs and the need for two normal guns for defense."*

Its recommended hull is the **Advanced Viper Mk II / Advanced Cylon Raider**, on four grounds:
stealthiness at range, speed between asteroid clusters, cheap equipment upgrades, and the claim
that larger hulls' extra power is exactly cancelled by their equipment's higher power cost.

**Three of those four are sound. The power claim is wrong within the strike class**, because
strike equipment costs the same on every strike hull while regen does not:

| Hull | Regen | Relative sustained mining rate |
|---|---|---|
| Adv Raptor / Adv Heavy Raider | **6/s** | **120%** |
| Adv Viper Mk VII / Adv Cylon War Raider | **6/s** | **120%** |
| Adv Viper Mk III | 5.5/s | 110% |
| Adv Viper Mk II / Adv Cylon Raider | 5/s | **100%** |
| Adv Raven / Malefactor | 3.6/s | 72% |

Once you are power-limited — which every strike is with two Gophers — sustained ore rate is
**directly proportional to regen**. The Advanced Raptor mines **20% faster than the wiki's own
pick**, and ties the Mk VII while carrying 25 more power and two more computer slots.

The Mk II's real advantages are survival and travel time, not throughput.

---

## Skills

19 groups, 57 skills total, all capped at level 10. A sub-skill can never exceed its parent.
**Training cannot be cancelled once started.**

| Skill level | Time | XP required |
|---|---|---|
| 1 | 15 min | 500 |
| 2 | 1 h | 2,000 |
| 3 | 2 h 15 | 4,500 |
| 4 | 4 h | 8,000 |
| 5 | 6 h 15 | 12,500 |
| 6 | 9 h | 18,000 |
| 7 | 12 h 15 | 24,500 |
| 8 | 16 h | 32,000 |
| 9 | 20 h 15 | 40,500 |
| 10 | 25 h | 50,000 |

**96 h 15 m and 192,000 XP to take one skill from 1 to 10.** All 57 to level 10 needs roughly
account level 106. Training continues while logged out.

### The four skills that matter for a mining bot

| Skill | Effect per level | At L10 |
|---|---|---|
| **Asteroid Mining** | +1% mining cannon **mining rate** | **+10% ore** |
| ↳ Ranged Extraction | +1% mining cannon max range | +10% range |
| ↳ Combat Mining | +1% mining cannon accuracy | +10% accuracy |
| **Electronics Operator** | +1% max DRADIS range | *(useless to the bot — see `README.md`)* |
| ↳ **Capacitor Management** | **+1% power recovery** | **+10% regen** |

> **Capacitor Management is the only way in the game to raise passive power regen.** There is no
> module for it. On a 6/s Advanced Raptor, level 10 is +0.6/s — and since sustained mining is
> proportional to regen, that is a straight **+10% ore per hour**, stacking with Asteroid
> Mining's +10%.
>
> Note it requires **Electronics Operator** as its parent, so you must train a Dradis skill that
> does nothing for you to reach it.

The wiki's advice — *"leave upgrading mining skills till last"* — is written for combat players.
Invert it for a mining bot.

### Full skill list

**Weapons:** Gunnery (+1% cannon optimal range) → Marksman (+1% accuracy), Precision Fire (+1%
crit offense) · Missile Combat (+1% missile max range) → Rapid Lock (−1% cooldown), Precision
Targeting (+1% crit) · Nuclear Warfare Specialist (+1% drain) → Nuclear Launch Timing (−1%
cooldown), Nuclear Launch Optimization (−0.5% power) · Rocket Specialization (−1% cooldown) →
Rocket Release Training (+1% lifetime), Rocket Payload Specialist (+1% crit) · **Asteroid Mining**
· KKC Specialization (−2.5% cooldown) → Coil Bleed Reduction (−1% power), KKC Armor Penetration
(+1%) · Heavy Missile Specialization (−1% cooldown) → Heavy Missile Timing (+1% lifetime), Payload
Specialist (+1% crit) · Flechette Cannon Training (−2.5% cooldown) → Muzzle Choke (+1% range),
Flechette Power Management (−1% power) · Machine Gun Specialization (−1.5% cooldown) → Burst
Control (−1% power), Sighting (+1% range)

**Hull:** Armored Combat (+1% crit defense) → Defensive Positioning (−1% durability loss),
Emergency Procedures (+1% hull points) · Damage Control (+1% hull recovery) → Repair Prioritization
(−1% DC cooldown), Mechanic (−1% DC power cost) · Munition Launchers (−1% mine cooldown) →
Guidance Theory (+1% lifetime), Munition Efficiency (−1% power) · Missile Evasion Training (+1%
decoy success) → Decoy Release Training (−1% cooldown), Decoy Power Management (−1% power)

**Engine:** Engineering (+1% flank speed) → Overcharge Regulation (+1% acceleration), Boost Tuning
(+1% boost speed) · Piloting (+1% turn speed) → Evasion (+1% avoidance), Thrusters (−1% RCS
cooldown) · Navigation (+1% FTL range) → Jump Calculations (−1% charge time), Reactor Monitoring
(−1% FTL cost)

**Computer:** Electronics Operator (+1% DRADIS range) → Countermeasures (+1% firewall),
**Capacitor Management (+1% power recovery)** · Electronic Support (−1% ES power cost) → Program
Integration (+1% duration), Signal Processing (+1% range) · Electronic Warfare (−1% EW power cost)
→ Hacking (+1% penetration), Jammer Cycling (−1% cooldown)

Skills marked for stealth ships (Rocket, Heavy Missile, Flechette, Machine Gun) and KKC
(Rhino/Marauder only) are wasted XP unless you own that hull.

---

## Modules — the big gap

**The wiki does not document ship modules.** The entire `Category:Equipment` contains three
pages: `Weapons`, `C31-Recharge Module`, and `HD-M50 "Thunderbolt"`. There is no page for the
High Density Capacitor Module or any other computer, hull or engine system.

Everything the wiki says about modules, in full:

### Ship-exclusive equipment

| Ship | Exclusive equipment |
|---|---|
| Rhino / Marauder | **D-1 energy capacitor** (increases max power *and* recharge rate), KKC Kinetic Kill Cannons, FBS-12 overcharge engine |
| Raven / Malefactor | Entire unique stealth item set |
| Glaive / Spectre | Range debuff computers |
| Scythe / Banshee | Reload disrupter computers, Armored angle computer, AC-42 cap blasters (capital targets only) |
| Maul / Wraith | AC-M nuke launcher, **C31-Recharge Module** |
| Jotunn / Jormung | **B-3 capacitor computers** (increase max power *and* recharge rate) |

> Note that **two hulls get modules that raise recharge rate** — the Rhino/Marauder's D-1 and the
> Jotunn/Jormung's B-3. Neither is available to a Raptor. For a Raptor, the only regen lever is
> the Capacitor Management skill.

### C31-Recharge Module (Maul / Wraith only, free with the ship)

Non-removable role slot, upgradeable to level 10 with **merits only**. At level 10:
+80 power, +29% power recharge, +29% hull recovery, +15,218 durability. The recharge bonus
applies only to **power cells** used with an auxiliary power cluster equipped; the hull bonus only
to **DC packs** with a damage control system equipped.

### Stealth-ship power items (Dev Blog 19)

All power generation was moved to **hull slots** for stealth ships. These items trade one
attribute for another — e.g. the **Ancillary Power Capacitor gives +30% power but −10% power
recovery**. That trade is the clearest statement anywhere that the game treats *pool* and
*recovery* as separate, independently-priced stats.

### Synthetic Aperture DRADIS (SAD)

A toggle module that raises DRADIS **and** visual range, added in Update 41 to let command ships
hunt stealth ships. The Ares/Hydra modules had their DRADIS component removed at the same time.
**Worthless to the bot** — it already sees the whole sector (see `README.md`).

### Where to get real module stats

1. **The in-game item store card** — authoritative for the live server, and the only source that
   reflects BSGOFUN's tuning.
2. **The bot's own slot-stat stream** — the server publishes real per-slot `MaxRange`,
   `Cooldown` and `PowerPointCost` for everything you have fitted. The diagnostics panel already
   prints these.

### Tuning Kits

Required to upgrade **any** ship system, and the **only** way past level 10. 1,000 cubits each,
cannot be sold. More kits per attempt = higher success chance. Also found in Unidentified Objects
and as Dradis Contact rewards.

---

## Boosters and consumables

| Type | Notable entries |
|---|---|
| **Mining** | 2× Mining Booster / 24 h (sells 50,000 Tyl), 3× / 24 h (120,000 Tyl). **Cannot be purchased** — reward only. |
| Experience | 1k / 5k / 25k instant XP; 1.5×–3× timed; +100% for 1 h / 7 days; Cavil Booster (+100%, 30 days, real money) |
| Merits | 1.5× / 2× timed; +100% for 1 h / 7 days; Adama Booster (30 days, real money). **Daily merit cap is 1,000.** |
| Skill | 1.5× / 12 h, 2× / 24 h, plus flat time reductions (−15 min, −4 h, −12 h, −25 h; most delisted) |
| **Divine Inspiration** | **Doubles XP, merits, loot, mining, skill training and interdiction cubits for 12 h.** Also the only booster that speeds up a skill *already training*. Dradis Contact jackpot, Top Gun 1st place (level 80+), or Ancient UOs. |
| FTL Override | 1,500 cubits — one-shot jump back to home fleet from anywhere |

**Salvage types:** Scrap Metal, Hull Plates, Power Conduits, Electronics, Weapon Parts, Drive
Components, Rare Elements, Heavy Metals, Reactor Core, Isotopes.

---

## Escort and line ships

Included for completeness — the wiki has **no stat tables** for any of them, only prose.

**Escorts** are anti-strike ships protecting the line. Slower and less accurate than strikes, but
much heavier. They can shoot down missiles, though not as easily as strikes. They rarely survive a
line ship.

| Faction | Escorts |
|---|---|
| Colonial | Scythe, Maul, Glaive, Halberd (+ Advanced of each) |
| Cylon | Banshee, Wraith, Spectre, Liche (+ Advanced of each) |

The Glaive is the escort **Command** ship — highest power and recharge of any escort, so it's the
best-case escort for mining. It still loses to a strike (see the mining-cannon comparison above).

| | Glaive | **Advanced Glaive** |
|---|---|---|
| Counterpart | Spectre | Advanced Spectre |
| Level req / Cost | 10 / 60,000 cubits | 10 / 50,000 cubits |
| Weapon / Hull / Engine / Computer | 6 / 2 / 2 / **5** | 6 / **3** / 2 / **5** |
| Hull points | 1,700 | 1,900 |
| Hull recovery | 15/s | 15/s |
| Durability / Armor | 30,000 / 25 | 30,000 / 25 |
| **Power** | **330** | **350** |
| **Power recharge** | **12/s** | **15/s** |
| Speed / Boost | 37.5 / 57.5 | 37.5 / 57.5 |
| **Boost cost** | **1.8 Tyl/s** | **1.8 Tyl/s** |
| Turning / Avoidance | 22.5°/s / 270 | 22.5°/s / 270 |
| Dradis / Visual | 3,500 / 750 | 3,500 / 750 |
| FTL range / cost | 8.25 LY / **80 Tyl/LY** | 8.25 LY / **80 Tyl/LY** |

Advancing gains +1 hull slot, +200 hull points, **+20 power, +3/s recharge**. Role bonus:
−50 power for FTL transponders (−100 advanced).

Beyond the mining maths, an escort costs a bot real throughput elsewhere: 37.5 m/s against a
strike's 52.5 is ~30% longer between rocks, at **3× the boost fuel and ~2.7× the FTL cost**.

| Faction | Lines |
|---|---|
| Colonial | Vanir (command), Jotunn ("baby battlestar"), Gungnir |
| Cylon | Hel (command), Jormung, Nidhogg |

| | **Advanced Vanir** | Advanced Jotunn |
|---|---|---|
| Role | **Command** | Assault |
| Counterpart | Advanced Hel | Advanced Jormung |
| Level req / Cost | 20 / 60,000 cubits | 20 / 60,000 cubits |
| Weapon / Hull / Engine / Computer | 8 / 2 / 2 / **5** | 8 / **5** / 2 / 2 |
| Hull points | 4,550 | **6,000** (14,091 max fitted) |
| Hull recovery | 35/s | **37/s** |
| Durability / Armor | 70,000 / 40 | **74,000 / 45** |
| **Power** | **900** | 750 |
| **Power recharge** | **30/s** | 25/s |
| Speed / Boost | 27.5 / 42.5 | 25 / 40 |
| Boost cost | 5.4 Tyl/s | 6.3 Tyl/s |
| FTL range / cost | 11 LY / 250 Tyl/LY | 10 LY / 250 Tyl/LY |
| Dradis / Visual | 4,000 / **1,000** | 3,500 / 350 |

The Advanced Vanir has **the most base power and the equal-highest recharge of any line ship**,
which makes it the fastest asteroid miner we have numbers for — see the mining-cannon comparison
above. Advancing it gains +2 weapon slots, +1 computer slot, +1,050 hull, +250 power and +2/s
recharge (it does *not* gain a hull slot, unlike the Advanced Glaive).

---

## What this wiki does not have

Do not go looking — these are genuinely absent:

- **Any module/system stats** beyond the six ship-exclusive items listed above
- Escort, line, capital and Basestar **weapon stats** (all `(tbc)`)
- **Power regen formula** — whether regen is flat or scales with max pool is never stated
  anywhere, and the client never transmits it as a rate (only `PowerPoints` value updates). The
  Ancillary Power Capacitor's separate ±% for pool and recovery implies they are independent, but
  that is inference, not a source.
- Escort and line **ship stat tables**
- Asteroid HP by threat level beyond "up to ~2,000"
- Anything specific to **BSGOFUN's** tuning

## Sources

- [Battlestar Galactica Online Wiki](https://bsgo.fandom.com/wiki/Battlestar_Galactica_Online_Wiki)
- [Weapons](https://bsgo.fandom.com/wiki/Weapons) ·
  [Mining](https://bsgo.fandom.com/wiki/Mining) ·
  [Asteroid Mining](https://bsgo.fandom.com/wiki/Asteroid_Mining) ·
  [Skills](https://bsgo.fandom.com/wiki/Skills) ·
  [Strike Ships](https://bsgo.fandom.com/wiki/Strike_Ships) ·
  [Escort Ships](https://bsgo.fandom.com/wiki/Escort_Ships)
- [Advanced Raptor](https://bsgo.fandom.com/wiki/Advanced_Raptor) ·
  [Raptor](https://bsgo.fandom.com/wiki/Raptor) ·
  [Advanced Viper Mk VII](https://bsgo.fandom.com/wiki/Advanced_Viper_Mark_VII) ·
  [Advanced Viper Mk III](https://bsgo.fandom.com/wiki/Advanced_Viper_Mark_III) ·
  [Advanced Viper Mk II](https://bsgo.fandom.com/wiki/Advanced_Viper_Mark_II) ·
  [Viper Mk VII](https://bsgo.fandom.com/wiki/Viper_Mark_VII) ·
  [Viper Mk II](https://bsgo.fandom.com/wiki/Viper_Mark_II) ·
  [Advanced Rhino](https://bsgo.fandom.com/wiki/Advanced_Rhino) ·
  [Advanced Raven Mk VI-R/A](https://bsgo.fandom.com/wiki/Advanced_Raven_Mark_VI-R/A)
- [Advanced Cylon Raider](https://bsgo.fandom.com/wiki/Advanced_Cylon_Raider) ·
  [Advanced Heavy Raider](https://bsgo.fandom.com/wiki/Advanced_Heavy_Raider) ·
  [Advanced Cylon War Raider](https://bsgo.fandom.com/wiki/Advanced_Cylon_War_Raider)
- [Category:Equipment](https://bsgo.fandom.com/wiki/Category:Equipment) ·
  [C31-Recharge Module](https://bsgo.fandom.com/wiki/C31-Recharge_Module) ·
  [Tuning Kits](https://bsgo.fandom.com/wiki/Tuning_Kits) ·
  [Boosters](https://bsgo.fandom.com/wiki/Boosters) ·
  [Dev Blog 19](https://bsgo.fandom.com/wiki/Dev_Blog_19)

The [namu.wiki page](https://en.namu.wiki/w/Battlestar%20Galactica%20Online) you found is a
general overview with no equipment tables — nothing in it that isn't covered above.
