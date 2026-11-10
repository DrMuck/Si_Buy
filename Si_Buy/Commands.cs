/*
 Commands.cs — admin sub-commands of /buy and the chat helpers.

 Player use of /buy (menu, /buy <N>, /buy back, /buy exit) lives in Buy.cs.
 Everything here is reached through the same command and gated on
 caller.CanAdminExecute(Power.Generic):

   /buy status                         economy state + your balance (any player)
   /buy on | off                       master switch
   /buy starter <amount>               StarterCreditsPerPlayer
   /buy give <steamId|name> <amount>   grant credits to a player
   /buy buildingreward on|off          structure-kill rewards
   /buy structures on|off              'Buildings' category in the menu
   /buy adminonly on|off               restrict /buy to admins
   /buy bounty [on|off|list|reload|add <steamId> <amount> [name]|remove <steamId>]
   /buy upgrades on|off                unit upgrades (Upgrades.cs)
   /buy upgradestep [<kind>] <percent|default>   general step, or one kind's own step
   /buy upgrademax|upgradecost|upgrademincost|upgradeswapspeed <value>
   /buy upgradestat <stat> [on|off]    which stats can be upgraded (no on|off = toggle)
   /buy upgradeexclude [<stat> [add|remove <building or unit>]]   stats not offered for some units
   /buy upgrade dump                   upgrade diagnostic file (handled in Upgrades.cs)
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Si_Buy
{
    public partial class Buy
    {
        static readonly string[] ADMIN_SUBS =
            { "status", "on", "off", "starter", "give", "buildingreward", "structurereward", "structures", "buystructures", "buybuildings", "adminonly", "bounty", "help", "enabled",
              "upgrades", "upgradestep", "upgradesteps", "upgrademax", "upgradecost", "upgrademincost", "upgradeswapspeed", "upgradestat", "upgradeexclude" };

        /// <summary>
        /// /buy upgradeexclude                                  list every stat's exclusions
        /// /buy upgradeexclude &lt;kind&gt;                           one stat
        /// /buy upgradeexclude &lt;kind&gt; add|remove &lt;name&gt;         name = production building or unit (or building:X / unit:X)
        /// /buy upgradeexclude add|remove &lt;kind&gt; &lt;name&gt;         same, the order the ModUI picker rows send
        /// </summary>
        static void HandleUpgradeExclude(Player? caller, string[] parts)
        {
            string a = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
            string b = parts.Length > 3 ? parts[3].ToLowerInvariant() : "";
            long sid = caller != null ? GetPlayerSteamId(caller) : 0;
            // ModUI editor forms (1.9.1): "pick <kind>" selects the stat the toggles edit, "set <entry> on|off" sets one toggle
            if (a == "pick")
            {
                if (Array.IndexOf(UPG_KINDS, b) < 0) { Reply(caller, $"[Buy] Usage: /buy upgradeexclude pick <{string.Join("|", UPG_KINDS)}>"); return; }
                _exclPick[sid] = b;
                string now = ExclusionList(b);
                Reply(caller, $"[Buy] Editing exclusions of {UpgStatAdminLabel(b)}: {(now.Length > 0 ? "not offered for " + now : "offered for every unit")}.");
                return;
            }
            if (a == "set")
            {
                string last = parts.Length > 4 ? parts[parts.Length - 1].ToLowerInvariant() : "";
                bool? on = ParseOnOff(last);
                string target = on.HasValue ? string.Join(" ", parts, 3, parts.Length - 4).Trim() : "";
                if (!on.HasValue || target.Length == 0) { Reply(caller, "[Buy] Usage: /buy upgradeexclude set <building:X|unit:X> on|off  (edits the stat picked with /buy upgradeexclude pick)"); return; }
                SetExclusion(caller, ExclusionPickFor(sid), target, on.Value);
                return;
            }
            string kind, op; int nameAt;
            if ((a == "add" || a == "remove" || a == "rm") && Array.IndexOf(UPG_KINDS, b) >= 0) { op = a; kind = b; nameAt = 4; }
            else { kind = a; op = b; nameAt = 4; }
            string usage = $"Usage: /buy upgradeexclude [<{string.Join("|", UPG_KINDS)}> [add|remove|toggle <building or unit>]]";
            if (kind.Length == 0)
            {
                Reply(caller, "[Buy] Upgrade exclusions (stat not offered for these units):");
                foreach (var k in UPG_KINDS)
                {
                    string l = ExclusionList(k);
                    if (l.Length > 0) Reply(caller, $"  {k}: {l}");
                }
                if (string.IsNullOrEmpty(ExclusionSummary())) Reply(caller, "  none");
                Reply(caller, usage);
                return;
            }
            if (Array.IndexOf(UPG_KINDS, kind) < 0) { Reply(caller, "[Buy] " + usage); return; }
            var list = Cfg.UpgradeExclusions[kind];
            if (op.Length == 0) { Reply(caller, $"[Buy] {kind} is not offered for: {(list.Count > 0 ? string.Join(", ", list) : "nothing excluded")}. {usage}"); return; }
            string name = parts.Length > nameAt ? string.Join(" ", parts, nameAt, parts.Length - nameAt).Trim() : "";
            if (name.Length > 0 && op == "toggle")
            {
                string toggled = ResolveExclusionName(name, out string te, out _) ? te : name;
                SetExclusion(caller, kind, name, !(IsExcludedDirect(kind, toggled) || (TryParseExclusion(toggled, out string tt, out string tn) && tt == EXCL_UNIT && ExcludedVia(kind, tn).Length > 0)));
                return;
            }
            if (name.Length == 0 || (op != "add" && op != "remove" && op != "rm")) { Reply(caller, "[Buy] " + usage); return; }
            if (op == "add")
            {
                if (!ResolveExclusionName(name, out string entry, out string why)) { Reply(caller, "[Buy] " + why); return; }
                if (list.Exists(x => x.Equals(entry, StringComparison.OrdinalIgnoreCase))) { Reply(caller, $"[Buy] {kind} already excludes {entry}."); return; }
                list.Add(entry); SaveConfig();
                Reply(caller, $"[Buy] {kind} upgrades are no longer offered for {entry}. Units that already carry a level keep it until their next refit. Now excluded: {ExclusionList(kind)}.");
                return;
            }
            // remove: the entry as listed, its bare name, or what the name resolves to
            string resolved = ResolveExclusionName(name, out string res, out _) ? res : "";
            int removed = list.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase) || ExclusionLabel(x).Equals(name, StringComparison.OrdinalIgnoreCase)
                                             || (resolved.Length > 0 && x.Equals(resolved, StringComparison.OrdinalIgnoreCase)));
            if (removed == 0) { Reply(caller, $"[Buy] {kind} has no exclusion '{name}'. Excluded: {(list.Count > 0 ? string.Join(", ", list) : "nothing")}."); return; }
            SaveConfig();
            Reply(caller, $"[Buy] {kind} upgrades are offered again for {name}. Now excluded: {(list.Count > 0 ? ExclusionList(kind) : "nothing")}.");
        }

        /// <summary>
        /// One toggle of the exclusion editor (and /buy upgradeexclude &lt;kind&gt; toggle). A unit excluded through its production
        /// building is not taken out on its own: the reply says which building to re-offer.
        /// </summary>
        static void SetExclusion(Player? caller, string kind, string name, bool exclude)
        {
            if (!ResolveExclusionName(name, out string entry, out string why)) { Reply(caller, "[Buy] " + why); return; }
            var list = Cfg.UpgradeExclusions[kind];
            string label = UpgStatAdminLabel(kind), shown = ExclusionLabel(entry);
            string via = TryParseExclusion(entry, out string type, out string bare) && type == EXCL_UNIT ? ExcludedVia(kind, bare) : "";
            bool direct = IsExcludedDirect(kind, entry);
            if (exclude)
            {
                if (direct || via.Length > 0) { Reply(caller, $"[Buy] {label} is already not offered for {shown}{(via.Length > 0 && !direct ? " (via " + via + ")" : "")}."); return; }
                list.Add(entry); SaveConfig();
                Reply(caller, $"[Buy] {label} is no longer offered for {shown}. Units that already carry a level keep it until their next refit.");
                return;
            }
            if (!direct)
            {
                Reply(caller, via.Length > 0
                    ? $"[Buy] {shown} is excluded through {via}: set {via} to OFFERED to offer {label} again for its units, or exclude the other units one by one."
                    : $"[Buy] {label} is already offered for {shown}.");
                return;
            }
            list.RemoveAll(x => x.Equals(entry, StringComparison.OrdinalIgnoreCase)); SaveConfig();
            Reply(caller, $"[Buy] {label} is offered again for {shown}{(via.Length > 0 ? " — but still excluded through " + via : "")}.");
        }

        /// <summary>Short form for /buy status: "magazine: Heavy Factory, Ultra Heavy Factory, Bomber, Freighter"; "" when none.</summary>
        static string ExclusionSummary()
        {
            var parts = new List<string>();
            foreach (var k in UPG_KINDS) { string l = ExclusionList(k); if (l.Length > 0) parts.Add(k + ": " + l); }
            return string.Join("; ", parts);
        }

        /// <summary>
        /// /buy upgradestep &lt;percent&gt;                      general step (UpgradeStepPercent), 0 &lt; x &lt;= 500
        /// /buy upgradestep &lt;kind&gt; &lt;percent|default&gt;     one kind (UpgradeStep&lt;Kind&gt;); default (or -1) = follow the general step
        /// /buy upgradestep [&lt;kind&gt;]                       show
        /// Changes reach units at their next upgrade purchase (records keep the steps they were bought with).
        /// </summary>
        static void HandleUpgradeStep(Player? caller, string[] parts)
        {
            var inv = CultureInfo.InvariantCulture;
            string a = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
            string b = parts.Length > 3 ? parts[3].ToLowerInvariant().TrimEnd('%') : "";
            string usage = $"Usage: /buy upgradestep <percent>  or  /buy upgradestep <{string.Join("|", UPG_KINDS)}> <percent|default>  (0 < percent <= {UPG_STEP_MAX:0})";
            if (Array.IndexOf(UPG_KINDS, a) >= 0)
            {
                string kind = a;
                if (b.Length == 0)
                {
                    Reply(caller, $"[Buy] Step for '{kind}' = {UpgStep(kind).ToString("0.#", inv)}%{(ValidStep(StepOverride(kind)) ? "" : " (general)")}. {usage}");
                    return;
                }
                if (parts.Length > 4) { Reply(caller, "[Buy] One value only — nothing changed. " + usage); return; }
                if (b == "default" || b == "general" || b == "-1")
                {
                    SetStepOverride(kind, -1f); SaveConfig();
                    Reply(caller, $"[Buy] Step for '{kind}' follows the general step again: {UpgStep(kind).ToString("0.#", inv)}% per level (applies from the next purchase on each unit).");
                    return;
                }
                if (TryParseFloat(b, out float ks) && ValidStep(ks))
                {
                    SetStepOverride(kind, ks); SaveConfig();
                    Reply(caller, $"[Buy] Step for '{kind}' = {ks.ToString("0.#", inv)}% per level (applies from the next purchase on each unit).");
                    return;
                }
                Reply(caller, "[Buy] " + usage);
                return;
            }
            // The general form takes exactly one number: "/buy upgradestep 30 damage" or a misspelt kind ("/buy upgradestep
            // firerat 30") must never fall through to the general setter (1.9.1).
            if (parts.Length > 3)
            {
                Reply(caller, $"[Buy] '{a}' is not a stat kind — nothing changed. {usage}");
                return;
            }
            if (TryParseFloat(a.TrimEnd('%'), out float step) && ValidStep(step))
            {
                Cfg.UpgradeStepPercent = step; SaveConfig();
                string own = StepOverrides();
                Reply(caller, $"[Buy] General upgrade step = {step.ToString("0.#", inv)}% per level (applies from the next purchase on each unit){(own.Length > 0 ? ". Own steps: " + own : "")}.");
                return;
            }
            string overrides = StepOverrides();
            Reply(caller, $"[Buy] General upgrade step = {Cfg.UpgradeStepPercent.ToString("0.#", inv)}% per level{(overrides.Length > 0 ? "; own steps: " + overrides : "; no kind has its own step")}. {usage}");
        }

        static bool IsAdminSub(string sub)
        {
            for (int i = 0; i < ADMIN_SUBS.Length; i++) if (ADMIN_SUBS[i] == sub) return true;
            return false;
        }

        /// <summary>
        /// Handles the admin / status sub-commands. Returns true when the sub-command was
        /// consumed (even on an error reply), false when it is menu navigation for Buy.cs.
        /// Runs BEFORE the Enabled / BuyAdminOnly gates so an admin can switch the
        /// economy on from chat.
        /// </summary>
        static bool TryHandleAdminSub(Player? caller, string sub, string[] parts)
        {
            if (!IsAdminSub(sub)) return false;
            string val = parts.Length > 2 ? parts[2] : "";

            if (sub == "status")
            {
                ShowStatus(caller);
                return true;
            }

            bool isAdmin = caller == null || caller.CanAdminExecute(Power.Generic);
            if (!isAdmin)
            {
                Reply(caller, "[Buy] Admin only.");
                return true;
            }

            switch (sub)
            {
                case "enabled":   // switch form of on/off (used by the Si_ModUI switch row)
                    if (val == "on") goto case "on";
                    if (val == "off") goto case "off";
                    Reply(caller, $"[Buy] Economy {(Cfg.Enabled ? "ON" : "OFF")}. Usage: /buy enabled on|off");
                    break;
                case "on":
                    Cfg.Enabled = true; SaveConfig();
                    Reply(caller, "[Buy] Economy ON — /buy, kill rewards, starter credits and bounties are active.");
                    break;
                case "off":
                    Cfg.Enabled = false; SaveConfig();
                    ResetBuyState();
                    Reply(caller, "[Buy] Economy OFF — balances are kept, nothing is paid or sold until /buy on.");
                    break;
                case "starter":
                    if (int.TryParse(val, out int s) && s >= 0)
                    {
                        Cfg.StarterCreditsPerPlayer = s; SaveConfig();
                        Reply(caller, $"[Buy] Starter credits = {s}cr per player per round.");
                    }
                    else Reply(caller, $"[Buy] Starter credits = {Cfg.StarterCreditsPerPlayer}cr per player per round. Usage: /buy starter <amount>");
                    break;
                case "give":
                {
                    if (parts.Length < 4 || !int.TryParse(parts[3], out int amount) || amount == 0)
                    {
                        Reply(caller, "[Buy] Usage: /buy give <steamId|player name> <amount>  (negative amount deducts)");
                        break;
                    }
                    Player? target = FindPlayer(val, out long sid);
                    if (sid == 0)
                    {
                        Reply(caller, $"[Buy] No connected player or Steam ID matches '{val}'.");
                        break;
                    }
                    AddCredits(sid, amount);
                    string who = target != null ? target.PlayerName : sid.ToString();
                    Reply(caller, $"[Buy] {(amount > 0 ? "+" : "")}{amount}cr -> {who}. Balance: {GetCredits(sid)}cr");
                    if (target != null && target != caller)
                    {
                        try { HelperMethods.SendChatMessageToPlayer(target, $"[Buy] An admin {(amount > 0 ? "granted you" : "deducted")} <color=#39ff14>{Math.Abs(amount)}</color>cr. Balance: {GetCredits(sid)}cr"); }
                        catch { }
                    }
                    break;
                }
                case "buildingreward":
                case "structurereward":
                {
                    bool? want = ParseOnOff(val);
                    if (!want.HasValue)
                    {
                        Reply(caller, $"[Buy] Building-kill reward = {(Cfg.StructureKillRewardsEnabled ? "ON" : "OFF")}. Usage: /buy buildingreward <on|off>");
                        break;
                    }
                    Cfg.StructureKillRewardsEnabled = want.Value; SaveConfig();
                    Reply(caller, $"[Buy] Building-kill reward = {(want.Value ? "ON" : "OFF")}. " +
                        (want.Value ? "Destroying enemy structures now pays the killer credits (cost × RewardKillFraction)." : "Structure destruction no longer pays the killer. Unit kills still pay."));
                    break;
                }
                case "structures":
                case "buystructures":
                case "buybuildings":
                {
                    bool? want = ParseOnOff(val);
                    if (!want.HasValue)
                    {
                        Reply(caller, $"[Buy] /buy structures = {(Cfg.BuyStructuresEnabled ? "ON" : "OFF")}. Usage: /buy structures <on|off>");
                        break;
                    }
                    Cfg.BuyStructuresEnabled = want.Value; SaveConfig();
                    Reply(caller, $"[Buy] /buy structures = {(want.Value ? "ON" : "OFF")}. " +
                        (want.Value ? "A 'Buildings' category now appears at the end of the /buy menu." : "Structures are hidden from the /buy menu again."));
                    break;
                }
                case "adminonly":
                {
                    bool? want = ParseOnOff(val);
                    if (!want.HasValue)
                    {
                        Reply(caller, $"[Buy] Admin-only = {(Cfg.BuyAdminOnly ? "ON" : "OFF")}. Usage: /buy adminonly <on|off>");
                        break;
                    }
                    Cfg.BuyAdminOnly = want.Value; SaveConfig();
                    Reply(caller, $"[Buy] Admin-only = {(want.Value ? "ON — only admins can open the menu" : "OFF — everyone can /buy")}.");
                    break;
                }
                case "bounty":
                    HandleBountySubcommand(caller, val, parts);
                    break;
                case "upgrades":
                {
                    bool? want = ParseOnOff(val);
                    if (!want.HasValue)
                    {
                        Reply(caller, $"[Buy] Unit upgrades = {(Cfg.UpgradesEnabled ? "ON" : "OFF")}. Usage: /buy upgrades <on|off>");
                        break;
                    }
                    Cfg.UpgradesEnabled = want.Value; SaveConfig();
                    OnUpgradesSwitched();
                    Reply(caller, $"[Buy] Unit upgrades = {(want.Value ? "ON — players can /buy upgrade the unit they control" : "OFF — damage boosts reverted; units already refitted keep their handling until they are gone")}.");
                    break;
                }
                case "upgradestep":
                case "upgradesteps":   // the ModUI picker row (kind, then value) needs its own command text
                    HandleUpgradeStep(caller, parts);
                    break;
                case "upgrademax":
                    if (int.TryParse(val, out int max) && max >= 0 && max <= 50)
                    {
                        Cfg.UpgradeMaxPerUnit = max; SaveConfig();
                        Reply(caller, $"[Buy] Upgrades per unit = {max}.");
                    }
                    else Reply(caller, $"[Buy] Upgrades per unit = {Cfg.UpgradeMaxPerUnit}. Usage: /buy upgrademax <levels>");
                    break;
                case "upgradecost":
                    if (TryParseFloat(val, out float frac) && frac >= 0f && frac <= 100f)
                    {
                        Cfg.UpgradeCostFraction = frac; SaveConfig();
                        Reply(caller, $"[Buy] Upgrade price = unit cost x {frac:0.##} (min {Cfg.UpgradeMinCost}cr).");
                    }
                    else Reply(caller, $"[Buy] Upgrade price = unit cost x {Cfg.UpgradeCostFraction:0.##} (min {Cfg.UpgradeMinCost}cr). Usage: /buy upgradecost <fraction>");
                    break;
                case "upgrademincost":
                    if (int.TryParse(val, out int minCost) && minCost >= 0)
                    {
                        Cfg.UpgradeMinCost = minCost; SaveConfig();
                        Reply(caller, $"[Buy] Upgrade minimum price = {minCost}cr.");
                    }
                    else Reply(caller, $"[Buy] Upgrade minimum price = {Cfg.UpgradeMinCost}cr. Usage: /buy upgrademincost <credits>");
                    break;
                case "upgradeswapspeed":
                    if (TryParseFloat(val, out float swapSpeed) && swapSpeed >= 0f)
                    {
                        Cfg.UpgradeMaxSwapSpeed = swapSpeed; SaveConfig();
                        Reply(caller, $"[Buy] Upgrades that refit the unit need it below {swapSpeed:0.#} m/s.");
                    }
                    else Reply(caller, $"[Buy] Refit speed limit = {Cfg.UpgradeMaxSwapSpeed:0.#} m/s. Usage: /buy upgradeswapspeed <m/s>");
                    break;
                case "upgradeexclude":
                    HandleUpgradeExclude(caller, parts);
                    break;
                case "upgradestat":
                {
                    // /buy upgradestat <kind> [on|off]; without on|off it toggles (the ModUI multi-select row sends just the kind)
                    string kind = val.ToLowerInvariant();
                    if (Array.IndexOf(UPG_KINDS, kind) < 0)
                    {
                        Reply(caller, $"[Buy] Upgradable stats on: {EnabledKinds()}. Usage: /buy upgradestat <{string.Join("|", UPG_KINDS)}> [on|off]");
                        break;
                    }
                    bool? want = ParseOnOff(parts.Length > 3 ? parts[3] : "");
                    bool now = want ?? !UpgStatEnabled(kind);
                    SetUpgStatEnabled(kind, now); SaveConfig();
                    Reply(caller, $"[Buy] Upgrade stat '{kind}' = {(now ? "ON" : "OFF")} (for new purchases; levels already bought stay). On: {EnabledKinds()}.");
                    break;
                }
                default:
                    Reply(caller, "/buy [status|on|off|enabled <on|off>|starter <amount>|give <player> <amount>|buildingreward <on|off>|structures <on|off>|adminonly <on|off>|bounty (on|off|list|add|remove|reload)" +
                                  "|upgrades <on|off>|upgradestep [<kind>] <percent|default>|upgrademax <n>|upgradecost <fraction>|upgrademincost <cr>|upgradeswapspeed <m/s>|upgradestat <stat> [on|off]|upgradeexclude [<stat> add|remove <name>]|upgrade dump]");
                    break;
            }
            return true;
        }

        static void ShowStatus(Player? caller)
        {
            string state = Cfg.Enabled ? "<color=#39ff14>ON</color>" : "<color=#ff5050>OFF</color>";
            Reply(caller, $"[Buy] Economy {state} | starter={Cfg.StarterCreditsPerPlayer}cr | kill reward={Cfg.RewardKillFraction:P0} of cost" +
                          $" | building reward={(Cfg.StructureKillRewardsEnabled ? "on" : "off")} | structures={(Cfg.BuyStructuresEnabled ? "on" : "off")}" +
                          $" | cooldown={Cfg.BuyCooldownSeconds:0}s | shrimp unlock={Cfg.ShrimpBuyUnlockSeconds:0}s | air unlock={Cfg.AirBuyUnlockSeconds:0}s" +
                          $" | bounties={(Cfg.BountiesEnabled ? _bountyCfg.Bounties.Count.ToString() : "off")}" +
                          $" | reset per round={(Cfg.RewardResetOnGameEnd ? "yes" : "no")}" +
                          $" | upgrades={(Cfg.UpgradesEnabled ? $"on (step {Cfg.UpgradeStepPercent:0.#}%/level{(StepOverrides().Length > 0 ? " [" + StepOverrides() + "]" : "")}, max {Cfg.UpgradeMaxPerUnit}, price x{Cfg.UpgradeCostFraction:0.##} min {Cfg.UpgradeMinCost}cr, refit below {Cfg.UpgradeMaxSwapSpeed:0.#} m/s, stats {EnabledKinds()}{(ExclusionSummary().Length > 0 ? ", excluded " + ExclusionSummary() : "")})" : "off")}");
            if (caller != null)
                Reply(caller, $"[Buy] Your balance: <color=#39ff14>{GetCredits(caller)}</color>cr");
        }

        /// <summary>Resolve "<steamId>" or a (prefix of a) player name to a connected player / Steam ID.</summary>
        static Player? FindPlayer(string key, out long steamId)
        {
            steamId = 0;
            if (string.IsNullOrEmpty(key)) return null;
            if (long.TryParse(key, out long parsed) && parsed > 0)
            {
                steamId = parsed;
                try
                {
                    foreach (var p in Player.Players)
                        if (p != null && GetPlayerSteamId(p) == parsed) return p;
                }
                catch { }
                return null;
            }
            try
            {
                var server = NetworkGameServer.GetServerPlayer();
                Player? best = null;
                foreach (var p in Player.Players)
                {
                    if (p == null || p == server) continue;
                    string name = p.PlayerName ?? "";
                    if (name.Equals(key, StringComparison.OrdinalIgnoreCase)) { best = p; break; }
                    if (best == null && name.StartsWith(key, StringComparison.OrdinalIgnoreCase)) best = p;
                }
                if (best == null)
                {
                    // unique substring (the Si_ModUI player picker sends the longest word of a name that no other name contains)
                    Player? only = null; int hits = 0;
                    foreach (var p in Player.Players)
                    {
                        if (p == null || p == server) continue;
                        if ((p.PlayerName ?? "").IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { only = p; hits++; }
                    }
                    if (hits == 1) best = only;
                }
                if (best != null) steamId = GetPlayerSteamId(best);
                return best;
            }
            catch { return null; }
        }

        /// <summary>
        /// Admin sub-commands under /buy bounty:
        ///   /buy bounty                        → list current bounties
        ///   /buy bounty list                   → same
        ///   /buy bounty reload                 → re-read Bounties.json from disk
        ///   /buy bounty add <steamId> <amt> [Name...]   → add or overwrite an entry
        ///   /buy bounty remove <steamId>       → remove an entry
        /// </summary>
        static void HandleBountySubcommand(Player? caller, string action, string[] parts)
        {
            action = (action ?? "").ToLowerInvariant();
            switch (action)
            {
                case "on":
                case "enable":
                    Cfg.BountiesEnabled = true; SaveConfig();
                    _announcedBounties.Clear();  // let announcements fire immediately
                    Reply(caller, "[Buy/Bounty] Bounties ENABLED.");
                    return;

                case "off":
                case "disable":
                    Cfg.BountiesEnabled = false; SaveConfig();
                    Reply(caller, "[Buy/Bounty] Bounties DISABLED. (Entries kept; re-enable with /buy bounty on.)");
                    return;

                case "":
                case "list":
                    string state = Cfg.BountiesEnabled ? "<color=#39ff14>ON</color>" : "<color=#ff5050>OFF</color>";
                    if (_bountyCfg.Bounties.Count == 0)
                    {
                        Reply(caller, $"[Buy/Bounty] {state}. No bounties active.");
                        return;
                    }
                    Reply(caller, $"[Buy/Bounty] {state} — {_bountyCfg.Bounties.Count} active (default {_bountyCfg.DefaultAmount}cr):");
                    foreach (var kv in _bountyCfg.Bounties)
                    {
                        int amt = kv.Value.Amount > 0 ? kv.Value.Amount : _bountyCfg.DefaultAmount;
                        string nm = string.IsNullOrEmpty(kv.Value.Name) ? "(no name)" : kv.Value.Name;
                        Reply(caller, $"  {kv.Key} — {nm} — {amt}cr");
                    }
                    return;

                case "reload":
                    LoadBountyConfig();
                    _announcedBounties.Clear();
                    Reply(caller, $"[Buy/Bounty] Reloaded. {_bountyCfg.Bounties.Count} active.");
                    return;

                case "add":
                {
                    long addSid = 0; string pickedName = "";
                    if (parts.Length >= 4 && !long.TryParse(parts[3], out addSid))
                    {
                        var target = FindPlayer(parts[3], out addSid);   // a connected player's name works as well as the Steam ID
                        if (target != null) pickedName = target.PlayerName ?? "";
                    }
                    if (parts.Length < 4 || addSid <= 0)
                    {
                        Reply(caller, "[Buy/Bounty] Usage: /buy bounty add <steamId|player> [amount] [name...]");
                        return;
                    }
                    // Re-read file first so any manual JSON edits (e.g. names typed in by hand)
                    // aren't clobbered when we serialize our in-memory dict back to disk.
                    LoadBountyConfig();

                    // Parse the optional amount WITHOUT letting int.TryParse zero out the
                    // default on a non-numeric arg (e.g. "/buy bounty add <sid> Dram" — Dram
                    // is a name, not an amount, and falls through to the name field).
                    int addAmount = _bountyCfg.DefaultAmount;
                    int nameStartIdx = 4;
                    if (parts.Length >= 5 && int.TryParse(parts[4], out int parsedAmount))
                    {
                        addAmount = parsedAmount;
                        nameStartIdx = 5;
                    }
                    string addName = parts.Length > nameStartIdx
                        ? string.Join(" ", parts, nameStartIdx, parts.Length - nameStartIdx)
                        : "";

                    // Preserve existing name/note if the caller didn't pass new ones.
                    string finalName = addName.Length > 0 ? addName : pickedName;
                    string finalNote = "";
                    if (_bountyCfg.Bounties.TryGetValue(addSid, out var existing))
                    {
                        if (string.IsNullOrEmpty(finalName)) finalName = existing.Name ?? "";
                        finalNote = existing.Note ?? "";
                    }
                    _bountyCfg.Bounties[addSid] = new BountyEntry { Name = finalName, Amount = addAmount, Note = finalNote };
                    SaveBountyConfig();
                    _announcedBounties.Remove(addSid); // let the announcement fire if they're already connected
                    Reply(caller, $"[Buy/Bounty] Added: {addSid} — {(string.IsNullOrEmpty(finalName) ? "(no name)" : finalName)} — {addAmount}cr");
                    return;
                }

                case "remove":
                case "rm":
                case "del":
                {
                    long rmSid = 0;
                    if (parts.Length >= 4 && !long.TryParse(parts[3], out rmSid))
                    {
                        FindPlayer(parts[3], out rmSid);   // connected player's name
                        if (rmSid <= 0)
                        {
                            LoadBountyConfig();
                            string needle = string.Join(" ", parts, 3, parts.Length - 3);
                            foreach (var kv in _bountyCfg.Bounties)
                                if (string.Equals(kv.Value.Name ?? "", needle, StringComparison.OrdinalIgnoreCase)) { rmSid = kv.Key; break; }
                        }
                    }
                    if (parts.Length < 4 || rmSid <= 0)
                    {
                        Reply(caller, "[Buy/Bounty] Usage: /buy bounty remove <steamId|name>");
                        return;
                    }
                    // Re-read file first so we operate on the current on-disk state and
                    // don't overwrite manual edits made between commands.
                    LoadBountyConfig();
                    if (!_bountyCfg.Bounties.Remove(rmSid))
                    {
                        Reply(caller, $"[Buy/Bounty] No entry for {rmSid}.");
                        return;
                    }
                    SaveBountyConfig();
                    _announcedBounties.Remove(rmSid);
                    Reply(caller, $"[Buy/Bounty] Removed: {rmSid}");
                    return;
                }

                default:
                    Reply(caller, "[Buy/Bounty] Usage: /buy bounty [on|off|list|reload|add <steamId> <amount> [name]|remove <steamId>]");
                    return;
            }
        }

        // ----- helpers -----

        static void Reply(Player? caller, string msg)
        {
            if (caller != null) HelperMethods.SendChatMessageToPlayer(caller, msg);
            MelonLogger.Msg(msg);
        }

        /// <summary>
        /// Parses "on"/"off"/"1"/"0"/"true"/"false"/"yes"/"no" to bool. Returns null if not recognised.
        /// Used by admin toggle commands so an empty arg shows the current state instead of flipping.
        /// </summary>
        static bool? ParseOnOff(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return null;
            string a = arg.Trim().ToLowerInvariant();
            if (a == "on" || a == "1" || a == "true"  || a == "yes" || a == "y") return true;
            if (a == "off" || a == "0" || a == "false" || a == "no"  || a == "n") return false;
            return null;
        }

        // Server-wide chat broadcast — loops Player.Players and sends per-player.
        // Skips the server's own ghost player.
        static void BroadcastAllChat(string msg)
        {
            try
            {
                var serverPlayer = NetworkGameServer.GetServerPlayer();
                for (int i = 0; i < Player.Players.Count; i++)
                {
                    var p = Player.Players[i];
                    if (p != null && p != serverPlayer)
                        HelperMethods.SendChatMessageToPlayer(p, msg);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] BroadcastAllChat failed: {ex.Message}");
            }
            MelonLogger.Msg(msg);
        }

        static bool TryParseFloat(string s, out float val)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }
    }
}
