/*
 UnitDump.cs — unit catalogue for prices and kill rewards.

 Loads Si_UnitBalance_Dump.json once (DisplayName -> built_at / cost / faction /
 prefab hint / structure flag) and overlays Si_UnitBalance's cost_mult per round so
 /buy prices and kill rewards see the live modded numbers. Falls back to a scan of
 the loaded ConstructionData assets when the dump is missing.
*/

using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Si_Buy
{
    public partial class Buy
    {
        // ----- Unit classification (from Si_UnitBalance dump) -----

        // DisplayName -> "built_at" (production building name). Empty string for auxiliaries.
        static readonly Dictionary<string, string> _unitToBuilding = new Dictionary<string, string>();
        // Extended dump data (used by reward + /buy system).
        static readonly Dictionary<string, int> _unitCost = new Dictionary<string, int>();           // DisplayName -> effective cost (vanilla * UnitBalance cost_mult)
        static readonly Dictionary<string, int> _unitCostVanilla = new Dictionary<string, int>();    // DisplayName -> vanilla cost (from dump; never mutated)
        static readonly Dictionary<string, string> _unitFaction = new Dictionary<string, string>(); // DisplayName -> "Sol"/"Centauri"/"Alien"
        static readonly Dictionary<string, string> _unitPrefab = new Dictionary<string, string>();  // DisplayName -> prefab name (internal minus "ObjectInfo_" prefix)
        // Marks dump entries that are structures (is_structure: true). Used by /buy to
        // exclude structures from the category list while still keeping their cost
        // entries available for structure-kill reward lookups.
        static readonly HashSet<string> _isStructure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Parallel cost lookup by ObjectInfo.name (asset name, e.g.
        // "ObjectInfo_Sol_HoverHarvester"). Needed because Si_UnitBalance's dump
        // disambiguates faction-shared display names (e.g. both factions' harvesters
        // are "Harvester" → dump stores "Sol Harvester" / "Cent Harvester") while at
        // runtime ObjectInfo.DisplayName returns just "Harvester". Without this
        // fallback, kill-reward / structure-kill-reward lookups on harvesters and HQs
        // miss the dict and silently pay nothing.
        static readonly Dictionary<string, int> _unitCostByInternal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Reverse map: dump display name -> raw internal asset name (e.g.
        // "Sol Harvester" -> "ObjectInfo_Sol_HoverHarvester"). Used to keep
        // _unitCostByInternal in sync when ApplyUnitBalanceCostOverrides scales costs.
        static readonly Dictionary<string, string> _internalByDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Faction-aware structure catalog for the /buy "Buildings" menu. Built one entry
        // per dump ROW (NOT keyed by display name) because Sol and Centauri structures
        // share display names — "Barracks", "Air Factory", "Refinery", turrets, etc. A
        // display-keyed dict silently drops the duplicate, so the faction whose row is
        // written last (Sol) wins and the other faction (Centauri) is left with only its
        // uniquely-named HQ. Units don't collide, so they keep using the dicts above.
        // Cost stays faction-independent (looked up from _unitCost by display name); only
        // the PREFAB must be carried per-row so the correct faction variant spawns.
        internal class StructRow
        {
            public string Display    = "";   // e.g. "Barracks"
            public string Prefab     = "";   // name hint for the spawn DB, e.g. "Cent_Barracks"
            public string Faction    = "";   // "Sol" / "Centauri" / "Alien"
            public string ObjectInfo = "";   // dump "internal" (e.g. "ObjectInfo_Alien_Nest") — authoritative prefab key
        }
        static readonly List<StructRow> _structRows = new List<StructRow>();

        /// <summary>
        /// Look up a unit/structure's effective cost using its ObjectInfo. Tries the
        /// display name first (matches dump entries for most units), falls back to the
        /// asset name (handles harvesters / HQs whose DisplayName collides between
        /// factions and where the dump used a disambiguated label). Returns false if
        /// neither key resolves.
        /// </summary>
        public static bool TryGetEffectiveCost(ObjectInfo? info, out int cost, out string lookupKey)
        {
            cost = 0;
            lookupKey = "";
            if (info == null) return false;

            string displayName = info.DisplayName ?? "";
            if (!string.IsNullOrEmpty(displayName) && _unitCost.TryGetValue(displayName, out cost) && cost > 0)
            {
                lookupKey = displayName;
                return true;
            }

            string assetName = info.name ?? "";
            if (!string.IsNullOrEmpty(assetName) && _unitCostByInternal.TryGetValue(assetName, out cost) && cost > 0)
            {
                lookupKey = displayName.Length > 0 ? displayName : assetName;
                return true;
            }

            return false;
        }
        static bool _dumpLoaded;

        static void LoadUnitDump()
        {
            if (_dumpLoaded) return;
            _dumpLoaded = true; // single-shot regardless of outcome

            string path = Paths.UnitBalanceFile("Si_UnitBalance_Dump.json");
            if (!File.Exists(path))
            {
                MelonLogger.Warning($"[Buy] Unit dump not found at {path} — prices and kill rewards fall back to live ConstructionData costs");
                return;
            }
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var unitsToken = root["units"];
                if (unitsToken == null || unitsToken.Type != JTokenType.Array)
                {
                    MelonLogger.Warning("[Buy] Unit dump 'units' array missing");
                    return;
                }
                // Iterate JToken directly — casting to JArray trips the netstandard2.1 /
                // net472 Newtonsoft.Json type resolution (ICloneable / IBindingList interfaces).
                int count = 0;
                int structuresFlagged = 0;
                foreach (var u in unitsToken)
                {
                    string? name = u["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    // Mark structures so /buy can filter them out of the category list
                    // (otherwise factories under Headquarters / Quantum Cortex / Research
                    // Facility pollute the player-facing buyable list). We still load
                    // them into the cost dict so the structure-kill reward handler can
                    // look up a price.
                    bool isStructure = u["is_structure"]?.Value<bool>() ?? false;
                    if (isStructure) { _isStructure.Add(name!); structuresFlagged++; }

                    _unitToBuilding[name!] = u["built_at"]?.ToString() ?? "";
                    int vanillaCost = u["cost"]?.Value<int>() ?? 0;
                    _unitCostVanilla[name!] = vanillaCost;
                    _unitCost[name!] = vanillaCost;  // overlay applied in ApplyUnitBalanceCostOverrides
                    _unitFaction[name!] = u["faction"]?.ToString() ?? "";

                    // Parallel by-internal-name lookup — see _unitCostByInternal field comment.
                    string rawInternal = u["internal"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(rawInternal))
                    {
                        _unitCostByInternal[rawInternal] = vanillaCost;
                        _internalByDisplay[name!] = rawInternal;
                    }

                    // Best-effort prefab NAME derived from "internal" (ObjectInfo asset name).
                    //   Sol_Soldier_Heavy  → game DB key "Sol_Soldier_Heavy"      (humans keep prefix)
                    //   Alien_Scorpion     → game DB key "Scorpion"               (creatures DROP prefix)
                    // This is only a FALLBACK hint now — no single string rule maps ObjectInfo
                    // names onto prefab names (alien STRUCTURES keep the prefix: "Alien_Nest";
                    // "Alien_CrabHorned" is prefab "Crab_Horned"; "FusionReactor" is
                    // "FusionReactor_01"). /buy resolves prefabs through ObjectInfo.Prefab
                    // instead — see ResolveBuyPrefab in Buy.cs — and falls back to this name.
                    string internalName = u["internal"]?.ToString() ?? "";
                    if (internalName.StartsWith("ObjectInfo_"))
                        internalName = internalName.Substring("ObjectInfo_".Length);
                    if (internalName.StartsWith("Alien_") && !isStructure)
                        internalName = internalName.Substring("Alien_".Length);
                    _unitPrefab[name!] = internalName;

                    // Faction-aware structure catalog (see _structRows comment). One row
                    // per structure so Sol/Centauri same-named buildings don't collide.
                    if (isStructure)
                    {
                        _structRows.Add(new StructRow
                        {
                            Display    = name!,
                            Prefab     = internalName,
                            Faction    = u["faction"]?.ToString() ?? "",
                            ObjectInfo = rawInternal,
                        });
                    }

                    count++;
                }
                MelonLogger.Msg($"[Buy] Loaded {count} dump entries ({structuresFlagged} marked as structures — included for kill rewards, hidden from /buy)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] Failed to parse unit dump: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuild _unitCost from the vanilla dump snapshot + Si_UnitBalance's cost_mult
        /// overrides. Reading ConstructionData.ResourceCost at runtime returns vanilla
        /// because OverrideManager applies an overlay rather than mutating prefab fields,
        /// so we parse Si_UnitBalance_Config.json directly.
        ///
        /// Idempotent: always reset _unitCost from _unitCostVanilla before multiplying,
        /// so calling per round-start can't compound.
        /// </summary>
        static void ApplyUnitBalanceCostOverrides()
        {
            // 1. Ensure we have a vanilla baseline. If the dump didn't populate
            //    _unitCostVanilla (Si_UnitBalance not installed), seed it from a live
            //    ConstructionData scan. That read returns vanilla values whether or
            //    not Si_UnitBalance is loaded (its OverrideManager is a runtime overlay,
            //    not a field mutation), so it's a safe baseline either way.
            if (_unitCostVanilla.Count == 0)
            {
                int scanned = SeedVanillaCostsFromConstructionData();
                MelonLogger.Msg($"[Buy] No dump — seeded {scanned} vanilla unit costs from live ConstructionData (fallback)");
            }

            // 2. Reset _unitCost to vanilla snapshot.
            foreach (var kv in _unitCostVanilla) _unitCost[kv.Key] = kv.Value;

            string path = Paths.UnitBalanceFile("Si_UnitBalance_Config.json");
            if (!File.Exists(path))
            {
                MelonLogger.Msg($"[Buy] {path} not found — rewards use vanilla unit costs");
                return;
            }

            // 2. Apply cost_mult overrides from UnitBalance config.
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var unitsToken = root["units"];
                if (unitsToken == null || unitsToken.Type != JTokenType.Object)
                {
                    MelonLogger.Warning("[Buy] Si_UnitBalance_Config.json: 'units' object missing");
                    return;
                }

                int touched = 0, skipped = 0;
                foreach (var prop in unitsToken.Children<JProperty>())
                {
                    string unitName = prop.Name;
                    // Skip pseudo-keys: _teleport, _comment_*, etc.
                    if (string.IsNullOrEmpty(unitName) || unitName[0] == '_') continue;
                    var u = prop.Value;
                    if (u == null || u.Type != JTokenType.Object) continue;

                    var mt = u["cost_mult"];
                    if (mt == null) continue;
                    float mult = mt.Value<float>();
                    if (mult <= 0f) continue;

                    if (!_unitCostVanilla.TryGetValue(unitName, out int vanilla) || vanilla <= 0)
                    {
                        skipped++;  // UnitBalance has this unit but our dump doesn't
                        continue;
                    }

                    int modded = (int)Math.Round(vanilla * mult);
                    if (modded != vanilla)
                    {
                        _unitCost[unitName] = modded;
                        // Mirror into by-internal map so the runtime fallback lookup
                        // (used for harvesters / HQs where DisplayName collides) also
                        // sees the scaled cost.
                        if (_internalByDisplay.TryGetValue(unitName, out string rawInternal))
                            _unitCostByInternal[rawInternal] = modded;
                        touched++;
                    }
                }
                MelonLogger.Msg($"[Buy] Applied cost_mult overrides from Si_UnitBalance_Config.json: {touched} units changed, {skipped} not in dump");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] ApplyUnitBalanceCostOverrides failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Last-resort fallback when Si_UnitBalance_Dump.json is absent (e.g. the server
        /// runs KGT without Si_UnitBalance installed). Walks every loaded ConstructionData
        /// and pulls DisplayName + ResourceCost into _unitCostVanilla. The values are
        /// vanilla because OverrideManager doesn't mutate prefab fields, which is exactly
        /// what we want as a baseline. Returns the number of entries written.
        /// </summary>
        static int SeedVanillaCostsFromConstructionData()
        {
            int n = 0;
            try
            {
                var allCD = Resources.FindObjectsOfTypeAll<ConstructionData>();
                foreach (var cd in allCD)
                {
                    if (cd == null || cd.ObjectInfo == null) continue;
                    string name = cd.ObjectInfo.DisplayName;
                    if (string.IsNullOrEmpty(name)) continue;
                    int cost = cd.ResourceCost;
                    if (cost <= 0) continue;
                    if (_unitCostVanilla.ContainsKey(name)) continue;
                    _unitCostVanilla[name] = cost;
                    n++;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] SeedVanillaCostsFromConstructionData failed: {ex.Message}");
            }
            return n;
        }
    }
}
