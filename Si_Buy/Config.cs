using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
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
            public bool  RewardResetOnGameEnd = true;                // wipe credits between rounds
            public bool  StructureKillRewardsEnabled = true;         // destroying an enemy building pays the killer (cost * RewardKillFraction). /buy buildingreward on|off

            // === Purchase rules ===
            public float BuySpawnDistance = 15.0f;                   // metres in front of player to place purchased unit
            public float BuyMinDistanceFromEnemyCritical = 400.0f;   // refuse purchase if spawn would land within this CYLINDRICAL (horizontal, ignores altitude) radius of any enemy HQ/Nest/Queen (0 = disabled)
            public float BuyMaxDistanceFromOwnCritical = 800.0f;     // refuse purchase if spawn would land FURTHER than this CYLINDRICAL radius from the player's nearest own HQ / Nest (0 = unlimited). Fails open if the team owns no HQ/Nest.
            public float ShrimpBuyUnlockSeconds = 600.0f;            // Shrimp units cannot be bought until this many seconds into the round. 0 = no time lock.
            public float BuyCooldownSeconds = 240.0f;                // Anti-spam: seconds between two purchases of the same player. 0 = disabled.
            public bool  BuyStructuresEnabled = false;               // list buyable structures in a 'Buildings' category. /buy structures on|off
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
            string kohPath = Path.Combine("UserData", "KingOfTheHill_cfg", "Si_KingOfTheHill_Config.json");
            if (!File.Exists(kohPath)) return false;
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
