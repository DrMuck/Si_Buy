using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace Si_Buy
{
    public partial class Buy
    {
        // === Config model ===
        // Field names are identical to the ones Si_KingOfTheHill used before the split,
        // so an existing KingOfTheHill config can seed this one on first start.
        public class BuyConfig
        {
            // Master switch. Off → /buy refuses, no rewards, API returns false / 0.
            public bool Enabled = true;

            // Starter credits granted to each player once per round. 0 disables.
            // Players joining mid-round receive the grant on connect (5 s poll).
            public int StarterCreditsPerPlayer = 0;

            // When true, /buy is restricted to admins (anyone who passes
            // SilicaAdminMod's CanAdminExecute check). Default false = everyone can /buy.
            public bool BuyAdminOnly = false;

            // Master switch for the headhunter bounty system. When false, no announcements
            // fire and no kill payouts are made (even if Bounties.json has entries).
            public bool BountiesEnabled = true;

            // === Rewards ===
            public float RewardKillFraction = 0.10f;                 // killer gets this % of victim's effective cost
            public bool  RewardResetOnGameEnd = true;                // wipe credits between rounds (at round end AND at round start)
            public bool  StructureKillRewardsEnabled = true;         // destroying an enemy building pays the killer (cost * RewardKillFraction). /buy buildingreward on|off

            // === Purchase rules ===
            public float BuySpawnDistance = 15.0f;                   // metres in front of player to place purchased unit
            public float BuySpawnSearchRadius = 40.0f;               // units: how far from the buyer a free spot is searched (rings from BuySpawnDistance outwards). Nothing free = purchase refused.
            public float StructureSpawnSearchRadius = 60.0f;         // structures: search radius for the flattest valid spot (game placement rules: grid, slope, obstruction)
            public float BuyMinDistanceFromEnemyCritical = 400.0f;   // refuse purchase if spawn would land within this CYLINDRICAL (horizontal, ignores altitude) radius of any enemy HQ/Nest/Queen (0 = disabled)
            public float BuyMaxDistanceFromOwnCritical = 800.0f;     // refuse purchase if spawn would land FURTHER than this CYLINDRICAL radius from the player's nearest own HQ / Nest (0 = unlimited). Fails open if the team owns no HQ/Nest.
            public float ShrimpBuyUnlockSeconds = 600.0f;            // Shrimp units cannot be bought until this many seconds into the round. 0 = no time lock.
            public float AirBuyUnlockSeconds = 720.0f;               // Sol/Centauri aircraft (Air Factory units) cannot be bought until this many seconds into the round. 0 = no time lock.
            public float BuyCooldownSeconds = 240.0f;                // Anti-spam: seconds between two purchases of the same player. 0 = disabled.
            public bool  BuyStructuresEnabled = false;               // list buyable structures in a 'Buildings' category. /buy structures on|off

            // === Unit upgrades (1.6.0; per weapon stat since 1.7.0, Upgrades.cs) ===
            public bool  UpgradesEnabled = false;                    // /buy upgrade for the unit you control. /buy upgrades on|off
            public float UpgradeStepPercent = 25.0f;                 // each level improves a stat by this % of the base value (linear: F = 1 + step x level)
            public int   UpgradeMaxPerUnit = 3;                      // sum of all levels (Movement + every weapon stat) on one unit
            public float UpgradeCostFraction = 0.5f;                 // price per level = the unit's effective buy cost x this
            public int   UpgradeMinCost = 100;                       // price floor (also the price when the unit has no known cost)
            public float UpgradeMaxSwapSpeed = 3.0f;                 // m/s: upgrades of handling stats refit the unit and are refused above this speed
            // Which stats exist. Movement is one item; its three kinds can be switched off inside it. /buy upgradestat <kind> on|off
            public bool  UpgradeMoveSpeed = true;                    // Movement: drive / walk / run / sprint / fly speeds
            public bool  UpgradeJumpSpeed = true;                    // Movement: soldier jump, creature pounce
            public bool  UpgradeTurnSpeed = true;                    // Movement: turn rate (hover, air, creatures), turning circle (wheeled)
            public bool  UpgradeWeaponDamage = true;                 // weapon stat: projectile damage
            public bool  UpgradeWeaponFireRate = true;               // weapon stat: shorter time between shots
            public bool  UpgradeWeaponReload = true;                 // weapon stat: shorter reload
            public bool  UpgradeWeaponAccuracy = true;               // weapon stat: smaller muzzle spread
            public bool  UpgradeWeaponMagazine = true;               // weapon stat: larger magazine (the server keeps the vanilla magazine)
            public bool  UpgradeWeaponProjectileSpeed = false;       // weapon stat: faster projectiles, lifetime cut so the range stays
            // Step per stat kind (1.8.0): % of the base value per level for that kind only; -1 = use UpgradeStepPercent.
            // Allowed: -1, or above 0 up to 500. /buy upgradestep <kind> <percent|default>
            public float UpgradeStepMoveSpeed = -1f;
            public float UpgradeStepJumpSpeed = -1f;
            public float UpgradeStepTurnSpeed = -1f;
            public float UpgradeStepWeaponDamage = -1f;
            public float UpgradeStepWeaponFireRate = -1f;
            public float UpgradeStepWeaponReload = -1f;
            public float UpgradeStepWeaponAccuracy = -1f;
            public float UpgradeStepWeaponMagazine = -1f;
            public float UpgradeStepWeaponProjectileSpeed = -1f;
            // Exclusions (1.9.0): per stat kind, the units it is NOT offered for. An entry is "building:<production building>"
            // (as in the buy menu, e.g. "building:Heavy Factory") or "unit:<unit name>" (display name or ObjectInfo name, e.g.
            // "unit:Bomber"); case-insensitive, whole name. /buy upgradeexclude <kind> add|remove <name>. A config without this
            // field gets the default below; to clear a kind keep the key with an empty list.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, List<string>> UpgradeExclusions = DefaultUpgradeExclusions();
            public int   UpgradeConfigVersion = 0;                  // 2 = 1.7.0 defaults applied once (UpgradeWeaponMagazine on); do not edit
        }

        // Kinds spelled out here (not UPG_KINDS): Cfg is created during type initialisation, and the order of static
        // initialisers across the partial files is not defined.
        static Dictionary<string, List<string>> DefaultUpgradeExclusions()
        {
            var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in new[] { "move", "jump", "turn", "damage", "firerate", "reload", "accuracy", "magazine", "projspeed" })
                d[k] = new List<string>();
            // Magazine breaks on these (live test 2026-11-09): every Heavy / Ultra Heavy Factory vehicle of Sol and Centauri,
            // the Sol Bomber and the Centauri Freighter. See README "Exclusions" for the cause.
            d["magazine"].AddRange(new[] { "building:Heavy Factory", "building:Ultra Heavy Factory", "unit:Bomber", "unit:Freighter" });
            return d;
        }

        // === Runtime singleton + persistence ===

        public static BuyConfig Cfg = new BuyConfig();
        static string _configPath = "";

        static void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var loaded = JsonConvert.DeserializeObject<BuyConfig>(File.ReadAllText(_configPath));
                    if (loaded != null)
                    {
                        Cfg = loaded;
                        MelonLogger.Msg("[Buy] Config loaded from " + _configPath);
                        // Re-save so newly-added fields appear in the JSON for tuning. Preserves
                        // user-customised values (Newtonsoft only overwrites fields it parsed).
                        SaveConfig();
                        return;
                    }
                }
                else if (TrySeedFromKingOfTheHillConfig())
                {
                    SaveConfig();
                    return;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[Buy] Failed to load config: " + ex.Message + " — writing defaults.");
            }
            SaveConfig();
            MelonLogger.Msg("[Buy] Default config written to " + _configPath);
        }

        /// <summary>
        /// First start after the split: take the economy fields out of an existing
        /// Si_KingOfTheHill config (same field names) so the server keeps its tuning.
        /// Enabled follows KGT's old Mode: 1 (Full) and 2 (BuyOnly) ran the economy,
        /// 3 (Disabled) or Enabled=false did not.
        /// </summary>
        static bool TrySeedFromKingOfTheHillConfig()
        {
            string kohPath = Paths.FirstExisting(
                Path.Combine(Paths.UserData, "cfg", "KingOfTheHill", "Si_KingOfTheHill_Config.json"),
                Path.Combine(Paths.UserData, "KingOfTheHill_cfg", "Si_KingOfTheHill_Config.json"));
            if (kohPath == null) return false;
            try
            {
                string json = File.ReadAllText(kohPath);
                var seeded = JsonConvert.DeserializeObject<BuyConfig>(json);
                if (seeded == null) return false;
                var root = JObject.Parse(json);
                bool kohEnabled = root["Enabled"]?.Value<bool>() ?? true;
                int mode = root["Mode"]?.Value<int>() ?? 1;
                seeded.Enabled = kohEnabled && (mode == 1 || mode == 2);
                Cfg = seeded;
                MelonLogger.Msg($"[Buy] No config yet — seeded {_configPath} from {kohPath} (KGT Mode={mode} → Enabled={Cfg.Enabled})");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[Buy] Could not seed config from Si_KingOfTheHill: " + ex.Message);
                return false;
            }
        }

        static void SaveConfig()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                // JsonConvert.SerializeObject(_, Formatting.Indented) is safe on Mono Newtonsoft.
                // (JToken.ToString(Formatting) is NOT — the server's Newtonsoft lacks it.)
                File.WriteAllText(_configPath, JsonConvert.SerializeObject(Cfg, Formatting.Indented));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[Buy] Failed to save config: " + ex.Message);
            }
        }
    }
}
