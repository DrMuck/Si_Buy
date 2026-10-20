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

Purchases are refused with a reason: not controlling a unit, not enough credits, cooldown, Shrimp
lock, too close to an enemy HQ / Nest / Queen, too far from your own HQ / Nest.

## Admin commands

All gate on SilicaAdminMod's `CanAdminExecute(Power.Generic)` and work while the economy is off.

| Command | Effect |
|--|--|
| `/buy on` / `/buy off` | Master switch (`Enabled`). Balances are kept while off |
| `/buy starter <N>` | `StarterCreditsPerPlayer` per round, 0 disables |
| `/buy give <steamId or name> <amount>` | Grant (or deduct, negative) credits |
| `/buy buildingreward on\|off` | Structure-kill rewards |
| `/buy structures on\|off` | *Buildings* category in the menu |
| `/buy adminonly on\|off` | Restrict `/buy` to admins |
| `/buy bounty [list\|on\|off\|reload\|add <steamId> <amount> [name]\|remove <steamId>]` | Headhunter bounty list |

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
| `BuySpawnDistance` | `15` | Metres in front of the buyer |
| `BuyMinDistanceFromEnemyCritical` | `400` | Cylindrical no-buy radius around enemy HQ / Nest / Queen (0 = off) |
| `BuyMaxDistanceFromOwnCritical` | `800` | Purchases must land within this radius of an own HQ / Nest (0 = unlimited) |
| `ShrimpBuyUnlockSeconds` | `600` | Shrimps cannot be bought before this round time |
| `BuyCooldownSeconds` | `240` | Per-player cooldown after a purchase |
| `BuyStructuresEnabled` | `false` | *Buildings* category |

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
```

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
