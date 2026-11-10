# Si_Buy

> Per-player **credit economy** with an in-round **`/buy` purchase menu** for [Silica](https://store.steampowered.com/app/504900/Silica/) dedicated servers.

Players earn credits (starter grant, kills, structure kills, headhunter bounties, and whatever other
mods pay them) and spend them with `/buy` to spawn units or structures next to their current position.
Server-only MelonLoader mod, no client component.

Carved out of [Si_KingOfTheHill](https://github.com/DrMuck/Si_KingOfTheHill) in October 2026 so the
economy runs on its own and other mods can plug into one small API. KGT pays its zone-presence
credits through that API; [Si_Logistics](https://github.com/DrMuck/Si_Logistics) pays delivery bonuses.

## Features

- **`/buy` menu** — categories by production building (Barracks, Light Factory, ..., Spawning Cysts), `/1`-`/N` navigation, optional *Buildings* category
- **Purchase rules** — spawn 15 m ahead of the buyer, own-base leash, enemy-base exclusion, Shrimp unlock timer, per-player cooldown, admin-only switch
- **Credit sources** — starter credits per round, kill and structure-kill rewards as a share of the victim's cost, headhunter bounties from `Bounties.json`
- **Unit upgrades** (1.6.0, per weapon stat since 1.7.0, off by default) — `/buy upgrade` raises Movement or one weapon stat (damage, rate of fire, reload, accuracy, magazine, projectile speed) of the unit you control; only the buyer's client gets the boost, through OverrideManager packets
- **Live modded prices** — reads `Si_UnitBalance_Dump.json` and applies `Si_UnitBalance_Config.json` `cost_mult` each round; falls back to the vanilla `ConstructionData` costs without Si_UnitBalance
- **Cross-mod API** — grant, spend and read balances by Steam64 ID via reflection, no compile-time dependency

## Installation

1. [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.x on the dedicated server (Mono, `net472` references)
2. [Silica Admin Mod](https://github.com/data-bomb/Silica) (`Si_AdminMod.dll`, required: chat commands, admin checks)
3. Recommended: [Si_UnitBalance](https://github.com/DrMuck/Silica-UnitBalance) so prices and kill rewards see the modded costs
4. Copy `Si_Buy.dll` to `Mods/`

First start writes `UserData/cfg/Buy/Si_Buy_Config.json` and `UserData/cfg/Buy/Bounties.json`.
If a pre-split `UserData/cfg/KingOfTheHill/Si_KingOfTheHill_Config.json` (or the older `UserData/KingOfTheHill_cfg/` one) exists, its economy fields
(same names) seed the new config, and its `Bounties.json` is copied over. `Enabled` follows the old
KGT `Mode`: 1 and 2 ran the economy, 3 did not.

## Player commands

| Command | Effect |
|--|--|
| `/buy` | Open the menu (categories), or re-render the current level |
| `/buy <N>` | Pick category / unit N |
| `/1`-`/N`, `/back`, `/0` | Navigate while the menu is open |
| `/buy back`, `/buy exit` | Same without the shared shortcuts |
| `/buy status` | Economy state and your balance |
| `/buy <C> <U>` | Direct purchase: category C, item U in menu order; no menu is opened |
| `/buy <name>` | Direct purchase by display name, e.g. `/buy Heavy Tank` |
| `/buy upgrade` | The upgrades of the unit you control, numbered: level, current → next value, price, levels used / max. Opens the menu so `/1`-`/N` buys |
| `/buy upgrade <N>` or `/buy upgrade <key>` | Buy one level: `move` (Movement), `pri.dmg`, `pri.rate`, `pri.reload`, `pri.acc`, `pri.mag`, `pri.speed`, and the same with `sec.` |

Purchases are refused with a reason: not controlling a unit, not enough credits, cooldown, Shrimp
lock, too close to an enemy HQ / Nest / Queen, too far from your own HQ / Nest.

## Unit upgrades

Switched on with `/buy upgrades on` (`UpgradesEnabled`). A player in FPS mode buys levels for the
unit they control: vehicle, aircraft, alien creature or their own soldier. There is one Movement
upgrade and, per weapon (primary, secondary), one upgrade per stat. Each has its own level; level L
improves it by its kind's step of the base value, linearly: factor F = 1 + step x L. The step is
`UpgradeStepPercent` for every kind unless the kind has its own (`UpgradeStep<Kind>`, set with
`/buy upgradestep <kind> <percent>`). The Movement upgrade is one purchase with one level, but its
speed, jump and turning parts each use their own kind's step.
Larger-is-better stats (speeds, turn rate, damage, magazine, projectile speed) are multiplied by F,
smaller-is-better stats (time between shots, reload time, muzzle spread, turning circle) are divided by F.
`UpgradeMaxPerUnit` caps the sum of all levels on one unit. One level costs the unit's effective buy
cost (with Si_UnitBalance `cost_mult`) x `UpgradeCostFraction`, at least `UpgradeMinCost`. No purchase
cooldown and no distance rules apply.

| Upgrade (key) | What one level raises | Shown as |
|--|--|--|
| Movement (`move`) | all movement of the unit together: the speed fields Si_UnitBalance's `move_speed_mult` scales (`MoveSpeed`, `FlyMoveSpeed`, `WalkSpeed` / `RunSpeed` / `SprintSpeed`, `ForwardSpeed` / `StrafeSpeed` / `TurboSpeed`), jump (soldier `JumpSpeed`, creature `PounceSpeed`), turning (`TurnSpeed` of hover, air and creatures, `FlyTurnSpeed`, wheeled `TurningCircleRadius`) | speed and jump in m/s, turning in % |
| Damage (`pri.dmg`, `sec.dmg`) | projectile impact / ricochet / penetrating / splash damage | the weapon's largest damage value |
| Rate of fire (`.rate`) | time between shots | rpm, or s/shot when slower than one shot a second |
| Reload (`.reload`) | reload time | s |
| Accuracy (`.acc`) | muzzle spread | degrees |
| Magazine (`.mag`) | magazine size (never a magazine of 0 = infinite) | rounds |
| Projectile speed (`.speed`) | projectile speed; lifetime cut by the same factor so the range stays; never on instant-hit weapons | m/s |

Primary = the first turret's primary weapon, the soldier's gun, or the creature's primary ranged attack;
Secondary = the first turret's secondary weapon or the creature's secondary ranged attack. Every kind
has a switch (`/buy upgradestat <kind> on|off`, config `Upgrade*`); move, jump and turn switch parts of
the Movement upgrade. Only what is reachable on a unit is offered: creature melee attacks and a second
turret are not, nor Movement on units whose movement the server simulates.

With the Si_ModUI client 1.6.0 (server 1.7.0) the Buy page shows the upgrades of the unit you control
as its own section above the unit grid: one row per upgrade with level pips, current → next value,
its percent per level, price and a BUY button, greyed out when maxed or unaffordable. Older clients reach the same list
through the "Upgrade your unit" command, whose numbered lines are buttons.

Admins get the settings on the Buy admin page in three headed sections (Si_ModUI server 1.8.0 /
client 1.7.0): *Unit upgrades* (on/off, general step, levels per unit, price factor, minimum price, refit
speed), *Upgrade stats: Movement* and *Upgrade stats: Weapon* with one line per stat: its ON/OFF switch,
its step % field with SET (marked "(general)" while it follows the general step) and, when it has an
own step, a "use general" button; the line's hint spells out what the percent does to the game value.
Older ModUI clients or servers show the same controls as separate rows. Each stat's hint ends with
"Not offered for: …" when it has exclusions. The *Upgrade exclusions* editor below it starts with an
"Edit exclusions of" selector (one button per stat, remembered per admin) and then shows, for that stat,
one EXCLUDED / OFFERED toggle per production building (its hint lists the units it covers) and per
buyable unit, under Sol / Centauri / Alien headings. A unit excluded through its building reads
"(via Heavy Factory)" (Si_ModUI server 1.8.1) and is offered again by setting the building to OFFERED.
No name is typed anywhere.

The boost exists only on the buyer's client; the server and every other player keep the normal
values. An upgrade belongs to one unit and its buyer: another player driving it gets nothing, a new
unit of the same type starts at zero, and the upgrades end when the unit is destroyed, the buyer
leaves, or the round ends.

- **Damage / projectile speed** are held on the buyer's client while they control the upgraded unit
  and reverted when they leave it.
- **Movement, rate of fire, reload, accuracy, magazine** are copied into a unit when the game creates it,
  so buying them **refits** the unit: it is replaced in place by a new instance with the same health,
  ammunition and reload state, and the player stays in control. The refit is refused (nothing charged)
  above `UpgradeMaxSwapSpeed`, with passengers aboard, for critical units (the Queen), and while seated
  as a passenger. Other players see the unit despawn and respawn in the same spot. State other mods
  keep on the old unit object (orders, groups, cargo) does not carry over.

**Exclusions.** `UpgradeExclusions` lists, per stat kind, the units it is not offered for: an entry is
`building:<production building>` (the building the buy menu files the unit under, e.g. `building:Heavy Factory`)
or `unit:<name>` (display name or ObjectInfo name, e.g. `unit:Bomber`); whole name, case-insensitive. An
excluded stat does not appear in the list or the ModUI section, is refused with the reason when bought by
key, is left out of refits, and `GetUpgradeFactor` reports 1 for it. A unit that already carries a level of
a stat that becomes excluded keeps the level (it still counts toward `UpgradeMaxPerUnit`) and its effect
until its next refit; a held damage or projectile-speed value is reverted at once. Default: magazine is
excluded for the Heavy Factory and Ultra Heavy Factory vehicles of Sol and Centauri (Hover Tank, Railgun
Tank, Barrage Truck, Pulse Truck, Repair Rig, Siege Tank; Combat Tank, Heavy Tank, Pyro Tank, Rocket Tank,
Repair Truck, Crimson Tank), the Bomber and the Freighter: in live tests a bigger magazine broke these
units. The likely cause is the shot-by-shot reload most of them use: the owner's client reports its shot
count to the server and the other players after each reload step, and with a bigger magazine on that one
client the count runs past the magazine everybody else simulates, so their copy of the turret stops firing
until it has caught up.

A weapon slot without a projectile (the empty secondary slot of a turret whose second weapon is another
turret, a bomb rack's empty primary) offers no rate of fire, reload, accuracy or magazine upgrade (1.9.0).

**Step changes during a round.** A unit keeps the steps it was upgraded with. A changed step (general or
per kind) reaches an upgraded unit's stat at the next purchase of that same stat (for Movement: of
Movement); the unit's other stats keep their recorded steps, so changing one step never alters a stat the
player did not buy again (1.9.1; 1.8.x/1.9.0 re-stepped every stat at any purchase). Lists and the ModUI
section show the current value at the unit's own steps and the next value at the configured steps.
`GetUpgradeFactor` reports what the unit carries. A stat that follows the general step shows `general` in
its ModUI field, not a number: only a typed number gives it an own step.

Other mods can read the factor in force: `Si_Buy.Buy.GetUpgradeFactor(Unit unit, string stat)`, see below.
`/buy upgrade dump` (admin) writes `UserData/cfg/Buy/Si_Buy_UpgradeDump.txt`: per controllable unit
type the upgrades offered and every member with its component, base value, direction, field type,
projectile assets, and the client-physics flag.

## Admin commands

All gate on SilicaAdminMod's `CanAdminExecute(Power.Generic)` and work while the economy is off.

| Command | Effect |
|--|--|
| `/buy on` / `/buy off` (also `/buy enabled on\|off`) | Master switch (`Enabled`). Balances are kept while off |
| `/buy starter <N>` | `StarterCreditsPerPlayer` per round, 0 disables |
| `/buy give <steamId or name> <amount>` | Grant (or deduct, negative) credits |
| `/buy buildingreward on\|off` | Structure-kill rewards |
| `/buy structures on\|off` | *Buildings* category in the menu |
| `/buy adminonly on\|off` | Restrict `/buy` to admins |
| `/buy bounty [list\|on\|off\|reload\|add <steamId> <amount> [name]\|remove <steamId>]` | Headhunter bounty list |
| `/buy upgrades on\|off` | Unit upgrades (`UpgradesEnabled`). Off reverts held damage boosts |
| `/buy upgradestep <percent>` | `UpgradeStepPercent`, the general step (above 0, at most 500) |
| `/buy upgradestep <kind> <percent\|default>` | One kind's own step (`UpgradeStep<Kind>`); `default` (or -1) follows the general step again. Kinds as for `upgradestat`. `/buy upgradestep` alone lists the steps. Alias `/buy upgradesteps` |
| `/buy upgrademax <levels>` | `UpgradeMaxPerUnit` |
| `/buy upgradecost <fraction>` | `UpgradeCostFraction` |
| `/buy upgrademincost <credits>` | `UpgradeMinCost` |
| `/buy upgradeswapspeed <m/s>` | `UpgradeMaxSwapSpeed` |
| `/buy upgradestat <kind> [on\|off]` | Which stats can be upgraded (`move`, `jump`, `turn`, `damage`, `firerate`, `reload`, `accuracy`, `magazine`, `projspeed`); without on/off it toggles |
| `/buy upgradeexclude [<kind> [add\|remove\|toggle <name>]]` | List or edit `UpgradeExclusions`. `<name>` is a production building or a unit (or `building:X` / `unit:X` when a name is both); unknown names get suggestions. Also `/buy upgradeexclude add\|remove <kind> <name>`, and the ModUI editor forms `pick <kind>` / `set <building:X\|unit:X> on\|off` (on the stat picked by that admin) |
| `/buy upgrade dump` | Write the upgrade diagnostic file |

## Configuration

`UserData/cfg/Buy/Si_Buy_Config.json` (see `Si_Buy_Config.example.json`):

| Field | Default | Meaning |
|--|--|--|
| `Enabled` | `true` | Economy on/off |
| `StarterCreditsPerPlayer` | `0` | Credits granted once per player per round (5 s poll catches late joiners) |
| `BuyAdminOnly` | `false` | Only admins may open the menu |
| `BountiesEnabled` | `true` | Headhunter bounties (`Bounties.json`) |
| `RewardKillFraction` | `0.10` | Killer receives this share of the victim's effective cost |
| `RewardResetOnGameEnd` | `true` | Wipe all balances at round end |
| `StructureKillRewardsEnabled` | `true` | Destroying enemy structures pays too |
| `BuySpawnDistance` | `15` | Metres in front of the buyer where the spot search starts |
| `BuySpawnSearchRadius` | `40` | Units: a free floor spot is searched in rings out to this distance (never inside a unit or building, under a roof, or below a floor). Nothing free = purchase refused |
| `StructureSpawnSearchRadius` | `60` | Structures: the game's placement rules (grid, slope, obstruction) are checked on a spiral out to this distance; the spot whose foundation hangs least above the ground wins |
| `BuyMinDistanceFromEnemyCritical` | `400` | Cylindrical no-buy radius around enemy HQ / Nest / Queen (0 = off) |
| `BuyMaxDistanceFromOwnCritical` | `800` | Purchases must land within this radius of an own HQ / Nest (0 = unlimited) |
| `ShrimpBuyUnlockSeconds` | `600` | Shrimps cannot be bought before this round time |
| `AirBuyUnlockSeconds` | `720` | Sol / Centauri aircraft (Air Factory units) cannot be bought before this round time (0 = off) |
| `BuyCooldownSeconds` | `240` | Per-player cooldown after a purchase |
| `BuyStructuresEnabled` | `false` | *Buildings* category |
| `UpgradesEnabled` | `false` | Unit upgrades |
| `UpgradeStepPercent` | `25` | Improvement per level, percent of the base value (linear), for every kind without its own step |
| `UpgradeMaxPerUnit` | `3` | Sum of all upgrade levels on one unit |
| `UpgradeCostFraction` | `0.5` | Price of one level = unit's effective buy cost x this |
| `UpgradeMinCost` | `100` | Price floor; also the price for units without a known cost |
| `UpgradeMaxSwapSpeed` | `3` | Refitting upgrades are refused above this speed (m/s) |
| `UpgradeMoveSpeed` | `true` | Movement raises speeds |
| `UpgradeJumpSpeed` | `true` | Movement raises jump / pounce |
| `UpgradeTurnSpeed` | `true` | Movement raises turning |
| `UpgradeWeaponDamage` | `true` | Damage upgrades |
| `UpgradeWeaponFireRate` | `true` | Rate-of-fire upgrades |
| `UpgradeWeaponReload` | `true` | Reload upgrades |
| `UpgradeWeaponAccuracy` | `true` | Accuracy upgrades |
| `UpgradeWeaponMagazine` | `true` | Magazine upgrades (the server keeps the vanilla magazine) |
| `UpgradeWeaponProjectileSpeed` | `false` | Projectile-speed upgrades (range kept) |
| `UpgradeStepMoveSpeed`, `UpgradeStepJumpSpeed`, `UpgradeStepTurnSpeed`, `UpgradeStepWeaponDamage`, `UpgradeStepWeaponFireRate`, `UpgradeStepWeaponReload`, `UpgradeStepWeaponAccuracy`, `UpgradeStepWeaponMagazine`, `UpgradeStepWeaponProjectileSpeed` | `-1` | Own step of that kind in percent per level; -1 = `UpgradeStepPercent`. Allowed: -1 or above 0 up to 500 (on load, other negatives and 0 become -1, values above 500 become 500; an invalid `UpgradeStepPercent` becomes 25, or 500 when above) |
| `UpgradeExclusions` | magazine: Heavy Factory, Ultra Heavy Factory, Bomber, Freighter | Per stat kind, `building:<name>` / `unit:<name>` entries the stat is not offered for. A config without the field gets the default; keep a kind with `[]` to clear it |
| `UpgradeConfigVersion` | `2` | Written by the mod. A config below 2 (1.6.0) gets `UpgradeWeaponMagazine` switched on once |

## Cross-mod API

Resolve the type by name and call the static members; all primitive parameters, so no reference to
game types is needed:

```csharp
// asm.GetType("Si_Buy.Buy") over AppDomain.CurrentDomain.GetAssemblies()
public static bool BuyActive                                                    // economy on?
public static bool GrantExternalCredits(long steamId, int amount, string reason) // false while off / invalid
public static bool SpendCredits(long steamId, int amount, string reason)         // false while off / insufficient
public static int  GetCreditsBySteamId(long steamId)
public static int  GetCredits(Player player)
public static string? ModUIData()   // catalog JSON for Si_ModUI: {cmd, enabled, shrimpLock, catalog:{Sol:[{c,u:[[name,cost]]}]}}
public static float GetUpgradeFactor(Unit unit, string stat)   // 1.6.0: F >= 1 in force on that unit while its buyer controls it, else 1
```

`GetUpgradeFactor` stats: `move`, `jump`, `turn` (the Movement upgrade, when the unit has members of
that kind), `damage`, `firerate`, `reload`, `accuracy`, `magazine`, `projspeed`. For weapon stats it
returns the larger factor of primary and secondary. It never throws.

`public static string? ModUIPlayerData(ulong steamId)` (1.7.0) is Si_ModUI's per-player hook: the
player's upgrade list as an `actions` panel, rebuilt every 5 s.

`steamId` is the Steam64 (`(long)player.PlayerID.m_ID`). Declare `"Buy"` in
`MelonOptionalDependencies` so Si_Buy loads first. The names and signatures are the ones
`Si_KingOfTheHill.KingOfTheHill` exposed before the split; KGT 0.5+ still forwards them to Si_Buy
for consumers that have not been updated.

## Building

```
cd Si_Buy && dotnet build -c Release
```

References come from `include/netstandard2.1/` (copy `SilicaCore.dll`, `Si_AdminMod.dll`, Unity
modules, and the `MelonLoader/net472` DLLs from your server install; the folder is git-ignored).
Target stays `netstandard2.1`, the server runs Mono.

## License

MIT, see [LICENSE](LICENSE) if present; otherwise same terms as Si_KingOfTheHill.
