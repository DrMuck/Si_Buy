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
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.Globalization;

namespace Si_Buy
{
    public partial class Buy
    {
        static readonly string[] ADMIN_SUBS =
            { "status", "on", "off", "starter", "give", "buildingreward", "structurereward", "structures", "buystructures", "buybuildings", "adminonly", "bounty", "help" };

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
                default:
                    Reply(caller, "/buy [status|on|off|starter <amount>|give <player> <amount>|buildingreward <on|off>|structures <on|off>|adminonly <on|off>|bounty (on|off|list|add|remove|reload)]");
                    break;
            }
            return true;
        }

        static void ShowStatus(Player? caller)
        {
            string state = Cfg.Enabled ? "<color=#39ff14>ON</color>" : "<color=#ff5050>OFF</color>";
            Reply(caller, $"[Buy] Economy {state} | starter={Cfg.StarterCreditsPerPlayer}cr | kill reward={Cfg.RewardKillFraction:P0} of cost" +
                          $" | building reward={(Cfg.StructureKillRewardsEnabled ? "on" : "off")} | structures={(Cfg.BuyStructuresEnabled ? "on" : "off")}" +
                          $" | cooldown={Cfg.BuyCooldownSeconds:0}s | shrimp unlock={Cfg.ShrimpBuyUnlockSeconds:0}s" +
                          $" | bounties={(Cfg.BountiesEnabled ? _bountyCfg.Bounties.Count.ToString() : "off")}" +
                          $" | reset per round={(Cfg.RewardResetOnGameEnd ? "yes" : "no")}");
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
                    if (parts.Length < 4 || !long.TryParse(parts[3], out long addSid))
                    {
                        Reply(caller, "[Buy/Bounty] Usage: /buy bounty add <steamId> <amount> [name...]");
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
                    string finalName = addName;
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
                    if (parts.Length < 4 || !long.TryParse(parts[3], out long rmSid))
                    {
                        Reply(caller, "[Buy/Bounty] Usage: /buy bounty remove <steamId>");
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
