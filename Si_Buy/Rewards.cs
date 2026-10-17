/*
 Rewards.cs — per-player credit ledger, the cross-mod API, starter credits and
 kill rewards.

 Credit sources handled here:
   1. Kill reward: when a player kills an enemy unit, +round(victim.cost * RewardKillFraction)
   2. Structure-kill reward: same for enemy structures (StructureKillRewardsEnabled)
   3. Starter credits: Cfg.StarterCreditsPerPlayer once per player per round
   4. GrantExternalCredits: other mods (Si_Logistics delivery bonus, Si_KingOfTheHill
      zone presence, ...) pay credits through the public API below.

 Credit sink: /buy (see Buy.cs) and SpendCredits for other mods.

 Credits are keyed by Steam ID (long) for stable identity across reconnects.
 Reset between rounds when Cfg.RewardResetOnGameEnd = true (default).
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_Buy
{
    public partial class Buy
    {
        // Steam ID -> credit balance. Cleared on round end if Cfg.RewardResetOnGameEnd.
        static readonly Dictionary<long, int> _playerCredits = new Dictionary<long, int>();

        static bool _rewardsHooked;

        // ----- Public accessors -----

        public static int GetCredits(Player player)
        {
            if (player == null) return 0;
            long key = GetPlayerSteamId(player);
            return _playerCredits.TryGetValue(key, out int c) ? c : 0;
        }

        static int GetCredits(long steamId)
            => _playerCredits.TryGetValue(steamId, out int c) ? c : 0;

        static void AddCredits(long steamId, int delta)
        {
            if (delta == 0) return;
            int now = GetCredits(steamId) + delta;
            if (now < 0) now = 0;
            _playerCredits[steamId] = now;
        }

        // ----- Cross-mod API (reflection-friendly) -----
        // Other mods call these via reflection, no compile-time dependency:
        //   asm.GetType("Si_Buy.Buy")
        //      .GetMethod("GrantExternalCredits", Public|Static, null, new[]{typeof(long),typeof(int),typeof(string)}, null)
        // Primitive parameters keep the reflected signature independent of game types.
        // All of them are inert (false / 0) while the economy is switched off.

        /// <summary>Pay credits to a player. False (and nothing credited) while the economy is off or the input is invalid.</summary>
        public static bool GrantExternalCredits(long steamId, int amount, string reason)
        {
            if (!BuyActive || steamId == 0 || amount <= 0) return false;
            AddCredits(steamId, amount);
            MelonLogger.Msg($"[Buy] +{amount}cr -> {steamId} ({reason ?? "external"}). Total: {GetCredits(steamId)}cr");
            return true;
        }

        /// <summary>Charge credits. False when the economy is off, the input is invalid, or the balance is too low (nothing is deducted then).</summary>
        public static bool SpendCredits(long steamId, int amount, string reason)
        {
            if (!BuyActive || steamId == 0 || amount <= 0) return false;
            if (GetCredits(steamId) < amount) return false;
            AddCredits(steamId, -amount);
            MelonLogger.Msg($"[Buy] -{amount}cr <- {steamId} ({reason ?? "external"}). Total: {GetCredits(steamId)}cr");
            return true;
        }

        public static int GetCreditsBySteamId(long steamId)
            => BuyActive ? GetCredits(steamId) : 0;

        static void ResetAllCredits()
        {
            _playerCredits.Clear();
        }

        /// <summary>
        /// Grant Cfg.StarterCreditsPerPlayer to each connected player who hasn't yet
        /// received it this round. Single-shot per Steam ID (tracked in
        /// _starterCreditsGranted), so calling repeatedly from OnUpdate is safe.
        /// </summary>
        static void GrantStarterCreditsToAll()
        {
            int amount = Cfg.StarterCreditsPerPlayer;
            if (amount <= 0) return;
            try
            {
                var players = Player.Players;
                if (players == null) return;
                int granted = 0;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null) continue;
                    // Skip the server's ghost player.
                    if (p == NetworkGameServer.GetServerPlayer()) continue;
                    long sid = GetPlayerSteamId(p);
                    if (sid == 0) continue;
                    if (!_starterCreditsGranted.Add(sid)) continue;  // already received
                    AddCredits(sid, amount);
                    granted++;
                    try
                    {
                        HelperMethods.SendChatMessageToPlayer(p,
                            $"[Buy] Welcome — starter credits: <color=#39ff14>{amount}</color>cr. Type /buy to spend.");
                    }
                    catch { }
                }
                if (granted > 0)
                    MelonLogger.Msg($"[Buy] Starter credits: granted {amount}cr to {granted} player(s)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] GrantStarterCreditsToAll threw: {ex.Message}");
            }
        }

        // ----- Event hookup -----

        static void HookRewards()
        {
            if (_rewardsHooked) return;
            try
            {
                GameEvents.OnUnitDestroyed -= OnUnitDestroyed_Reward;
                GameEvents.OnUnitDestroyed += OnUnitDestroyed_Reward;
                GameEvents.OnStructureDestroyed -= OnStructureDestroyed_Reward;
                GameEvents.OnStructureDestroyed += OnStructureDestroyed_Reward;
                _rewardsHooked = true;
                MelonLogger.Msg("[Buy] Rewards: subscribed to OnUnitDestroyed + OnStructureDestroyed");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] HookRewards failed: {ex.Message}");
            }
        }

        // GameEvents clear on scene transition — call from OnSceneWasLoaded too.
        static void ReHookRewards()
        {
            _rewardsHooked = false;
            HookRewards();
        }

        // ----- Kill reward -----

        static void OnUnitDestroyed_Reward(Unit unit, GameObject instigator)
        {
            try
            {
                if (!BuyActive || unit == null || unit.ObjectInfo == null || instigator == null) return;

                // Walk instigator -> BaseGameObject -> OwnerPlayer
                BaseGameObject? attacker = GameFuncs.GetBaseGameObject(instigator);
                Player? killer = attacker?.NetworkComponent?.OwnerPlayer;
                if (killer == null) return;

                // No reward for self-kill / friendly fire.
                if (unit.Team != null && killer.Team != null && unit.Team == killer.Team) return;

                if (!TryGetEffectiveCost(unit.ObjectInfo, out int cost, out string victimName)) return;

                int reward = Mathf.RoundToInt(cost * Cfg.RewardKillFraction);
                if (reward <= 0) return;

                long key = GetPlayerSteamId(killer);
                AddCredits(key, reward);
                try
                {
                    HelperMethods.SendChatMessageToPlayer(killer,
                        $"[Buy] +<color=#39ff14>{reward}</color>cr (killed {victimName}). Total: {GetCredits(key)}cr");
                }
                catch { }

                // Headhunter bounty bonus — paid on top of the normal kill reward.
                PayBountyIfApplicable(unit, killer);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] OnUnitDestroyed_Reward threw: {ex.Message}");
            }
        }

        // ----- Structure-kill reward (mirror of unit-kill, separate event) -----

        static void OnStructureDestroyed_Reward(Structure structure, GameObject instigator)
        {
            try
            {
                if (!BuyActive || !Cfg.StructureKillRewardsEnabled || structure == null || structure.ObjectInfo == null || instigator == null) return;

                BaseGameObject? attacker = GameFuncs.GetBaseGameObject(instigator);
                Player? killer = attacker?.NetworkComponent?.OwnerPlayer;
                if (killer == null) return;

                // No reward for destroying own/allied structure.
                if (structure.Team != null && killer.Team != null && structure.Team == killer.Team) return;

                if (!TryGetEffectiveCost(structure.ObjectInfo, out int cost, out string victimName)) return;

                int reward = Mathf.RoundToInt(cost * Cfg.RewardKillFraction);
                if (reward <= 0) return;

                long key = GetPlayerSteamId(killer);
                AddCredits(key, reward);
                try
                {
                    HelperMethods.SendChatMessageToPlayer(killer,
                        $"[Buy] +<color=#39ff14>{reward}</color>cr (destroyed {victimName}). Total: {GetCredits(key)}cr");
                }
                catch { }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Buy] OnStructureDestroyed_Reward threw: {ex.Message}");
            }
        }
    }
}
