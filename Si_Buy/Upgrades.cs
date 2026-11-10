/*
 Upgrades.cs — per-unit, per-buyer stat upgrades bought with credits (1.6.0, per stat since 1.7.0).

 A player in FPS mode buys levels for ONE stat of the unit they control: move speed, jump,
 turning, and per weapon (primary / secondary) damage, rate of fire, reload, accuracy, magazine,
 projectile speed. Level L of a stat improves it by the factor
   F = 1 + step/100 * L
 where step is the kind's own UpgradeStep<Kind> (1.8.0), or UpgradeStepPercent when that is -1. The Movement bundle has
 one level, but its speed, jump and turning parts each use their kind's step.
 (larger-is-better members x F, smaller-is-better members / F). UpgradeMaxPerUnit caps the sum of
 all stat levels on one unit. The boost reaches ONLY the buyer's client, through OverrideManager
 packets sent to that one player; vanilla clients, no client mod. Server values never change.

 Two delivery paths, decided by where the game reads a member:
   Baked  (prefab component fields: speeds, turning, fire interval, reload, spread, magazine) are
          copied into a unit when it is instantiated, so the buyer's client must create a NEW
          instance while the override is in place: the unit is swapped (see SwapUnit).
   Live   (ProjectileData damage / speed) are read from the shared asset on every shot, so the
          buyer's client simply holds Set values while it controls the upgraded unit.

 Upgrades belong to one unit instance and its buyer (key = the Unit reference; NetIDs are
 reused). Destroyed / deleted unit, buyer leaving, round start / end, map change: gone.

 OverrideManager client receive (OverrideManager.cs 654-698): per target a member list, each
 member = name + type byte (+ value). The type byte must equal the field's OM type or the client
 ignores it; target "r" reverts everything and is never sent. For a member the ObjectInfo does
 not declare, the client takes the FIRST MonoBehaviour in Prefab.GetComponentsInChildren(true)
 whose concrete type declares it (DeclaredOnly, OverrideManager.cs 847-866); ResolveOmMember
 replicates that lookup on the server prefab to know what the client will touch.

 Si_ModUI: ModUIPlayerData(steamId) publishes this player's upgrade list as an "actions" panel
 (one row per stat, level, current -> next value, price, a button sending /buy upgrade <key>).
*/

using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace Si_Buy
{
    public partial class Buy
    {
        // ===================================================================
        // Stats a player buys — key (chat / ModUI), label, kind (what it improves), group
        // ===================================================================

        const int UPG_MOVEMENT = 0, UPG_PRIMARY = 1, UPG_SECONDARY = 2;
        static readonly string[] UPG_GROUP_LABEL = { "Movement", "Primary weapon", "Secondary weapon" };

        sealed class UpgStat
        {
            public string Key = "", Label = "", Kind = "";
            public int Group;
        }

        static UpgStat St(string key, string label, string kind, int group) => new UpgStat { Key = key, Label = label, Kind = kind, Group = group };

        // What a player buys, in list order. Movement is ONE item: a level raises every movement member of the unit
        // together (speeds, jump, turning; each kind still has its own config switch). Weapon stats are bought one by one.
        // Kind = the GetUpgradeFactor stat name and config switch of a weapon stat ("move" for the bundle).
        static readonly UpgStat[] UPG_STATS =
        {
            St("move", "Movement", "move", UPG_MOVEMENT),
            St("pri.dmg", "Damage", "damage", UPG_PRIMARY),
            St("pri.rate", "Rate of fire", "firerate", UPG_PRIMARY),
            St("pri.reload", "Reload", "reload", UPG_PRIMARY),
            St("pri.acc", "Accuracy", "accuracy", UPG_PRIMARY),
            St("pri.mag", "Magazine", "magazine", UPG_PRIMARY),
            St("pri.speed", "Projectile speed", "projspeed", UPG_PRIMARY),
            St("sec.dmg", "Damage", "damage", UPG_SECONDARY),
            St("sec.rate", "Rate of fire", "firerate", UPG_SECONDARY),
            St("sec.reload", "Reload", "reload", UPG_SECONDARY),
            St("sec.acc", "Accuracy", "accuracy", UPG_SECONDARY),
            St("sec.mag", "Magazine", "magazine", UPG_SECONDARY),
            St("sec.speed", "Projectile speed", "projspeed", UPG_SECONDARY),
        };
        static readonly string[] UPG_KINDS = { "move", "jump", "turn", "damage", "firerate", "reload", "accuracy", "magazine", "projspeed" };

        static UpgStat? StatByKey(string key)
        {
            foreach (var s in UPG_STATS) if (s.Key == key) return s;
            return null;
        }

        static string StatKeyFor(string kind, int slot)
        {
            if (slot == UPG_MOVEMENT) return "move";   // speeds, jump and turning are one bundle
            string shortKind = kind == "damage" ? "dmg" : kind == "firerate" ? "rate" : kind == "accuracy" ? "acc" : kind == "magazine" ? "mag" : kind == "projspeed" ? "speed" : kind;
            return (slot == UPG_PRIMARY ? "pri." : "sec.") + shortKind;
        }

        static bool UpgStatEnabled(string kind)
        {
            switch (kind)
            {
                case "move":      return Cfg.UpgradeMoveSpeed;
                case "jump":      return Cfg.UpgradeJumpSpeed;
                case "turn":      return Cfg.UpgradeTurnSpeed;
                case "damage":    return Cfg.UpgradeWeaponDamage;
                case "firerate":  return Cfg.UpgradeWeaponFireRate;
                case "reload":    return Cfg.UpgradeWeaponReload;
                case "accuracy":  return Cfg.UpgradeWeaponAccuracy;
                case "magazine":  return Cfg.UpgradeWeaponMagazine;
                case "projspeed": return Cfg.UpgradeWeaponProjectileSpeed;
                default:          return false;
            }
        }

        static bool SetUpgStatEnabled(string kind, bool on)
        {
            switch (kind)
            {
                case "move":      Cfg.UpgradeMoveSpeed = on; return true;
                case "jump":      Cfg.UpgradeJumpSpeed = on; return true;
                case "turn":      Cfg.UpgradeTurnSpeed = on; return true;
                case "damage":    Cfg.UpgradeWeaponDamage = on; return true;
                case "firerate":  Cfg.UpgradeWeaponFireRate = on; return true;
                case "reload":    Cfg.UpgradeWeaponReload = on; return true;
                case "accuracy":  Cfg.UpgradeWeaponAccuracy = on; return true;
                case "magazine":  Cfg.UpgradeWeaponMagazine = on; return true;
                case "projspeed": Cfg.UpgradeWeaponProjectileSpeed = on; return true;
                default:          return false;
            }
        }

        // ----- step per kind (1.8.0): UpgradeStep<Kind> in percent, -1 = the general UpgradeStepPercent -----

        const float UPG_STEP_MAX = 500f;
        static bool ValidStep(float v) => v > 0f && v <= UPG_STEP_MAX;

        static float StepOverride(string kind)
        {
            switch (kind)
            {
                case "move":      return Cfg.UpgradeStepMoveSpeed;
                case "jump":      return Cfg.UpgradeStepJumpSpeed;
                case "turn":      return Cfg.UpgradeStepTurnSpeed;
                case "damage":    return Cfg.UpgradeStepWeaponDamage;
                case "firerate":  return Cfg.UpgradeStepWeaponFireRate;
                case "reload":    return Cfg.UpgradeStepWeaponReload;
                case "accuracy":  return Cfg.UpgradeStepWeaponAccuracy;
                case "magazine":  return Cfg.UpgradeStepWeaponMagazine;
                case "projspeed": return Cfg.UpgradeStepWeaponProjectileSpeed;
                default:          return -1f;
            }
        }

        static bool SetStepOverride(string kind, float v)
        {
            switch (kind)
            {
                case "move":      Cfg.UpgradeStepMoveSpeed = v; return true;
                case "jump":      Cfg.UpgradeStepJumpSpeed = v; return true;
                case "turn":      Cfg.UpgradeStepTurnSpeed = v; return true;
                case "damage":    Cfg.UpgradeStepWeaponDamage = v; return true;
                case "firerate":  Cfg.UpgradeStepWeaponFireRate = v; return true;
                case "reload":    Cfg.UpgradeStepWeaponReload = v; return true;
                case "accuracy":  Cfg.UpgradeStepWeaponAccuracy = v; return true;
                case "magazine":  Cfg.UpgradeStepWeaponMagazine = v; return true;
                case "projspeed": Cfg.UpgradeStepWeaponProjectileSpeed = v; return true;
                default:          return false;
            }
        }

        /// <summary>The step (percent per level) a new purchase of this kind gets.</summary>
        static float UpgStep(string kind)
        {
            float o = StepOverride(kind);
            return ValidStep(o) ? o : Cfg.UpgradeStepPercent;
        }

        static Dictionary<string, float> SnapshotSteps()
        {
            var d = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var k in UPG_KINDS) d[k] = UpgStep(k);
            return d;
        }

        static string Pct(float step) => "+" + step.ToString("0.#", CultureInfo.InvariantCulture) + "%";

        /// <summary>"+25% per level" when every switched-on kind has the same step, else "+20-50% per level, by stat".</summary>
        static string StepSummary()
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var k in UPG_KINDS)
            {
                if (!UpgStatEnabled(k)) continue;
                float s = UpgStep(k);
                lo = Mathf.Min(lo, s); hi = Mathf.Max(hi, s);
            }
            if (lo > hi) return Pct(Cfg.UpgradeStepPercent) + " per level";
            if (hi - lo < 0.05f) return Pct(lo) + " per level";
            return "+" + lo.ToString("0.#", CultureInfo.InvariantCulture) + "-" + hi.ToString("0.#", CultureInfo.InvariantCulture) + "% per level, by stat";
        }

        /// <summary>The kinds whose step differs from the general one, "damage 40%, magazine 50%"; "" when none.</summary>
        static string StepOverrides()
        {
            var parts = new List<string>();
            foreach (var k in UPG_KINDS)
                if (ValidStep(StepOverride(k)) && Math.Abs(StepOverride(k) - Cfg.UpgradeStepPercent) > 0.001f)
                    parts.Add(k + " " + StepOverride(k).ToString("0.#", CultureInfo.InvariantCulture) + "%");
            return string.Join(", ", parts);
        }

        // ----- exclusions (1.9.0): Cfg.UpgradeExclusions[kind] = "building:<production building>" / "unit:<name>" -----

        const string EXCL_BUILDING = "building:", EXCL_UNIT = "unit:";

        /// <summary>Load-time cleanup: known kinds only (all present), trimmed entries, unknown prefixes reported and dropped.</summary>
        static bool SanitizeUpgradeExclusions()
        {
            bool changed = false;
            var src = Cfg.UpgradeExclusions ?? new Dictionary<string, List<string>>();
            var clean = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in UPG_KINDS) clean[k] = new List<string>();
            foreach (var kv in src)
            {
                string kind = (kv.Key ?? "").Trim().ToLowerInvariant();
                if (!clean.ContainsKey(kind)) { MelonLogger.Warning($"[Buy] Config: UpgradeExclusions key '{kv.Key}' is not a stat kind ({string.Join(", ", UPG_KINDS)}) — dropped."); changed = true; continue; }
                foreach (var raw in kv.Value ?? new List<string>())
                {
                    string e = (raw ?? "").Trim();
                    if (!TryParseExclusion(e, out string type, out string name))
                    {
                        MelonLogger.Warning($"[Buy] Config: UpgradeExclusions[{kind}] entry '{raw}' needs \"{EXCL_BUILDING}<name>\" or \"{EXCL_UNIT}<name>\" — dropped.");
                        changed = true; continue;
                    }
                    string norm = type + name;
                    if (!clean[kind].Exists(x => x.Equals(norm, StringComparison.OrdinalIgnoreCase))) clean[kind].Add(norm);
                    if (norm != raw) changed = true;
                }
            }
            if (Cfg.UpgradeExclusions == null || Cfg.UpgradeExclusions.Count != clean.Count) changed = true;
            Cfg.UpgradeExclusions = clean;
            return changed;
        }

        static bool TryParseExclusion(string entry, out string type, out string name)
        {
            type = name = "";
            if (string.IsNullOrEmpty(entry)) return false;
            if (entry.StartsWith(EXCL_BUILDING, StringComparison.OrdinalIgnoreCase)) type = EXCL_BUILDING;
            else if (entry.StartsWith(EXCL_UNIT, StringComparison.OrdinalIgnoreCase)) type = EXCL_UNIT;
            else return false;
            name = entry.Substring(type.Length).Trim();
            return name.Length > 0;
        }

        // ObjectInfo name -> dump display name (the dump's own key; the runtime DisplayName can differ: "Sol Harvester" / "Harvester")
        static readonly Dictionary<string, string> _dumpNameByInternal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string? DumpNameFor(string infoName)
        {
            if (_dumpNameByInternal.Count != _internalByDisplay.Count)
            {
                _dumpNameByInternal.Clear();
                foreach (var kv in _internalByDisplay) if (!_dumpNameByInternal.ContainsKey(kv.Value)) _dumpNameByInternal[kv.Value] = kv.Key;
            }
            return _dumpNameByInternal.TryGetValue(infoName ?? "", out var n) ? n : null;
        }

        /// <summary>The production building of a unit type ("Heavy Factory"), "" when unknown.</summary>
        static string BuildingFor(UpgProfile prof)
        {
            string? dump = DumpNameFor(prof.InfoName);
            if (dump != null && _unitToBuilding.TryGetValue(dump, out var b) && !string.IsNullOrEmpty(b)) return b;
            if (_unitToBuilding.TryGetValue(prof.Display, out b) && !string.IsNullOrEmpty(b)) return b;
            return "";
        }

        /// <summary>The exclusion entry that removes this kind from this unit type, or null.</summary>
        static string? ExcludedBy(UpgProfile? prof, string kind)
        {
            if (prof == null || Cfg.UpgradeExclusions == null || !Cfg.UpgradeExclusions.TryGetValue(kind, out var list) || list == null || list.Count == 0) return null;
            string building = BuildingFor(prof);
            string? dump = DumpNameFor(prof.InfoName);
            foreach (var e in list)
            {
                if (!TryParseExclusion(e, out string type, out string name)) continue;
                if (type == EXCL_BUILDING)
                {
                    if (building.Length > 0 && building.Equals(name, StringComparison.OrdinalIgnoreCase)) return e;
                }
                else if (name.Equals(prof.Display, StringComparison.OrdinalIgnoreCase) || name.Equals(prof.InfoName, StringComparison.OrdinalIgnoreCase)
                         || (dump != null && name.Equals(dump, StringComparison.OrdinalIgnoreCase)))
                    return e;
            }
            return null;
        }

        static string ExclusionLabel(string entry) => TryParseExclusion(entry, out _, out string name) ? name : entry;

        // ----- exclusion editor (1.9.1): per admin a selected stat; toggles per production building and buyable unit -----

        const string ExclusionPickDefault = "magazine";
        static readonly Dictionary<long, string> _exclPick = new Dictionary<long, string>();
        static string ExclusionPickFor(long sid) => _exclPick.TryGetValue(sid, out var k) ? k : ExclusionPickDefault;

        sealed class ExclRow
        {
            public string Text = "", Label = "", Desc = "", Value = "", Group = "", Entry = "", Via = "";
            public Newtonsoft.Json.Linq.JArray Choices = null!;
        }

        static bool IsExcludedDirect(string kind, string entry)
            => Cfg.UpgradeExclusions != null && Cfg.UpgradeExclusions.TryGetValue(kind, out var l) && l != null && l.Exists(x => x.Equals(entry, StringComparison.OrdinalIgnoreCase));

        /// <summary>The excluded building entry a unit (dump name) inherits its exclusion from, "" when none.</summary>
        static string ExcludedVia(string kind, string unitName)
        {
            if (!_unitToBuilding.TryGetValue(unitName, out var b) || string.IsNullOrEmpty(b)) return "";
            return IsExcludedDirect(kind, EXCL_BUILDING + b) ? b : "";
        }

        /// <summary>
        /// The editor rows for one stat, in page order: the stat selector, the production buildings (hint = the units they
        /// cover), then the buyable units per faction (from the buy catalog). Value "on" = not offered; a unit excluded
        /// through its building carries Via (shown in the admin's label, and its own toggle cannot take it out).
        /// </summary>
        static List<ExclRow> ExclusionEditorRows(string kind)
        {
            var rows = new List<ExclRow>();
            var kinds = new Newtonsoft.Json.Linq.JArray();
            foreach (var k in UPG_KINDS) kinds.Add(new Newtonsoft.Json.Linq.JArray(k, UpgStatAdminLabel(k)));
            rows.Add(new ExclRow
            {
                Text = "/buy upgradeexclude pick", Label = "Edit exclusions of", Value = kind, Choices = kinds, Group = "Upgrade exclusions",
                Desc = "Pick a stat; the toggles below show and change the buildings and units it is not offered for.",
            });
            if (_unitFaction.Count == 0) return rows;   // catalog not loaded yet (before the first round start)
            Newtonsoft.Json.Linq.JArray Toggle() => new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JArray("on", "EXCLUDED"), new Newtonsoft.Json.Linq.JArray("off", "OFFERED"));
            var buildings = new List<string>();
            var coverage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var units = new List<KeyValuePair<string, string>>();   // faction, unit
            foreach (string faction in new[] { "Sol", "Centauri", "Alien" })
                foreach (var cat in BuildCategories(faction))
                {
                    if (string.Equals(cat, STRUCTURES_CATEGORY, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!coverage.ContainsKey(cat)) { coverage[cat] = new List<string>(); buildings.Add(cat); }
                    var names = new List<string>();
                    foreach (var item in BuildUnits(faction, cat)) { names.Add(item.Display); units.Add(new KeyValuePair<string, string>(faction, item.Display)); }
                    if (names.Count > 0) coverage[cat].Add(faction + ": " + string.Join(", ", names));
                }
            foreach (var b in buildings)
            {
                string entry = EXCL_BUILDING + b;
                rows.Add(new ExclRow
                {
                    Text = "/buy upgradeexclude set " + entry, Label = b + " (all its units)", Entry = entry, Choices = Toggle(),
                    Value = IsExcludedDirect(kind, entry) ? "on" : "off", Group = "Exclusions: production buildings",
                    Desc = "Covers " + string.Join("; ", coverage[b]) + ".",
                });
            }
            foreach (var u in units)
            {
                string entry = EXCL_UNIT + u.Value, via = ExcludedVia(kind, u.Value);
                rows.Add(new ExclRow
                {
                    Text = "/buy upgradeexclude set " + entry, Label = u.Value, Entry = entry, Choices = Toggle(), Via = via,
                    Value = IsExcludedDirect(kind, entry) || via.Length > 0 ? "on" : "off", Group = "Exclusions: " + u.Key + " units",
                });
            }
            return rows;
        }

        /// <summary>"Heavy Factory, Bomber" for a kind; "" when nothing is excluded.</summary>
        static string ExclusionList(string kind)
        {
            if (Cfg.UpgradeExclusions == null || !Cfg.UpgradeExclusions.TryGetValue(kind, out var list) || list == null) return "";
            var parts = new List<string>();
            foreach (var e in list) parts.Add(ExclusionLabel(e));
            return string.Join(", ", parts);
        }

        /// <summary>Known names for /buy upgradeexclude: production buildings and unit types (dump names, ObjectInfo names).</summary>
        static void KnownExclusionNames(out List<string> buildings, out List<string> units)
        {
            buildings = new List<string>(); units = new List<string>();
            foreach (var kv in _unitToBuilding)
            {
                if (_isStructure.Contains(kv.Key)) continue;
                if (!string.IsNullOrEmpty(kv.Value) && !buildings.Exists(x => x.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))) buildings.Add(kv.Value);
                if (!units.Exists(x => x.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) units.Add(kv.Key);
            }
            foreach (var kv in _internalByDisplay)
                if (!_isStructure.Contains(kv.Key) && !units.Exists(x => x.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))) units.Add(kv.Value);
            buildings.Sort(StringComparer.OrdinalIgnoreCase); units.Sort(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Turn what an admin typed into an entry: "building:X" / "unit:X" as given (X must be known), else a production
        /// building or unit name (exact, case-insensitive). Ambiguous or unknown names return false with a message.
        /// </summary>
        static bool ResolveExclusionName(string input, out string entry, out string why)
        {
            entry = ""; why = "";
            KnownExclusionNames(out var buildings, out var units);
            string text = input.Trim();
            string forced = "";
            if (TryParseExclusion(text, out string t, out string n)) { forced = t; text = n; }
            string? b = buildings.Find(x => x.Equals(text, StringComparison.OrdinalIgnoreCase));
            string? u = units.Find(x => x.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (forced == EXCL_BUILDING && b != null) { entry = EXCL_BUILDING + b; return true; }
            if (forced == EXCL_UNIT && u != null) { entry = EXCL_UNIT + u; return true; }
            if (forced.Length == 0 && b != null && u == null) { entry = EXCL_BUILDING + b; return true; }
            if (forced.Length == 0 && u != null && b == null) { entry = EXCL_UNIT + u; return true; }
            if (forced.Length == 0 && b != null && u != null) { why = $"'{text}' is both a building and a unit — write {EXCL_BUILDING}{b} or {EXCL_UNIT}{u}."; return false; }
            var sugg = new List<string>();
            foreach (var x in buildings) if (x.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) sugg.Add(EXCL_BUILDING + x);
            foreach (var x in units) if (x.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 && sugg.Count < 10) sugg.Add(EXCL_UNIT + x);
            why = $"No production building or unit is called '{text}'." + (sugg.Count > 0 ? " Did you mean: " + string.Join(", ", sugg) + "?" : $" Buildings: {string.Join(", ", buildings)}.");
            return false;
        }

        /// <summary>Admin-page label of a stat kind.</summary>
        static string UpgStatAdminLabel(string kind)
        {
            switch (kind)
            {
                case "move":      return "Speed";
                case "jump":      return "Jump";
                case "turn":      return "Turning";
                case "damage":    return "Damage";
                case "firerate":  return "Rate of fire";
                case "reload":    return "Reload";
                case "accuracy":  return "Accuracy";
                case "magazine":  return "Magazine";
                case "projspeed": return "Projectile speed";
                default:          return kind;
            }
        }

        /// <summary>
        /// What the step does to the game value, with the step in force; for smaller-is-better stats the division is spelled
        /// out ("+25% rate of fire = time between shots ÷ 1.25 at Lv 1, ÷ 1.5 at Lv 2") so nobody has to invert it.
        /// </summary>
        static string UpgStatHint(string kind)
        {
            var inv = CultureInfo.InvariantCulture;
            float s = UpgStep(kind);
            string p = s.ToString("0.#", inv), f1 = (1f + s / 100f).ToString("0.##", inv), f2 = (1f + 2f * s / 100f).ToString("0.##", inv);
            switch (kind)
            {
                case "move":      return $"+{p}% per level of every speed: drive, walk, run, sprint, fly.";
                case "jump":      return $"+{p}% per level of soldier jump and creature pounce speed.";
                case "turn":      return $"+{p}% per level: turn rate x {f1} at Lv 1; wheeled turning circle ÷ {f1} at Lv 1, ÷ {f2} at Lv 2.";
                case "damage":    return $"+{p}% per level of every projectile damage value (impact, splash, penetration, ricochet).";
                case "firerate":  return $"+{p}% rate of fire = time between shots ÷ {f1} at Lv 1, ÷ {f2} at Lv 2.";
                case "reload":    return $"+{p}% faster reload = reload time ÷ {f1} at Lv 1, ÷ {f2} at Lv 2.";
                case "accuracy":  return $"+{p}% accuracy = muzzle spread ÷ {f1} at Lv 1, ÷ {f2} at Lv 2.";
                case "magazine":  return $"+{p}% per level of magazine size, rounded; infinite magazines stay infinite. The server keeps the vanilla size.";
                case "projspeed": return $"+{p}% per level of projectile speed; lifetime ÷ the same factor so the range stays. Not on instant-hit weapons.";
                default:          return "";
            }
        }

        /// <summary>Load-time range check: general step 0 < x <= 500 (else 25 / 500), per kind -1 or 0 < x <= 500 (else -1 / 500).</summary>
        static bool SanitizeUpgradeSteps()
        {
            bool changed = false;
            if (!ValidStep(Cfg.UpgradeStepPercent))
            {
                float fixedStep = Cfg.UpgradeStepPercent > UPG_STEP_MAX ? UPG_STEP_MAX : 25f;
                MelonLogger.Warning($"[Buy] Config: UpgradeStepPercent {Cfg.UpgradeStepPercent} out of range (0 < x <= {UPG_STEP_MAX}) — using {fixedStep}.");
                Cfg.UpgradeStepPercent = fixedStep; changed = true;
            }
            foreach (var k in UPG_KINDS)
            {
                float v = StepOverride(k);
                if (v == -1f || ValidStep(v)) continue;
                float fixedStep = v > UPG_STEP_MAX ? UPG_STEP_MAX : -1f;
                MelonLogger.Warning($"[Buy] Config: step for '{k}' = {v} out of range (-1 or 0 < x <= {UPG_STEP_MAX}) — using {fixedStep}.");
                SetStepOverride(k, fixedStep); changed = true;
            }
            return changed;
        }

        static string EnabledKinds()
        {
            var on = new List<string>();
            foreach (var k in UPG_KINDS) if (UpgStatEnabled(k)) on.Add(k);
            return string.Join(",", on);
        }

        /// <summary>
        /// 1.6.0 configs carried UpgradeWeaponMagazine = false as its default; 1.7.0 turns magazines on by default
        /// (asked for by name). Config version 2 marks configs that already went through this once.
        /// </summary>
        static bool MigrateUpgradeConfig()
        {
            if (Cfg.UpgradeConfigVersion >= 2) return false;
            Cfg.UpgradeWeaponMagazine = true;
            Cfg.UpgradeConfigVersion = 2;
            MelonLogger.Msg("[Buy] Config: upgrade settings migrated to version 2 (UpgradeWeaponMagazine = true).");
            return true;
        }

        // ===================================================================
        // Member table — the ONE place that says what an upgrade touches and in which
        // direction "better" goes, keyed by stat kind. Every entry was checked in the
        // 1.0.15 decompile (declared on the listed component, semantics from where the
        // game reads it).
        // ===================================================================

        sealed class UpgSpec
        {
            public string Name = "";
            public bool OnProjectile;        // false = prefab component field (baked), true = ProjectileData asset field (live)
            public string Kind = "";         // move / jump / turn / damage / firerate / reload / accuracy / magazine / projspeed
            public int Slot;                 // prefab members: 0 movement, 1 primary, 2 secondary; projectile members: the weapon that owns the asset
            public bool LargerIsBetter;      // true: x F, false: / F
            public float Min;                // lower clamp for smaller-is-better floats (0 = none)
            public bool Show;                // the member shown as the stat's value in the list
            public string[] Components = Array.Empty<string>();   // concrete component types allowed to declare it (prefab members)
        }

        static UpgSpec P(string name, string kind, int slot, bool larger, float min, bool show, params string[] comps)
            => new UpgSpec { Name = name, Kind = kind, Slot = slot, LargerIsBetter = larger, Min = min, Show = show, Components = comps };
        static UpgSpec J(string name, string kind, bool larger, bool show)
            => new UpgSpec { Name = name, OnProjectile = true, Kind = kind, Slot = -1, LargerIsBetter = larger, Show = show };

        const bool BIGGER = true, SMALLER = false, SHOW = true, HIDE = false;

        static readonly UpgSpec[] UPG_SPECS =
        {
            // --- move: exactly the members Si_UnitBalance scales for move_speed_mult (_speedFieldNames), m/s. ---
            P("MoveSpeed",    "move", UPG_MOVEMENT, BIGGER, 0f, SHOW, "VehicleWheeled", "VehicleHovered", "CreatureDecapod"),
            P("FlyMoveSpeed", "move", UPG_MOVEMENT, BIGGER, 0f, HIDE, "CreatureDecapod"),
            P("WalkSpeed",    "move", UPG_MOVEMENT, BIGGER, 0f, HIDE, "Soldier"),
            P("RunSpeed",     "move", UPG_MOVEMENT, BIGGER, 0f, SHOW, "Soldier"),
            P("SprintSpeed",  "move", UPG_MOVEMENT, BIGGER, 0f, HIDE, "Soldier"),
            P("ForwardSpeed", "move", UPG_MOVEMENT, BIGGER, 0f, SHOW, "VehicleAir"),
            P("StrafeSpeed",  "move", UPG_MOVEMENT, BIGGER, 0f, HIDE, "VehicleAir", "VehicleHovered"),
            P("TurboSpeed",   "move", UPG_MOVEMENT, BIGGER, 0f, HIDE, "VehicleAir", "VehicleHovered"),

            // --- jump: Soldier.JumpSpeed = take-off velocity, m/s (Soldier.cs 3022). CreatureDecapod.PounceSpeed = pounce
            //     velocity, m/s (CreatureDecapod.cs 2151: GetJumpDirection() * PounceSpeed; the jump-at cap is
            //     PounceSpeed * JumpAtSpeedMult, line 1023, so it scales along). ---
            P("JumpSpeed",   "jump", UPG_MOVEMENT, BIGGER, 0f, SHOW, "Soldier"),
            P("PounceSpeed", "jump", UPG_MOVEMENT, BIGGER, 0f, SHOW, "CreatureDecapod"),

            // --- turn: maximum yaw rate in deg/s (VehicleHovered.cs 1584, VehicleAir.cs 1008, CreatureDecapod.cs 3692).
            //     Wheeled vehicles steer by TurningCircleRadius in m, smaller = tighter (VehicleWheeled.cs 41).
            //     Soldier.TurnSpeed is left out: it is the body catching up with the view (Soldier.cs 3254), not handling. ---
            P("TurnSpeed",           "turn", UPG_MOVEMENT, BIGGER,  0f, SHOW, "VehicleHovered", "VehicleAir", "CreatureDecapod"),
            P("FlyTurnSpeed",        "turn", UPG_MOVEMENT, BIGGER,  0f, HIDE, "CreatureDecapod"),
            P("TurningCircleRadius", "turn", UPG_MOVEMENT, SMALLER, 0f, SHOW, "VehicleWheeled"),

            // --- Vehicle turret: the first VehicleTurret on the prefab (the only one OM reaches). ---
            // FireInterval = seconds between shots (VehicleTurret.cs 1490: m_fPrimaryFireDelay = PrimaryFireInterval).
            P("PrimaryFireInterval",   "firerate", UPG_PRIMARY,   SMALLER, 0.01f, SHOW, "VehicleTurret"),
            P("PrimaryReloadTime",     "reload",   UPG_PRIMARY,   SMALLER, 0.1f,  SHOW, "VehicleTurret"),
            // MuzzleSpread = cone half-angle the shot is randomised in, degrees (VehicleTurret.cs 1170).
            P("PrimaryMuzzleSpread",   "accuracy", UPG_PRIMARY,   SMALLER, 0f,    SHOW, "VehicleTurret"),
            // MagazineSize 0 = no reload / infinite (VehicleTurret.cs 469 PrimaryHasReload => size > 0): never scaled.
            P("PrimaryMagazineSize",   "magazine", UPG_PRIMARY,   BIGGER,  0f,    SHOW, "VehicleTurret"),
            P("SecondaryFireInterval", "firerate", UPG_SECONDARY, SMALLER, 0.01f, SHOW, "VehicleTurret"),
            P("SecondaryReloadTime",   "reload",   UPG_SECONDARY, SMALLER, 0.1f,  SHOW, "VehicleTurret"),
            P("SecondaryMuzzleSpread", "accuracy", UPG_SECONDARY, SMALLER, 0f,    SHOW, "VehicleTurret"),
            P("SecondaryMagazineSize", "magazine", UPG_SECONDARY, BIGGER,  0f,    SHOW, "VehicleTurret"),

            // --- Infantry gun (HumanHandsAnimator), the soldier's primary weapon. ---
            // FireDelay = seconds between rounds (HumanHandsAnimator.cs 807: CurrentFireDelay = FireDelay).
            P("FireDelay",       "firerate", UPG_PRIMARY, SMALLER, 0.01f, SHOW, "HumanHandsAnimator"),
            P("ReloadTime",      "reload",   UPG_PRIMARY, SMALLER, 0.1f,  SHOW, "HumanHandsAnimator"),
            // Spread in degrees between Min (rested) and Max (sustained fire) (HumanHandsAnimator.cs 292).
            P("MuzzleSpreadMin", "accuracy", UPG_PRIMARY, SMALLER, 0f,    SHOW, "HumanHandsAnimator"),
            P("MuzzleSpreadMax", "accuracy", UPG_PRIMARY, SMALLER, 0f,    HIDE, "HumanHandsAnimator"),
            P("MagazineSize",    "magazine", UPG_PRIMARY, BIGGER,  0f,    SHOW, "HumanHandsAnimator"),

            // --- ProjectileData (live, read per shot). The list shows the largest damage field of the weapon. ---
            J("m_fImpactDamage",      "damage",    BIGGER, SHOW),
            J("m_fRicochetDamage",    "damage",    BIGGER, SHOW),
            J("m_fPenetratingDamage", "damage",    BIGGER, SHOW),
            J("m_fSplashDamageMax",   "damage",    BIGGER, SHOW),
            J("m_fBaseSpeed",         "projspeed", BIGGER, SHOW),
            // Range compensation: MaxDistance = m_fBaseSpeed * m_fLifeTime (ProjectileData.cs 442), so lifetime / F keeps
            // the range when the speed goes x F. Skipped for m_InstantHit projectiles, where m_fBaseSpeed is the ray length.
            J("m_fLifeTime",          "projspeed", SMALLER, HIDE),
        };

        // ===================================================================
        // Per-unit-type profile (built from the server prefab, cached by ObjectInfo name)
        // ===================================================================

        sealed class UpgBinding
        {
            public UpgSpec Spec = null!;
            public string StatKey = "";
            public int Group;
            public string Target = "";            // "A:<asset>.asset"
            public string Owner = "";             // declaring component type, or the ProjectileData asset name
            public object Holder = null!;         // component on the server prefab, or the ProjectileData asset
            public MemberInfo Member = null!;
            public byte TypeByte;                 // OverrideManager.EOverrideDataType: 3 Int, 4 Float
            public bool Baked => !Spec.OnProjectile;
        }

        sealed class UpgProfile
        {
            public string InfoName = "", Display = "";
            public bool ClientPhysics;            // NetworkTransformComponent.m_NetClientSidePhysics
            public bool SwapOk;                   // ObjectInfo.Prefab is the networked prefab clients instantiate
            public bool InfoTargetOk;             // "A:<ObjectInfo>.asset" resolves on the client
            public readonly Dictionary<string, List<UpgBinding>> Stats = new Dictionary<string, List<UpgBinding>>(StringComparer.Ordinal);
            public readonly List<string>[] Projectiles = { new List<string>(), new List<string>(), new List<string>() };
            public readonly List<string> Notes = new List<string>();
            public List<UpgBinding> Of(string key) => Stats.TryGetValue(key, out var l) ? l : _noBindings;
            public IEnumerable<UpgBinding> All() { foreach (var l in Stats.Values) foreach (var b in l) yield return b; }
        }
        static readonly List<UpgBinding> _noBindings = new List<UpgBinding>();

        static readonly Dictionary<string, UpgProfile> _upgProfiles = new Dictionary<string, UpgProfile>(StringComparer.Ordinal);

        static UpgProfile? GetUpgProfile(ObjectInfo? info)
        {
            if (info == null || info.Prefab == null) return null;
            string key = info.name ?? "";
            if (key.Length == 0) return null;
            if (_upgProfiles.TryGetValue(key, out var p)) return p;
            p = BuildUpgProfile(info);
            _upgProfiles[key] = p;
            return p;
        }

        const BindingFlags OM_FLAGS = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;

        static void AddBinding(UpgProfile prof, UpgBinding b)
        {
            if (!prof.Stats.TryGetValue(b.StatKey, out var list)) { list = new List<UpgBinding>(); prof.Stats[b.StatKey] = list; }
            list.Add(b);
        }

        static UpgProfile BuildUpgProfile(ObjectInfo info)
        {
            var prof = new UpgProfile { InfoName = info.name ?? "", Display = info.DisplayName ?? info.name ?? "" };
            GameObject prefab = info.Prefab;
            try
            {
                var ntc = prefab.GetComponentInChildren<NetworkTransformComponent>(true);
                prof.ClientPhysics = ntc != null && ntc.m_NetClientSidePhysics;
                prof.SwapOk = GameDatabase.GetSpawnablePrefabIndex(prefab) >= 0 && prefab.GetComponent<Unit>() != null;
                var db = GameDatabase.Database;
                prof.InfoTargetOk = db != null && AssetTargetResolvable(prof.InfoName, db.AssetPaths_AllObjectData);
                if (!prof.SwapOk) prof.Notes.Add("ObjectInfo.Prefab is not in the networked prefab list: baked stats cannot be delivered");
                if (!prof.InfoTargetOk) prof.Notes.Add($"target A:{prof.InfoName}.asset does not resolve on clients: baked stats cannot be delivered");
                if (!prof.ClientPhysics) prof.Notes.Add("m_NetClientSidePhysics = false (server simulates movement): move / jump / turn not offered");

                var monos = prefab.GetComponentsInChildren<MonoBehaviour>(true);
                string target = "A:" + prof.InfoName + ".asset";
                foreach (var spec in UPG_SPECS)
                {
                    if (spec.OnProjectile) continue;
                    if (!ResolveOmMember(monos, spec.Name, out object? holder, out MemberInfo? mi, out string why))
                    {
                        if (why.Length > 0) prof.Notes.Add($"{spec.Name}: {why}");
                        continue;
                    }
                    string comp = mi!.DeclaringType?.Name ?? "?";
                    if (Array.IndexOf(spec.Components, comp) < 0)
                    {
                        prof.Notes.Add($"{spec.Name}: resolves to {comp}, not {string.Join("/", spec.Components)} — skipped");
                        continue;
                    }
                    if (!TryTypeByte(mi, out byte tb)) { prof.Notes.Add($"{spec.Name}: field type not float/int — skipped"); continue; }
                    // A weapon slot without a projectile fires nothing (VehicleTurret.FireProjectile returns, line 1199), so
                    // its handling members are not offered: the first turret of a hover tank has an empty secondary slot (its
                    // machine gun is a second turret), the bomber's bomb rack an empty primary (1.9.0).
                    if (holder is VehicleTurret slotTurret && spec.Slot != UPG_MOVEMENT
                        && (spec.Slot == UPG_PRIMARY ? slotTurret.PrimaryProjectileData : slotTurret.SecondaryProjectileData) == null)
                    {
                        prof.Notes.Add($"{spec.Name} ({comp} '{slotTurret.name}'): weapon slot has no projectile — skipped");
                        continue;
                    }
                    if (holder is HumanHandsAnimator slotHands && slotHands.BulletType == null)
                    {
                        prof.Notes.Add($"{spec.Name} ({comp}): no bullet type — skipped");
                        continue;
                    }
                    var b = new UpgBinding { Spec = spec, StatKey = StatKeyFor(spec.Kind, spec.Slot), Group = spec.Slot, Target = target, Owner = comp, Holder = holder!, Member = mi, TypeByte = tb };
                    if (!ReadMemberRaw(b, out float fv, out int iv) || (tb == 4 ? fv == 0f : iv <= 0))
                    {
                        prof.Notes.Add($"{spec.Name} ({comp}): base value 0 — skipped");
                        continue;
                    }
                    AddBinding(prof, b);
                }

                // Projectile assets per weapon. Assets are shared ScriptableObjects, so any weapon's projectile is
                // reachable, but weapons follow the members above: first VehicleTurret, first HumanHandsAnimator,
                // first CreatureDecapod (its ranged attacks; melee has no projectile and stays unreachable).
                var assets = new List<KeyValuePair<int, ProjectileData>>();
                var vt = prefab.GetComponentInChildren<VehicleTurret>(true);
                if (vt != null)
                {
                    if (vt.PrimaryProjectileData != null) assets.Add(new KeyValuePair<int, ProjectileData>(UPG_PRIMARY, vt.PrimaryProjectileData));
                    if (vt.SecondaryProjectileData != null) assets.Add(new KeyValuePair<int, ProjectileData>(UPG_SECONDARY, vt.SecondaryProjectileData));
                }
                var hands = prefab.GetComponentInChildren<HumanHandsAnimator>(true);
                if (hands != null && hands.BulletType != null) assets.Add(new KeyValuePair<int, ProjectileData>(UPG_PRIMARY, hands.BulletType));
                var deca = prefab.GetComponentInChildren<CreatureDecapod>(true);
                if (deca != null)
                {
                    if (deca.AttackPrimary != null && deca.AttackPrimary.AttackProjectileData != null)
                        assets.Add(new KeyValuePair<int, ProjectileData>(UPG_PRIMARY, deca.AttackPrimary.AttackProjectileData));
                    if (deca.AttackSecondary != null && deca.AttackSecondary.AttackProjectileData != null)
                        assets.Add(new KeyValuePair<int, ProjectileData>(UPG_SECONDARY, deca.AttackSecondary.AttackProjectileData));
                }
                foreach (var kv in assets)
                {
                    int weapon = kv.Key;
                    var pd = kv.Value;
                    string pdName = pd.name ?? "";
                    if (prof.Projectiles[weapon].Contains(pdName)) continue;
                    prof.Projectiles[weapon].Add(pdName);
                    if (db == null || !AssetTargetResolvable(pdName, db.AssetPaths_AllProjectileData))
                    {
                        prof.Notes.Add($"projectile {pdName}: target does not resolve on clients — damage / speed not offered");
                        continue;
                    }
                    string pdTarget = "A:" + pdName + ".asset";
                    foreach (var spec in UPG_SPECS)
                    {
                        if (!spec.OnProjectile) continue;
                        if (spec.Kind == "projspeed" && pd.m_InstantHit) continue;   // speed is the ray length there
                        var mi = typeof(ProjectileData).GetMember(spec.Name, OM_FLAGS);
                        if (mi.Length == 0 || !OmMemberValid(mi[0]) || !TryTypeByte(mi[0], out byte tb)) continue;
                        var b = new UpgBinding { Spec = spec, StatKey = StatKeyFor(spec.Kind, weapon), Group = weapon, Target = pdTarget, Owner = pdName, Holder = pd, Member = mi[0], TypeByte = tb };
                        if (!ReadMemberRaw(b, out float fv, out int iv) || (tb == 4 ? fv == 0f : iv <= 0)) continue;
                        AddBinding(prof, b);
                    }
                }
            }
            catch (Exception ex)
            {
                prof.Notes.Add("profile build threw: " + ex.Message);
                MelonLogger.Warning($"[Buy] Upgrade profile for {prof.InfoName} failed: {ex.Message}");
            }
            return prof;
        }

        /// <summary>
        /// The client's OverrideManager member lookup for an "A:&lt;ObjectInfo&gt;.asset" target, on the server
        /// prefab: ObjectInfo itself first, then the first MonoBehaviour whose concrete type declares the member.
        /// Members declared on ObjectInfo are not upgraded (no pool drop, not instance-copied) and are reported.
        /// </summary>
        static bool ResolveOmMember(MonoBehaviour[] monos, string name, out object? holder, out MemberInfo? mi, out string why)
        {
            holder = null; mi = null; why = "";
            if (typeof(ObjectInfo).GetMember(name, OM_FLAGS).Length > 0) { why = "declared on ObjectInfo — skipped"; return false; }
            foreach (var mb in monos)
            {
                if (mb == null) continue;
                var found = mb.GetType().GetMember(name, OM_FLAGS);
                if (found.Length == 0) continue;
                if (!OmMemberValid(found[0])) { why = $"{mb.GetType().Name}.{name} is not overridable — skipped"; return false; }
                holder = mb; mi = found[0];
                return true;
            }
            return false;   // not on this prefab: silently not offered
        }

        // OverrideManager.IsMemberValid (OverrideManager.cs 898-925).
        static bool OmMemberValid(MemberInfo m)
        {
            if (m.Name.ToLowerInvariant() == "guid") return false;
            if (m is FieldInfo f) { if (f.IsInitOnly || f.IsLiteral || f.IsNotSerialized) return false; }
            else if (m is PropertyInfo p) { if (!p.CanWrite || p.SetMethod == null || !p.SetMethod.IsPublic) return false; }
            else return false;
            foreach (var a in m.GetCustomAttributes(true))
            {
                string n = a.GetType().Name;
                if (n == "NonSerializedAttribute" || n == "ShowOnlyAttribute") return false;
            }
            return true;
        }

        static bool TryTypeByte(MemberInfo m, out byte tb)
        {
            Type? t = (m as FieldInfo)?.FieldType ?? (m as PropertyInfo)?.PropertyType;
            tb = t == typeof(float) ? (byte)4 : t == typeof(int) ? (byte)3 : (byte)0;   // EOverrideDataType.Float / Int
            return tb != 0;
        }

        // OverrideManager.AssetSourceRegistry.GetSource: the asset list is picked by the text before the
        // first '_' of the name, then any path ending in "<name>.asset" matches. A name without '_' would
        // make the client's Slice throw, so it never resolves here.
        static bool AssetTargetResolvable(string assetName, List<string>? paths)
        {
            if (string.IsNullOrEmpty(assetName) || paths == null || paths.Count == 0) return false;
            string want = assetName + ".asset";
            string? prefix = AssetPrefix(want), listPrefix = AssetPrefix(paths[0] ?? "");
            if (prefix == null || listPrefix == null || prefix != listPrefix) return false;
            foreach (var p in paths)
                if (p != null && p.EndsWith(want, StringComparison.Ordinal)) return true;
            return false;
        }

        static string? AssetPrefix(string path)
        {
            int slash = path.LastIndexOf('/');
            string seg = slash >= 0 ? path.Substring(slash + 1) : path;
            int us = seg.IndexOf('_');
            return us > 0 ? seg.Substring(0, us) : null;
        }

        static bool ReadMemberRaw(UpgBinding b, out float f, out int i)
        {
            f = 0f; i = 0;
            try
            {
                object? v = b.Member is FieldInfo fi ? fi.GetValue(b.Holder) : ((PropertyInfo)b.Member).GetValue(b.Holder);
                if (v is float fv) { f = fv; return true; }
                if (v is int iv) { i = iv; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Base value = the global OverrideManager value if an enabled entry exists, else the server
        /// prefab / asset field (which already holds any global override, OM writes the field).
        /// </summary>
        static bool TryGetBase(UpgBinding b, out float f, out int i, out bool global)
        {
            global = false;
            try
            {
                var all = OverrideManager.GetOverrides();
                if (all != null && all.TryGetValue(b.Target, out var members) && members.TryGetValue(b.Spec.Name, out var mo) && mo != null && mo.Enabled)
                {
                    if (mo.CurrentValue is float gf) { f = gf; i = 0; global = true; return true; }
                    if (mo.CurrentValue is int gi) { i = gi; f = 0f; global = true; return true; }
                }
            }
            catch { }
            return ReadMemberRaw(b, out f, out i);
        }

        // ===================================================================
        // Per-player OverrideManager packets
        // ===================================================================

        struct UpgWrite
        {
            public string Target, Member;
            public byte Type;     // 0 = revert, 3 = Int, 4 = Float
            public float F;
            public int I;
        }

        static bool BoostWrite(UpgBinding b, float factor, out UpgWrite w)
        {
            w = default;
            if (!TryGetBase(b, out float f, out int i, out _)) return false;
            w.Target = b.Target; w.Member = b.Spec.Name; w.Type = b.TypeByte;
            if (b.TypeByte == 4)
            {
                if (f == 0f) return false;
                float v = b.Spec.LargerIsBetter ? f * factor : f / factor;
                if (!b.Spec.LargerIsBetter && b.Spec.Min > 0f && f > b.Spec.Min) v = Mathf.Max(b.Spec.Min, v);
                w.F = v;
            }
            else
            {
                if (i <= 0) return false;   // magazine 0 = infinite, never scaled
                w.I = b.Spec.LargerIsBetter ? Math.Max(1, (int)Math.Round(i * factor)) : Math.Max(1, (int)Math.Round(i / factor));
            }
            return true;
        }

        /// <summary>The value a binding has at the given factor (1 = base), as a float for display.</summary>
        static bool BoostedValue(UpgBinding b, float factor, out float v)
        {
            v = 0f;
            if (factor <= 1.0001f)
            {
                if (!TryGetBase(b, out float f, out int i, out _)) return false;
                v = b.TypeByte == 4 ? f : i;
                return true;
            }
            if (!BoostWrite(b, factor, out var w)) return false;
            v = w.Type == 4 ? w.F : w.I;
            return true;
        }

        // What the buyer's client must go back to: the global value when one is in force, else its own original (vanilla).
        static UpgWrite RevertWrite(string target, string member, byte typeByte)
        {
            var w = new UpgWrite { Target = target, Member = member, Type = 0 };
            try
            {
                var all = OverrideManager.GetOverrides();
                if (all != null && all.TryGetValue(target, out var members) && members.TryGetValue(member, out var mo) && mo != null && mo.Enabled)
                {
                    if (typeByte == 4 && mo.CurrentValue is float gf) { w.Type = 4; w.F = gf; }
                    else if (typeByte == 3 && mo.CurrentValue is int gi) { w.Type = 3; w.I = gi; }
                }
            }
            catch { }
            return w;
        }

        /// <summary>One Overrides packet per target (keeps every packet small), sent to one player only.</summary>
        static void SendUpgWrites(Player player, List<UpgWrite> writes)
        {
            if (player == null || writes.Count == 0) return;
            var byTarget = new Dictionary<string, List<UpgWrite>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var w in writes)
            {
                if (string.IsNullOrEmpty(w.Target) || w.Target == "r" || string.IsNullOrEmpty(w.Member)) continue;   // "r" = client revert-all
                if (!byTarget.TryGetValue(w.Target, out var list)) { list = new List<UpgWrite>(); byTarget[w.Target] = list; order.Add(w.Target); }
                list.Add(w);
            }
            foreach (string target in order)
            {
                var list = byTarget[target];
                // Shared singleton writer: built and sent without anything else writing in between.
                var pw = GameByteStreamWriter.GetGameByteStreamWriter(0u, "Si_Buy::PlayerOverride");
                pw.WriteByte((byte)ENetworkPacketType.Overrides);
                pw.WriteUInt16(1);
                pw.WriteString(target);
                pw.WriteUInt16((ushort)list.Count);
                foreach (var w in list)
                {
                    pw.WriteString(w.Member);
                    pw.WriteByte(w.Type);
                    if (w.Type == 4) pw.WriteFloat(w.F);
                    else if (w.Type == 3) pw.WriteInt32(w.I);
                }
                NetworkLayer.SendPacketToPlayer(pw, ENetworkPacketSend.Reliable, player);
            }
        }

        // ===================================================================
        // State
        // ===================================================================

        sealed class UpgRecord
        {
            public long Buyer;
            public string BuyerName = "";
            public readonly Dictionary<string, int> Levels = new Dictionary<string, int>(StringComparer.Ordinal);   // stat key -> level
            // Step per kind when last bought (what the unit carries): a later admin change reaches the unit only at its next purchase.
            public Dictionary<string, float> Steps = new Dictionary<string, float>(StringComparer.Ordinal);
            public int Total { get { int n = 0; foreach (var v in Levels.Values) n += v; return n; } }
            public int Level(string key) => Levels.TryGetValue(key, out int l) ? l : 0;
            public float StepOf(string kind) => Steps.TryGetValue(kind, out float s) ? s : UpgStep(kind);
            // the Movement bundle has one level but a factor per kind (speed, jump, turning)
            public float Factor(string key, string kind) => 1f + StepOf(kind) / 100f * Level(key);
        }

        sealed class RefEq<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T? a, T? b) => ReferenceEquals(a, b);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }

        // Unity's == on destroyed objects would make two dead keys "equal", hence reference identity.
        static readonly Dictionary<Unit, UpgRecord> _upgRecords = new Dictionary<Unit, UpgRecord>(new RefEq<Unit>());

        sealed class HeldValue { public string Target = "", Member = ""; public byte Type; public float F; public int I; }
        // Live values the buyer's client currently holds: steamId -> "target|member" -> value.
        static readonly Dictionary<long, Dictionary<string, HeldValue>> _upgHeld = new Dictionary<long, Dictionary<string, HeldValue>>();
        static readonly HashSet<long> _upgResync = new HashSet<long>();
        static bool _upgSwapping;
        static float _upgPruneTimer;

        static UpgRecord? RecordFor(Unit? unit)
            => unit != null && _upgRecords.TryGetValue(unit, out var r) ? r : null;

        // ===================================================================
        // Public API (anti-cheat reads this by reflection)
        // ===================================================================

        /// <summary>
        /// Improvement factor F (>= 1) of one stat currently in force on <paramref name="unit"/>: only while
        /// the buyer of its upgrades controls it, else 1. stat: move, jump, turn, damage, firerate, reload,
        /// accuracy, magazine, projspeed. For weapon stats the larger of primary and secondary. Never throws.
        /// </summary>
        public static float GetUpgradeFactor(Unit unit, string stat)
        {
            try
            {
                if (unit == null || string.IsNullOrEmpty(stat)) return 1f;
                if (!_upgRecords.TryGetValue(unit, out var rec)) return 1f;
                if (IsLiveStat(stat) && !Cfg.UpgradesEnabled) return 1f;   // live values are reverted while switched off
                var by = unit.ControlledBy;
                if (by == null || GetPlayerSteamId(by) != rec.Buyer) return 1f;
                float best = 1f;
                var prof = GetUpgProfile(unit.ObjectInfo);
                if (prof == null || ExcludedBy(prof, stat) != null) return 1f;   // excluded for this unit type: reported as not in force
                if (stat == "move" || stat == "jump" || stat == "turn")
                {
                    // the Movement bundle: in force for a kind only when the unit type has members of that kind
                    if (rec.Level("move") <= 0) return 1f;
                    foreach (var b in prof.Of("move")) if (b.Spec.Kind == stat) return rec.Factor("move", stat);
                    return 1f;
                }
                foreach (var kv in rec.Levels)
                {
                    if (kv.Value <= 0) continue;
                    var s = StatByKey(kv.Key);
                    if (s != null && s.Kind == stat) best = Mathf.Max(best, rec.Factor(kv.Key, s.Kind));
                }
                return best;
            }
            catch { return 1f; }
        }

        // ===================================================================
        // Live (projectile) values: diff what the buyer's client should hold against what it holds
        // ===================================================================

        static Dictionary<string, HeldValue> DesiredLive(Player player)
        {
            var want = new Dictionary<string, HeldValue>(StringComparer.Ordinal);
            if (!Cfg.UpgradesEnabled) return want;
            var unit = player.ControlledUnit;
            var rec = RecordFor(unit);
            if (unit == null || rec == null || rec.Buyer != GetPlayerSteamId(player) || unit.IsDestroyed) return want;
            var prof = GetUpgProfile(unit.ObjectInfo);
            if (prof == null) return want;
            var level = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in rec.Levels)
            {
                if (kv.Value <= 0) continue;
                foreach (var b in prof.Of(kv.Key))
                {
                    if (b.Baked || ExcludedBy(prof, b.Spec.Kind) != null) continue;   // an excluded live stat is reverted at once
                    string key = b.Target + "|" + b.Spec.Name;
                    if (level.TryGetValue(key, out int have) && have >= kv.Value) continue;   // shared asset: higher level wins
                    if (!BoostWrite(b, rec.Factor(kv.Key, b.Spec.Kind), out var w)) continue;   // shared asset = same kind, same step
                    level[key] = kv.Value;
                    want[key] = new HeldValue { Target = w.Target, Member = w.Member, Type = w.Type, F = w.F, I = w.I };
                }
            }
            return want;
        }

        static void SyncLive(Player? player, bool force)
        {
            if (player == null) return;
            try
            {
                long sid = GetPlayerSteamId(player);
                _upgHeld.TryGetValue(sid, out var held);
                var want = DesiredLive(player);
                if ((held == null || held.Count == 0) && want.Count == 0) return;
                var writes = new List<UpgWrite>();
                foreach (var kv in want)
                {
                    var v = kv.Value;
                    if (!force && held != null && held.TryGetValue(kv.Key, out var h) && h.Type == v.Type && h.F == v.F && h.I == v.I) continue;
                    writes.Add(new UpgWrite { Target = v.Target, Member = v.Member, Type = v.Type, F = v.F, I = v.I });
                }
                if (held != null)
                    foreach (var kv in held)
                        if (!want.ContainsKey(kv.Key))
                            writes.Add(RevertWrite(kv.Value.Target, kv.Value.Member, kv.Value.Type));
                SendUpgWrites(player, writes);
                if (want.Count > 0) _upgHeld[sid] = want; else _upgHeld.Remove(sid);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] Upgrade live sync for {player.PlayerName} threw: {ex.Message}");
            }
        }

        static Player? PlayerBySteamId(long sid)
        {
            try
            {
                foreach (var p in Player.Players)
                    if (p != null && GetPlayerSteamId(p) == sid) return p;
            }
            catch { }
            return null;
        }

        /// <summary>Every client that holds live values goes back to the global values.</summary>
        static void RevertHeldLive()
        {
            foreach (var kv in new List<KeyValuePair<long, Dictionary<string, HeldValue>>>(_upgHeld))
            {
                var p = PlayerBySteamId(kv.Key);
                if (p == null) continue;
                var writes = new List<UpgWrite>();
                foreach (var h in kv.Value.Values) writes.Add(RevertWrite(h.Target, h.Member, h.Type));
                try { SendUpgWrites(p, writes); } catch (Exception ex) { MelonLogger.Warning($"[Buy] Upgrade revert for {p.PlayerName} threw: {ex.Message}"); }
            }
            _upgHeld.Clear();
            _upgResync.Clear();
        }

        /// <summary>Round start / end and map change: live values reverted (when the clients are still there), all records go.</summary>
        static void ResetUpgrades(bool sendReverts)
        {
            if (sendReverts) RevertHeldLive();
            _upgRecords.Clear();
            _upgHeld.Clear();
            _upgResync.Clear();
            _upgProfiles.Clear();
        }

        /// <summary>
        /// /buy upgrades on|off. Off: held damage boosts are reverted (and not re-sent while off); records stay so
        /// GetUpgradeFactor keeps reporting the handling stats refitted units still carry. On: holders re-sync.
        /// </summary>
        static void OnUpgradesSwitched()
        {
            if (!Cfg.UpgradesEnabled) { RevertHeldLive(); return; }
            foreach (var rec in _upgRecords.Values) _upgResync.Add(rec.Buyer);
        }

        static bool IsLiveStat(string kind) => kind == "damage" || kind == "projspeed";

        // ===================================================================
        // Game events
        // ===================================================================

        static bool _upgHooked;
        static void HookUpgrades()
        {
            if (_upgHooked) return;
            try
            {
                GameEvents.OnPlayerChangedUnit -= OnPlayerChangedUnit_Upg;
                GameEvents.OnPlayerChangedUnit += OnPlayerChangedUnit_Upg;
                GameEvents.OnUnitDestroyed -= OnUnitGone_Upg;
                GameEvents.OnUnitDestroyed += OnUnitGone_Upg;
                GameEvents.OnUnitDeleted -= OnUnitDeleted_Upg;
                GameEvents.OnUnitDeleted += OnUnitDeleted_Upg;
                GameEvents.OnPlayerLeft -= OnPlayerLeft_Upg;
                GameEvents.OnPlayerLeft += OnPlayerLeft_Upg;
                _upgHooked = true;
            }
            catch (Exception ex) { MelonLogger.Warning($"[Buy] HookUpgrades failed: {ex.Message}"); }
        }
        // GameEvents.Clear() runs on every level load — re-hook from OnSceneWasLoaded.
        static void ReHookUpgrades() { _upgHooked = false; HookUpgrades(); }

        static void OnPlayerChangedUnit_Upg(Player player, Unit oldUnit, Unit newUnit)
        {
            if (_upgSwapping || player == null) return;   // the swap syncs explicitly once it is done
            try
            {
                long sid = GetPlayerSteamId(player);
                if (!_upgHeld.ContainsKey(sid) && RecordFor(newUnit) == null) return;
                SyncLive(player, false);
            }
            catch (Exception ex) { MelonLogger.Warning($"[Buy] Upgrade unit-change handler threw: {ex.Message}"); }
        }

        static void OnUnitGone_Upg(Unit unit, GameObject instigator) => DropUpgradeRecord(unit);
        static void OnUnitDeleted_Upg(Unit unit) => DropUpgradeRecord(unit);

        static void DropUpgradeRecord(Unit unit)
        {
            try
            {
                if (unit == null || !_upgRecords.TryGetValue(unit, out var rec)) return;
                _upgRecords.Remove(unit);
                if (_upgHeld.ContainsKey(rec.Buyer)) SyncLive(PlayerBySteamId(rec.Buyer), false);   // dropped record -> reverts what was held
            }
            catch (Exception ex) { MelonLogger.Warning($"[Buy] Upgrade record drop threw: {ex.Message}"); }
        }

        static void OnPlayerLeft_Upg(Player player)
        {
            try
            {
                if (player == null) return;
                long sid = GetPlayerSteamId(player);
                // Their client is gone; a reconnect builds every unit from the vanilla prefab again.
                var gone = new List<Unit>();
                foreach (var kv in _upgRecords) if (kv.Value.Buyer == sid) gone.Add(kv.Key);
                foreach (var u in gone) _upgRecords.Remove(u);
                _upgHeld.Remove(sid);
                _upgResync.Remove(sid);
            }
            catch (Exception ex) { MelonLogger.Warning($"[Buy] Upgrade player-left handler threw: {ex.Message}"); }
        }

        /// <summary>Called every frame from OnUpdate: deferred re-sends after a full override sync, and pruning.</summary>
        static void UpgradesTick()
        {
            if (_upgResync.Count > 0)
            {
                var ids = new List<long>(_upgResync);
                _upgResync.Clear();
                foreach (long sid in ids) SyncLive(PlayerBySteamId(sid), true);
            }
            if (_upgRecords.Count == 0) return;
            _upgPruneTimer -= Time.deltaTime;
            if (_upgPruneTimer > 0f) return;
            _upgPruneTimer = 5f;
            List<Unit>? dead = null;
            foreach (var kv in _upgRecords)
                if (kv.Key == null) (dead ??= new List<Unit>()).Add(kv.Key!);   // Unity null: destroyed without an event we saw
            if (dead != null) foreach (var u in dead) _upgRecords.Remove(u);
        }

        // ----- Harmony: a full override sync to a player (or a global change / revert-all broadcast)
        // overwrites what the buyer's client holds, so re-send it on the next frame. Si_UnitBalance
        // prefixes SendPlayerOverrides and may skip the original or call it per chunk: postfixes run
        // either way, and the HashSet collapses repeats into one re-send.

        static void PatchUpgradeSync(HarmonyLib.Harmony harmony)
        {
            try
            {
                var full = AccessTools.Method(typeof(NetworkLayer), nameof(NetworkLayer.SendPlayerOverrides), new[] { typeof(Player) });
                var one = AccessTools.Method(typeof(NetworkLayer), nameof(NetworkLayer.SendOverride));
                if (full != null) harmony.Patch(full, postfix: new HarmonyMethod(typeof(Buy), nameof(Upg_SendPlayerOverrides_Postfix)));
                if (one != null) harmony.Patch(one, postfix: new HarmonyMethod(typeof(Buy), nameof(Upg_SendOverride_Postfix)));
                if (full == null || one == null)
                    MelonLogger.Warning("[Buy] Upgrades: NetworkLayer.SendPlayerOverrides/SendOverride not found — held damage boosts are not re-sent after a full override sync.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] Upgrades: override-sync patch failed ({ex.Message}) — held damage boosts are not re-sent after a full override sync.");
            }
        }

        static void Upg_SendPlayerOverrides_Postfix(Player __0)
        {
            try
            {
                if (__0 == null || _upgHeld.Count == 0) return;
                long sid = GetPlayerSteamId(__0);
                if (_upgHeld.ContainsKey(sid)) _upgResync.Add(sid);
            }
            catch { }
        }

        static void Upg_SendOverride_Postfix(string __0, bool __3)
        {
            try
            {
                if (_upgHeld.Count == 0) return;
                foreach (var kv in _upgHeld)
                {
                    if (__3) { _upgResync.Add(kv.Key); continue; }   // revert-all broadcast
                    foreach (var h in kv.Value.Values)
                        if (h.Target == __0) { _upgResync.Add(kv.Key); break; }
                }
            }
            catch { }
        }

        // ===================================================================
        // What a player can buy right now (shared by chat, the /N menu and the ModUI panel)
        // ===================================================================

        sealed class UpgOffer
        {
            public UpgStat Stat = null!;
            public List<UpgBinding> Bindings = null!;   // usable now
            public int Level;
            public string Detail = "";            // "600 → 750 rpm" (or the current value alone once maxed)
            public string Step = "";              // "+25%" per level (Movement: per part when they differ)
            public bool Maxed, Affordable;
        }

        sealed class UpgView
        {
            public Unit? Unit;
            public string UnitName = "", Refusal = "";
            public int Used, Price, Credits;
            public UpgRecord? Record;   // this player's record on the unit, or null
            public readonly List<UpgOffer> Offers = new List<UpgOffer>();
        }

        static int UpgradePrice(ObjectInfo? info)
        {
            int raw = TryGetEffectiveCost(info, out int cost, out _) ? (int)Math.Round(cost * Cfg.UpgradeCostFraction) : 0;
            return Math.Max(Cfg.UpgradeMinCost, raw);
        }

        /// <summary>The bindings of one stat a purchase would deliver right now (stat switch, delivery limits).</summary>
        static List<UpgBinding> UsableBindings(UpgProfile prof, UpgStat stat)
        {
            var list = new List<UpgBinding>();
            foreach (var b in prof.Of(stat.Key))
            {
                if (!UpgStatEnabled(b.Spec.Kind)) continue;   // per kind: move / jump / turn inside the bundle, one per weapon stat
                if (ExcludedBy(prof, b.Spec.Kind) != null) continue;   // not offered for this unit type (UpgradeExclusions)
                if (b.Baked && (!prof.SwapOk || !prof.InfoTargetOk)) continue;
                if (b.Baked && b.Group == UPG_MOVEMENT && !prof.ClientPhysics) continue;
                list.Add(b);
            }
            return list;
        }

        /// <summary>The binding whose value stands for the stat: the largest damage field, else the first Show member.</summary>
        static UpgBinding? DisplayBinding(List<UpgBinding> bindings)
        {
            UpgBinding? best = null;
            float bestV = float.MinValue;
            foreach (var b in bindings)
            {
                if (!b.Spec.Show) continue;
                if (b.Spec.Kind != "damage") return b;
                if (BoostedValue(b, 1f, out float v) && Mathf.Abs(v) > bestV) { bestV = Mathf.Abs(v); best = b; }
            }
            return best ?? (bindings.Count > 0 ? bindings[0] : null);
        }

        /// <summary>
        /// The values a list row shows: for a weapon stat its one display member; for the Movement bundle the main speed
        /// and, where the unit has one, the jump (turning is in the bundle but not printed, to keep the row short).
        /// "4.0 m/s" with toFactor null, "4.0 → 5.0 m/s" otherwise. Factors are per kind (each kind has its own step).
        /// </summary>
        static string DescribeValues(List<UpgBinding> bindings, Func<string, float> fromFactorOf, Func<string, float>? toFactorOf)
        {
            var shown = new List<KeyValuePair<string, UpgBinding>>();
            bool movement = bindings.Count > 0 && bindings[0].Group == UPG_MOVEMENT;
            if (movement)
            {
                foreach (string kind in new[] { "move", "jump" })
                    foreach (var b in bindings)
                        if (b.Spec.Kind == kind && b.Spec.Show) { shown.Add(new KeyValuePair<string, UpgBinding>(kind == "move" ? "speed " : "jump ", b)); break; }
                if (shown.Count == 0)
                    foreach (var b in bindings)
                        if (b.Spec.Show) { shown.Add(new KeyValuePair<string, UpgBinding>(b.Spec.Kind == "turn" ? "turning " : "", b)); break; }
            }
            else
            {
                var b = DisplayBinding(bindings);
                if (b != null) shown.Add(new KeyValuePair<string, UpgBinding>("", b));
            }
            var parts = new List<string>();
            foreach (var kv in shown)
            {
                string kind = kv.Value.Spec.Kind;
                if (!BoostedValue(kv.Value, fromFactorOf(kind), out float v0)) continue;
                string text = kv.Key + FormatStat(kv.Value, v0, v0);
                if (toFactorOf != null && BoostedValue(kv.Value, toFactorOf(kind), out float v1))
                {
                    string first = FormatStat(kv.Value, v0, v0), suffix = UnitSuffix(first);
                    text = kv.Key + (suffix.Length > 0 && first.EndsWith(suffix, StringComparison.Ordinal) ? first.Substring(0, first.Length - suffix.Length) : first)
                         + " → " + FormatStat(kv.Value, v1, v0);
                }
                parts.Add(text);
            }
            // turning is part of the bundle; named (with its gain) so the row says everything a level buys
            if (movement && !shown.Exists(kv => kv.Value.Spec.Kind == "turn") && bindings.Exists(b => b.Spec.Kind == "turn"))
                parts.Add(toFactorOf != null ? "turning +" + ((toFactorOf("turn") / fromFactorOf("turn") - 1f) * 100f).ToString("0", CultureInfo.InvariantCulture) + "%" : "turning");
            return string.Join(", ", parts);
        }

        /// <summary>
        /// The step a level of this upgrade adds, per row: "+25%" for a weapon stat; for Movement "+25%" when its kinds
        /// (present on the unit and switched on) agree, else "speed +25%, jump +40%".
        /// </summary>
        static string StepText(UpgStat stat, List<UpgBinding> usable, Func<string, float> stepOf)
        {
            if (stat.Group != UPG_MOVEMENT) return Pct(stepOf(stat.Kind));
            var kinds = new List<string>();
            foreach (var b in usable) if (!kinds.Contains(b.Spec.Kind)) kinds.Add(b.Spec.Kind);
            if (kinds.Count == 0) return Pct(stepOf("move"));
            bool same = true;
            foreach (var k in kinds) if (Math.Abs(stepOf(k) - stepOf(kinds[0])) > 0.05f) same = false;
            if (same) return Pct(stepOf(kinds[0]));
            var parts = new List<string>();
            foreach (var k in kinds) parts.Add((k == "move" ? "speed" : k == "jump" ? "jump" : "turning") + " " + Pct(stepOf(k)));
            return string.Join(", ", parts);
        }

        // The unit suffix FormatStat appends (" m/s", " rpm", ...), so "4.0 → 5.0 m/s" carries it once.
        static string UnitSuffix(string formatted)
        {
            int space = formatted.IndexOf(' ');
            return space > 0 ? formatted.Substring(space) : "";
        }

        static string FormatStat(UpgBinding b, float v, float reference)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (b.Spec.Kind)
            {
                case "move": case "jump": case "projspeed":
                    return v.ToString("0.#", inv) + " m/s";
                case "turn":
                    return b.Spec.Name == "TurningCircleRadius" ? v.ToString("0.#", inv) + " m radius" : v.ToString("0", inv) + "°/s";
                case "damage":
                    return v.ToString(Mathf.Abs(v) >= 10f ? "0" : "0.#", inv) + " dmg";
                case "firerate":
                    // the unit follows the current interval, so "now" and "next" read alike
                    return reference < 1f ? (60f / Mathf.Max(0.001f, v)).ToString("0", inv) + " rpm" : v.ToString("0.0#", inv) + " s/shot";
                case "reload":
                    return v.ToString("0.0#", inv) + " s";
                case "accuracy":
                    return v.ToString("0.0#", inv) + "° spread";
                case "magazine":
                    return ((int)Math.Round(v)).ToString(inv) + " rds";
                default:
                    return v.ToString("0.##", inv);
            }
        }

        /// <summary>Everything the list and the panel show for this player and the unit they control.</summary>
        static UpgView BuildUpgView(Player player)
        {
            var view = new UpgView();
            long sid = GetPlayerSteamId(player);
            view.Credits = GetCredits(sid);
            if (!UpgCallerUnit(player, out var unit, out string why)) { view.Refusal = why; return view; }
            view.Unit = unit;
            var info = unit.ObjectInfo;
            view.UnitName = info.DisplayName ?? info.name;
            if (info.Critical) { view.Refusal = view.UnitName + " is a critical unit and cannot be upgraded."; return view; }
            if (unit.InCompartment != null) { view.Refusal = "Leave the seat first: upgrades apply to the unit you drive or are."; return view; }
            var prof = GetUpgProfile(info);
            if (prof == null) { view.Refusal = view.UnitName + " has nothing to upgrade."; return view; }
            var rec = RecordFor(unit);
            if (rec != null && rec.Buyer != sid) rec = null;   // someone else's upgrades are not yours
            view.Record = rec;
            view.Used = rec?.Total ?? 0;
            view.Price = UpgradePrice(info);
            bool affordable = view.Credits >= view.Price || TryFund(sid, view.Price - view.Credits, false);
            foreach (var stat in UPG_STATS)
            {
                var usable = UsableBindings(prof, stat);
                if (usable.Count == 0) continue;
                var offer = new UpgOffer { Stat = stat, Bindings = usable, Level = rec?.Level(stat.Key) ?? 0 };
                offer.Maxed = view.Used >= Cfg.UpgradeMaxPerUnit;
                offer.Affordable = affordable;
                // what the player has now (base with their level at the steps the unit carries) and what one more level
                // gives (the next purchase applies the steps configured now)
                int lvl = offer.Level;
                Func<string, float> nowF = k => rec != null ? rec.Factor(stat.Key, k) : 1f;
                Func<string, float> nextF = k => 1f + UpgStep(k) / 100f * (lvl + 1);
                offer.Detail = DescribeValues(usable, nowF, offer.Maxed ? null : nextF);
                offer.Step = StepText(stat, usable, UpgStep);
                view.Offers.Add(offer);
            }
            if (view.Offers.Count == 0) view.Refusal = view.UnitName + " has nothing to upgrade on this server.";
            return view;
        }

        static string OfferValue(UpgOffer o) => o.Detail;

        // ===================================================================
        // Player command: /buy upgrade [<number>|<key>]   (+ admin: /buy upgrade dump)
        // ===================================================================

        static void HandleUpgradeCommand(Player caller, string[] parts)
        {
            string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
            if (arg == "dump")
            {
                if (!caller.CanAdminExecute(Power.Generic)) { Reply(caller, "[Buy] Admin only."); return; }
                WriteUpgradeDump(caller);
                return;
            }
            if (!BuyActive) { Reply(caller, "[Buy] /buy is currently disabled."); return; }
            if (Cfg.BuyAdminOnly && !caller.CanAdminExecute(Power.Generic)) { Reply(caller, "[Buy] /buy is admin-only on this server."); return; }
            if (!Cfg.UpgradesEnabled) { Reply(caller, "[Buy] Unit upgrades are switched off on this server."); return; }

            // a number picks from the list as it stands now; a key names the stat
            if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                var view = BuildUpgView(caller);
                if (n < 1 || n > view.Offers.Count) { Reply(caller, view.Offers.Count == 0 ? "[Buy] " + view.Refusal : $"[Buy] No stat {n} (1-{view.Offers.Count})."); return; }
                TryBuyUpgrade(caller, view.Offers[n - 1].Stat.Key);
                return;
            }
            if (arg.Length > 0 && StatByKey(arg) != null) { TryBuyUpgrade(caller, arg); return; }
            // nothing, "status", or a 1.6.0 track word (primary / secondary): the list
            ShowUpgrades(caller);
        }

        static bool UpgCallerUnit(Player caller, out Unit unit, out string why)
        {
            unit = null!; why = "";
            var u = caller.ControlledUnit;
            if (u == null || caller.IsCommander || caller.Team == null)
            {
                why = "Take control of a unit in FPS mode to upgrade it.";
                return false;
            }
            if (u.IsDestroyed || u.ObjectInfo == null || (u.DamageManager != null && u.DamageManager.Health <= 0f))
            {
                why = "Your unit is destroyed.";
                return false;
            }
            unit = u;
            return true;
        }

        /// <summary>The numbered list. It also opens the /buy menu at the upgrade level, so "/3" (or a ModUI reply-page button) buys stat 3.</summary>
        static void ShowUpgrades(Player caller)
        {
            var view = BuildUpgView(caller);
            long key = GetPlayerSteamId(caller);
            if (!_buyStates.TryGetValue(key, out var state)) { state = new BuyState(); _buyStates[key] = state; }
            if (view.Offers.Count == 0)
            {
                state.Level = BuyLevel.Closed;
                Reply(caller, "[Buy] " + (view.Refusal.Length > 0 ? view.Refusal : "Nothing to upgrade."));
                return;
            }
            state.Level = BuyLevel.Upgrades;
            state.UpgradeUnit = view.Unit;
            state.UpgradeKeys = new List<string>();
            foreach (var o in view.Offers) state.UpgradeKeys.Add(o.Stat.Key);
            Reply(caller, $"[Buy] === Upgrades: {view.UnitName} === {view.Used}/{Cfg.UpgradeMaxPerUnit} used, {view.Price}cr per level. Credits: <color={COLOR_AFFORD}>{view.Credits}</color>");
            int group = -1;
            for (int i = 0; i < view.Offers.Count; i++)
            {
                var o = view.Offers[i];
                if (o.Stat.Group != group) { group = o.Stat.Group; if (group != UPG_MOVEMENT) Reply(caller, " " + UPG_GROUP_LABEL[group]); }   // Movement is its own row
                string color = !o.Maxed && o.Affordable ? COLOR_AFFORD : COLOR_TOOEXPENSIVE;
                string tail = o.Maxed ? "max reached" : $"{view.Price}cr";
                Reply(caller, $"  {i + 1}. <color={color}>{o.Stat.Label}</color> Lv {o.Level}  {OfferValue(o)}  ({o.Step}/level)  {tail}  [{o.Stat.Key}]");
            }
            Reply(caller, "/1-/N buys one level  ·  /0 to close  (or /buy upgrade <key>, e.g. /buy upgrade " + view.Offers[view.Offers.Count - 1].Stat.Key + ")");
        }

        /// <summary>"/N" while the upgrade list is open (chat, or a numbered button on a ModUI reply page): buy stat N, show the list again.</summary>
        static void BuyUpgradeFromMenu(Player caller, int n)
        {
            long key = GetPlayerSteamId(caller);
            if (!_buyStates.TryGetValue(key, out var state)) return;
            if (!BuyActive || !Cfg.UpgradesEnabled) { state.Level = BuyLevel.Closed; Reply(caller, "[Buy] Unit upgrades are switched off on this server."); return; }
            if (Cfg.BuyAdminOnly && !caller.CanAdminExecute(Power.Generic)) { state.Level = BuyLevel.Closed; Reply(caller, "[Buy] /buy is admin-only on this server."); return; }
            if (!ReferenceEquals(state.UpgradeUnit, caller.ControlledUnit))
            {
                Reply(caller, "[Buy] You are in a different unit now — here is its list.");
                ShowUpgrades(caller);
                return;
            }
            if (n < 1 || n > state.UpgradeKeys.Count) { Reply(caller, "[Buy] Invalid choice."); return; }
            TryBuyUpgrade(caller, state.UpgradeKeys[n - 1]);
            ShowUpgrades(caller);   // new levels and values; a swap put the player in a new instance, the list follows it
        }

        static void TryBuyUpgrade(Player caller, string statKey)
        {
            var stat = StatByKey(statKey);
            if (stat == null) { Reply(caller, $"[Buy] Unknown upgrade '{statKey}'. /buy upgrade lists them."); return; }
            if (!UpgCallerUnit(caller, out var unit, out string why)) { Reply(caller, $"[Buy] {why} <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>"); return; }
            var info = unit.ObjectInfo;
            string name = info.DisplayName ?? info.name;
            if (unit.InCompartment != null)
            {
                Reply(caller, $"[Buy] Leave the seat first — upgrades apply to the unit you drive or are. <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                return;
            }
            if (info.Critical)
            {
                Reply(caller, $"[Buy] {name} is a critical unit and cannot be upgraded. <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                return;
            }
            var prof = GetUpgProfile(info);
            var usable = prof != null ? UsableBindings(prof, stat) : new List<UpgBinding>();
            string what = stat.Group == UPG_MOVEMENT ? stat.Label : UPG_GROUP_LABEL[stat.Group].Replace(" weapon", "") + " " + stat.Label.ToLowerInvariant();
            if (prof == null || usable.Count == 0)
            {
                // a stat the unit has but the server excludes for it gets its own reason
                string? excl = null;
                if (prof != null) foreach (var b in prof.Of(stat.Key)) if ((excl = ExcludedBy(prof, b.Spec.Kind)) != null) break;
                if (excl != null)
                {
                    Reply(caller, $"[Buy] {what} upgrades are not available for {name} on this server (excluded: {ExclusionLabel(excl)}). <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                    return;
                }
                Reply(caller, $"[Buy] {name} has no {what} upgrade. <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                return;
            }

            long key = GetPlayerSteamId(caller);
            var old = RecordFor(unit);
            bool fresh = old == null || old.Buyer != key;
            int used = fresh ? 0 : old!.Total;
            if (used >= Cfg.UpgradeMaxPerUnit)
            {
                Reply(caller, $"[Buy] {name} already has {used}/{Cfg.UpgradeMaxPerUnit} upgrades. <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                return;
            }

            int price = UpgradePrice(info);
            int credits = GetCredits(key);
            // Short of credits: a funding provider (Si_Market coins) may cover the rest; it only sells once the upgrade worked.
            if (credits < price && !TryFund(key, price - credits, false))
            {
                Reply(caller, $"[Buy] Insufficient credits — a {what} upgrade for {name} costs {price}, you have {credits}.");
                return;
            }

            // The new state, applied only once delivery worked. Only the bought upgrade takes the steps configured now
            // (Movement: its move / jump / turn parts); every other stat keeps the step it was bought with, so a step change
            // never alters a stat the player did not buy again (1.9.1; before, any purchase re-stepped all kinds and could
            // refit the unit, which read as the general step interfering with the specific ones).
            var rec = new UpgRecord { Buyer = key, BuyerName = caller.PlayerName ?? "", Steps = fresh ? SnapshotSteps() : new Dictionary<string, float>(old!.Steps, StringComparer.Ordinal) };
            foreach (var k in stat.Group == UPG_MOVEMENT ? new[] { "move", "jump", "turn" } : new[] { stat.Kind }) rec.Steps[k] = UpgStep(k);
            if (!fresh) foreach (var kv in old!.Levels) rec.Levels[kv.Key] = kv.Value;
            rec.Levels[stat.Key] = rec.Level(stat.Key) + 1;
            Func<string, float> before = k => fresh ? 1f : old!.Factor(stat.Key, k);
            Func<string, float> after = k => rec.Factor(stat.Key, k);
            string change = DescribeValues(usable, before, after);   // for the reply
            string stepText = StepText(stat, usable, rec.StepOf);

            // A swap is needed when this stat has baked members: the new instance must carry ALL baked levels (the other
            // stats at their unchanged recorded steps). Live members follow through SyncLive below.
            bool statBaked = false;
            foreach (var b in usable) if (b.Baked) { statBaked = true; break; }
            bool doSwap = statBaked;

            if (doSwap)
            {
                if (!CanSwap(caller, unit, out string refuse))
                {
                    Reply(caller, $"[Buy] {refuse} <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                    return;
                }
                Unit? swapped;
                string err;
                try { swapped = SwapUnit(caller, unit, prof, rec, out err); }
                catch (Exception ex) { swapped = null; err = "exception: " + ex.Message; }
                if (swapped == null)
                {
                    Reply(caller, $"[Buy] Upgrade failed — {name} could not be refitted. <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                    MelonLogger.Warning($"[Buy] Upgrade swap failed for {caller.PlayerName} ({name}, {stat.Key}): {err}");
                    return;
                }
            }
            else
            {
                _upgRecords[unit] = rec;   // replaces another buyer's record too: their levels never applied to this player
            }

            int have = GetCredits(key);
            if (have < price && !TryFund(key, price - have, true))
                MelonLogger.Warning($"[Buy] Funding provider promised {price - have}cr for {caller.PlayerName} but did not deliver; charged the {have}cr there were.");
            AddCredits(key, -price);
            SyncLive(caller, false);

            int lvl = rec.Level(stat.Key);
            Reply(caller, $"[Buy] Upgraded <color={COLOR_AFFORD}>{name}</color> {what} to Lv {lvl} ({stepText} per level{(change.Length > 0 ? ": " + change : "")}) for {price}cr. Used {rec.Total}/{Cfg.UpgradeMaxPerUnit}. Remaining: {GetCredits(key)}cr.");
            MelonLogger.Msg($"[Buy] {caller.PlayerName} upgraded {name} ({info.name}) {stat.Key} -> Lv {lvl} for {price}cr" +
                            $" (total {rec.Total}, step {stepText}, {(doSwap ? "swapped" : "no swap")}).");
        }

        // ===================================================================
        // The swap
        // ===================================================================

        static readonly FieldInfo? _vtPriShots  = typeof(VehicleTurret).GetField("m_iPrimaryShotsFired", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo? _vtSecShots  = typeof(VehicleTurret).GetField("m_iSecondaryShotsFired", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo? _vtPriReload = typeof(VehicleTurret).GetField("m_fPrimaryReloadTime", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo? _vtSecReload = typeof(VehicleTurret).GetField("m_fSecondaryReloadTime", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo? _hhFired     = typeof(HumanHandsAnimator).GetField("NumFiredRounds", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo? _hhReload    = typeof(HumanHandsAnimator).GetField("CurrentReloadTime", BindingFlags.NonPublic | BindingFlags.Instance);

        static bool CanSwap(Player player, Unit unit, out string why)
        {
            why = "";
            var nc = unit.NetworkComponent;
            if (nc == null || !nc.NetHasSource
                || !ReferenceEquals(GameDatabase.GetSpawnablePrefab((int)nc.NetSourcePrefabIndex), unit.ObjectInfo.Prefab))
            {
                why = "This unit cannot be refitted (no networked prefab).";
                return false;
            }
            if (unit.Team != player.Team) { why = "This unit is not on your team."; return false; }
            float speed = unit.RigidBody != null ? unit.RigidBody.linearVelocity.magnitude : 0f;
            if (speed > Cfg.UpgradeMaxSwapSpeed)
            {
                why = $"Slow down to refit: {speed:F1} m/s (max {Cfg.UpgradeMaxSwapSpeed:F1}).";
                return false;
            }
            var drv = unit.DriverCompartment;
            foreach (var c in unit.UnitCompartments)
            {
                if (c == null) continue;
                foreach (var b in c.BoardedUnits)
                {
                    if (b == null) continue;
                    if (c == drv && b == c.DriverUnit && b.ControlledBy == null) continue;   // the driver's own soldier moves along
                    why = "Someone else is aboard — unload passengers first.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Replace the controlled unit with a fresh instance the buyer's client builds from the boosted prefab.
        /// Order (all in one server tick, every packet Reliable on lane 0, so the client sees them in this order):
        ///   1. Overrides packet to the buyer: baked members = base x/÷ F for all current levels
        ///   2. ObjectCreate + ObjectInit of the replacement (GameMode.SpawnUnitForPlayer sequence)
        ///   3. PlayerSelectUnit replacement + player.ControlledUnit = replacement (old unit's ControlledBy -> null)
        ///   4. driver soldier: compartment RPC remove from old, add to replacement (control stays on the replacement)
        ///   5. Overrides packet to the buyer: the same members back to the global value / vanilla
        ///   6. Object.Destroy(old): ObjectDestroy at end of frame; Unit.OnDestroy no longer nulls ControlledUnit (ControlledBy is null)
        /// No damage path: no death, no kill credit, no respawn timer (MP_Strategy.OnUnitDestroyed is not raised).
        /// Returns the replacement, or null with nothing charged and the player still in the old unit.
        /// </summary>
        static Unit? SwapUnit(Player player, Unit old, UpgProfile prof, UpgRecord rec, out string err)
        {
            err = "";
            var info = old.ObjectInfo;
            GameObject prefab = info.Prefab;

            var boost = new List<UpgWrite>();
            var revert = new List<UpgWrite>();
            foreach (var kv in rec.Levels)
            {
                if (kv.Value <= 0) continue;
                foreach (var b in prof.Of(kv.Key))
                {
                    if (!b.Baked) continue;
                    if (b.Group == UPG_MOVEMENT && !prof.ClientPhysics) continue;
                    if (ExcludedBy(prof, b.Spec.Kind) != null) continue;   // excluded since it was bought: the refit drops it
                    if (!BoostWrite(b, rec.Factor(kv.Key, b.Spec.Kind), out var w)) continue;
                    boost.Add(w);
                    revert.Add(RevertWrite(b.Target, b.Spec.Name, b.TypeByte));
                }
            }
            if (boost.Count == 0) { err = "no baked member to deliver"; return null; }

            Vector3 pos = old.transform.position;
            Quaternion rot = old.transform.rotation;
            var oldDrvComp = old.DriverCompartment;
            Unit? driver = oldDrvComp != null ? oldDrvComp.DriverUnit : null;

            Unit? nu = null;
            bool sent = false, selected = false;
            _upgSwapping = true;
            try
            {
                SendUpgWrites(player, boost);
                sent = true;

                // GameMode.SpawnUnitForPlayer (GameMode.cs 705-740): owner = player, no net init yet.
                GameObject go = Game.SpawnPrefab(prefab, player, null, pos, rot, true, false);
                if (go == null) { err = "Game.SpawnPrefab returned null"; return null; }
                nu = go.GetComponent<Unit>();
                if (nu == null) { UnityEngine.Object.Destroy(go); err = "spawned object has no Unit"; return null; }
                nu.QueueTeleport(pos, rot);
                nu.Team = player.Team;
                nu.IsFPSSpawned = old.IsFPSSpawned;
                CopyUnitState(old, nu);
                var nnc = go.GetComponent<NetworkComponent>();
                if (nnc == null) { UnityEngine.Object.Destroy(go); nu = null; err = "spawned object has no NetworkComponent"; return null; }
                nnc.SendNetInit(null);

                if (driver != null && nu.DriverCompartment == null)
                { UnityEngine.Object.Destroy(go); nu = null; err = "replacement has no driver compartment"; return null; }
                if (!player.GetCanSwitchToUnit(nu))
                { UnityEngine.Object.Destroy(go); nu = null; err = "player cannot switch to the replacement"; return null; }

                // Same two calls as SpawnUnitForPlayer, vehicle enter (UnitCompartment.AddUnit) and Player.SetUnit.
                NetworkLayer.SendPlayerSelectUnit(player, nu);
                player.ControlledUnit = nu;
                selected = true;
                if (player.ControlledUnit != nu) { err = "ControlledUnit did not take the replacement"; selected = false; RollBackSwap(player, old, nu, driver, oldDrvComp); nu = null; return null; }

                if (driver != null)
                {
                    // Both are vanilla server paths that RPC the clients. Neither changes control: RemoveUnit only re-selects
                    // the soldier while the vehicle is still controlled (it no longer is), AddUnit only selects the vehicle
                    // while the soldier is controlled (it is not).
                    oldDrvComp!.RequestRemoveUnit(driver);
                    var newDrvComp = nu.DriverCompartment!;
                    if (driver.InCompartment == null) newDrvComp.RequestAddUnit(driver);
                    if (driver.InCompartment != newDrvComp)
                    {
                        err = "driver could not be moved into the replacement";
                        RollBackSwap(player, old, nu, driver, oldDrvComp);
                        nu = null;
                        return null;
                    }
                    // RemoveUnit queued a teleport to the old exit; land it on the seat instead (server side).
                    driver.QueueTeleport(newDrvComp.transform.position, newDrvComp.transform.rotation);
                }
            }
            catch (Exception ex)
            {
                err = "exception: " + ex.Message;
                if (nu != null)
                {
                    try { if (selected) RollBackSwap(player, old, nu, driver, oldDrvComp); else UnityEngine.Object.Destroy(nu.gameObject); }
                    catch (Exception rex) { MelonLogger.Warning($"[Buy] Upgrade swap rollback threw: {rex.Message}"); }
                }
                nu = null;
                return null;
            }
            finally
            {
                if (sent)
                {
                    try { SendUpgWrites(player, revert); }
                    catch (Exception ex) { MelonLogger.Warning($"[Buy] Upgrade revert packet for {player.PlayerName} threw: {ex.Message}"); }
                }
                _upgSwapping = false;
            }

            // Success: the record moves to the replacement, the old instance goes without a damage path.
            _upgRecords.Remove(old);
            _upgRecords[nu!] = rec;
            UnityEngine.Object.Destroy(old.gameObject);
            MelonLogger.Msg($"[Buy] Swap: {player.PlayerName} {info.name} -> new instance at ({pos.x:F0},{pos.y:F0},{pos.z:F0}), {boost.Count} baked member(s)" +
                            $"{(driver != null ? ", driver moved" : "")}, health {(nu!.DamageManager != null ? nu.DamageManager.Health.ToString("F0", CultureInfo.InvariantCulture) : "?")}.");
            return nu;
        }

        /// <summary>Failure after the replacement exists: control and driver back to the old unit, replacement removed.</summary>
        static void RollBackSwap(Player player, Unit old, Unit nu, Unit? driver, UnitCompartment? oldDrvComp)
        {
            if (driver != null && oldDrvComp != null)
            {
                if (driver.InCompartment != null && driver.InCompartment != oldDrvComp) driver.InCompartment.RequestRemoveUnit(driver);
                if (driver.InCompartment == null) oldDrvComp.RequestAddUnit(driver);
            }
            if (player.ControlledUnit != old && player.GetCanSwitchToUnit(old))
            {
                NetworkLayer.SendPlayerSelectUnit(player, old);
                player.ControlledUnit = old;
            }
            if (driver == null || driver.InCompartment == null || driver.InCompartment.OwnerUnit != nu)
                UnityEngine.Object.Destroy(nu.gameObject);
            else
                MelonLogger.Warning($"[Buy] Upgrade rollback: driver is still inside the replacement — replacement kept.");
        }

        /// <summary>Health, rigidbody velocity, turret and hand-gun ammo / reload — set before SendNetInit so the init ships them.</summary>
        static void CopyUnitState(Unit from, Unit to)
        {
            try
            {
                if (from.DamageManager != null && to.DamageManager != null)
                {
                    float h = from.DamageManager.Health;
                    if (h > 0f && Mathf.Abs(h - to.DamageManager.Health) > 0.01f) to.DamageManager.SetHealth(h);
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[Buy] Swap: health copy failed: {ex.Message}"); }
            try
            {
                var a = from.RigidBody; var b = to.RigidBody;
                if (a != null && b != null && !b.isKinematic)
                {
                    b.linearVelocity = a.linearVelocity;
                    b.angularVelocity = a.angularVelocity;
                }
            }
            catch { }
            try
            {
                var va = from.GetComponentsInChildren<VehicleTurret>(true);
                var vb = to.GetComponentsInChildren<VehicleTurret>(true);
                for (int i = 0; i < va.Length && i < vb.Length; i++)
                    foreach (var f in new[] { _vtPriShots, _vtSecShots, _vtPriReload, _vtSecReload })
                        if (f != null) f.SetValue(vb[i], f.GetValue(va[i]));
                var ha = from.GetComponentsInChildren<HumanHandsAnimator>(true);
                var hb = to.GetComponentsInChildren<HumanHandsAnimator>(true);
                for (int i = 0; i < ha.Length && i < hb.Length; i++)
                    foreach (var f in new[] { _hhFired, _hhReload })
                        if (f != null) f.SetValue(hb[i], f.GetValue(ha[i]));
            }
            catch (Exception ex) { MelonLogger.Warning($"[Buy] Swap: ammo copy failed: {ex.Message}"); }
        }

        // ===================================================================
        // Si_ModUI: per-player "actions" panel
        // ===================================================================

        /// <summary>
        /// Si_ModUI per-player hook (server 1.7.0+ renders "actions"; older servers merge only "v" / "tables" and ignore it).
        /// The panel lists the stats of the unit this player controls; each row's button sends "/buy upgrade &lt;key&gt;".
        /// "ttl" asks the ModUI server to rebuild this document at most every 5 s (credits change with kills).
        /// </summary>
        public static string? ModUIPlayerData(ulong steamId)
        {
            try
            {
                Player? player = PlayerBySteamId((long)steamId);
                if (player == null) return null;
                var panel = new Newtonsoft.Json.Linq.JObject { ["n"] = "Unit upgrades" };
                var groups = new Newtonsoft.Json.Linq.JArray();
                if (!BuyActive) panel["e"] = "The economy is switched off on this server.";
                else if (!Cfg.UpgradesEnabled) panel["e"] = "Unit upgrades are switched off on this server.";
                else if (Cfg.BuyAdminOnly && !player.CanAdminExecute(Power.Generic)) panel["e"] = "Buying is admin-only on this server.";
                else
                {
                    var view = BuildUpgView(player);
                    if (view.Offers.Count == 0) panel["e"] = view.Refusal.Length > 0 ? view.Refusal : "Nothing to upgrade.";
                    else
                    {
                        panel["h"] = new Newtonsoft.Json.Linq.JArray(
                            $"{view.UnitName}: {view.Used}/{Cfg.UpgradeMaxPerUnit} upgrades used, {StepSummary()}",
                            $"Credits: {view.Credits}   Price per level: {view.Price}cr");
                        int remaining = Math.Max(0, Cfg.UpgradeMaxPerUnit - view.Used);
                        Newtonsoft.Json.Linq.JArray? rows = null;
                        int group = -1;
                        foreach (var o in view.Offers)
                        {
                            if (o.Stat.Group != group)
                            {
                                group = o.Stat.Group;
                                rows = new Newtonsoft.Json.Linq.JArray();
                                groups.Add(new Newtonsoft.Json.Linq.JObject { ["n"] = UPG_GROUP_LABEL[group], ["r"] = rows });
                            }
                            bool on = !o.Maxed && o.Affordable;
                            rows!.Add(new Newtonsoft.Json.Linq.JObject
                            {
                                ["l"] = o.Stat.Label,
                                ["lv"] = o.Level,
                                ["max"] = o.Level + remaining,
                                ["d"] = OfferValue(o) + (o.Step.Length > 0 ? "   (" + o.Step + " per level)" : ""),
                                ["c"] = o.Maxed ? "" : view.Price + "cr",
                                ["t"] = "/buy upgrade " + o.Stat.Key,
                                ["b"] = o.Maxed ? "MAX" : o.Affordable ? "BUY" : "NO CREDITS",
                                ["on"] = on,
                            });
                        }
                    }
                }
                if (groups.Count > 0) panel["g"] = groups;
                var doc = new Newtonsoft.Json.Linq.JObject { ["actions"] = panel, ["ttl"] = 5 };
                // Admins: their own exclusion-editor state (selected stat, toggles, "via <building>" labels; labels need
                // Si_ModUI server 1.8.1, older servers apply only the values).
                if (player.CanAdminExecute(Power.Generic))
                {
                    var v = new Newtonsoft.Json.Linq.JObject();
                    var l = new Newtonsoft.Json.Linq.JObject();
                    foreach (var row in ExclusionEditorRows(ExclusionPickFor((long)steamId)))
                    {
                        v[row.Text] = row.Value;
                        if (row.Via.Length > 0) l[row.Text] = row.Label + "   (via " + row.Via + ")";
                    }
                    doc["v"] = v;
                    if (l.Count > 0) doc["l"] = l;
                }
                return Newtonsoft.Json.JsonConvert.SerializeObject(doc, Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] ModUIPlayerData failed: {ex.Message}");
                return null;
            }
        }

        // ===================================================================
        // Admin diagnostic: /buy upgrade dump
        // ===================================================================

        static void WriteUpgradeDump(Player? caller)
        {
            string path = Path.Combine(Paths.ConfigDir, "Si_Buy_UpgradeDump.txt");
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine($"Si_Buy upgrade dump {DateTime.Now:yyyy-MM-dd HH:mm:ss}  general step {Cfg.UpgradeStepPercent.ToString("0.##", inv)}%  max {Cfg.UpgradeMaxPerUnit} levels per unit  cost x{Cfg.UpgradeCostFraction.ToString("0.##", inv)} (min {Cfg.UpgradeMinCost}cr)");
                sb.AppendLine("Stat kinds switched on: " + EnabledKinds());
                var stepLine = new List<string>();
                foreach (var k in UPG_KINDS) stepLine.Add(k + " " + UpgStep(k).ToString("0.##", inv) + "%" + (ValidStep(StepOverride(k)) ? "" : " (general)"));
                sb.AppendLine("Step per kind (a new purchase): " + string.Join(", ", stepLine));
                sb.AppendLine("Columns: member | delivery | declared on | field type | direction | base (global OM value if one is in force) | target");
                sb.AppendLine();
                _upgProfiles.Clear();   // rebuild from the current prefabs
                var infos = new List<ObjectInfo>(Resources.FindObjectsOfTypeAll<ObjectInfo>());
                infos.Sort((x, y) => string.CompareOrdinal(x != null ? x.name : "", y != null ? y.name : ""));
                int n = 0;
                foreach (var info in infos)
                {
                    if (info == null || info.Prefab == null || info.DisableDirectControl) continue;
                    if (info.Prefab.GetComponent<Unit>() == null) continue;
                    var prof = GetUpgProfile(info);
                    if (prof == null) continue;
                    n++;
                    sb.AppendLine($"== {prof.Display} ({prof.InfoName})  price {UpgradePrice(info)}cr  clientPhysics={prof.ClientPhysics}  networkedPrefab={prof.SwapOk}  infoTarget={prof.InfoTargetOk}{(info.Critical ? "  CRITICAL (refused)" : "")}");
                    for (int g = 0; g < 3; g++)
                        if (prof.Projectiles[g].Count > 0) sb.AppendLine($"   {UPG_GROUP_LABEL[g]} projectiles: {string.Join(", ", prof.Projectiles[g])}");
                    string bld = BuildingFor(prof);
                    var excl = new List<string>();
                    foreach (var k in UPG_KINDS) { var e = ExcludedBy(prof, k); if (e != null) excl.Add(k + " (" + e + ")"); }
                    sb.AppendLine($"   built at: {(bld.Length > 0 ? bld : "?")}   excluded: {(excl.Count > 0 ? string.Join(", ", excl) : "none")}");
                    // every turret, in the order OM sees them (the first one is the only one the prefab members reach)
                    var turrets = info.Prefab.GetComponentsInChildren<VehicleTurret>(true);
                    for (int ti = 0; ti < turrets.Length; ti++)
                    {
                        var t = turrets[ti];
                        sb.AppendLine($"   turret {ti}{(ti == 0 ? " (reached)" : "")} '{t.name}': " +
                                      $"P mag {t.PrimaryMagazineSize}{(t.PrimaryReloadShotByShot ? " shot-by-shot x" + t.PrimaryShotByShotCount : "")} muzzles {t.PrimaryMuzzles?.Count ?? 0} {(t.PrimaryProjectileData != null ? t.PrimaryProjectileData.name : "-")} | " +
                                      $"S mag {t.SecondaryMagazineSize}{(t.SecondaryReloadShotByShot ? " shot-by-shot x" + t.SecondaryShotByShotCount : "")} muzzles {t.SecondaryMuzzles?.Count ?? 0} {(t.SecondaryProjectileData != null ? t.SecondaryProjectileData.name : "-")}");
                    }
                    foreach (var stat in UPG_STATS)
                    {
                        var all = prof.Of(stat.Key);
                        if (all.Count == 0) continue;
                        var usable = UsableBindings(prof, stat);
                        string shownText = DescribeValues(all, k => 1f, null);
                        string value = shownText.Length > 0 ? "  shows " + shownText : "";
                        sb.AppendLine($"   [{stat.Key}] {UPG_GROUP_LABEL[stat.Group]} / {stat.Label}: {(usable.Count > 0 ? "offered" : "not offered")}, {StepText(stat, all, UpgStep)} per level{value}");
                        foreach (var b in all)
                        {
                            TryGetBase(b, out float f, out int i, out bool global);
                            string bv = b.TypeByte == 4 ? f.ToString("0.####", inv) : i.ToString(inv);
                            string state = (usable.Contains(b) ? "" : ExcludedBy(prof, b.Spec.Kind) != null ? "  (excluded)" : "  (off)")
                                         + (b.Holder is Component hc ? "  on '" + hc.name + "'" : "");
                            sb.AppendLine($"      {b.Spec.Name,-24} {(b.Baked ? "baked/swap" : "live"),-10} {b.Owner,-22} {(b.TypeByte == 4 ? "float" : "int"),-5} {(b.Spec.LargerIsBetter ? "larger=better (xF)" : "smaller=better (/F)"),-20} base {bv}{(global ? " (global OM)" : "")}  {b.Target}{state}");
                        }
                    }
                    foreach (string note in prof.Notes) sb.AppendLine("   note: " + note);
                }
                File.WriteAllText(path, sb.ToString());
                Reply(caller, $"[Buy] Upgrade dump: {n} unit types -> {path}");
            }
            catch (Exception ex)
            {
                Reply(caller, $"[Buy] Upgrade dump failed: {ex.Message}");
            }
        }
    }
}
