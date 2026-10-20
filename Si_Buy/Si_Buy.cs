/*
 Si_Buy — per-player credit economy + /buy purchase menu for Silica dedicated servers.

 Carved out of Si_KingOfTheHill (KGT) v0.4 on 2026-10-17 so the economy can run on
 its own and other mods can pay or charge credits through one small API:

   Si_Buy.Buy.BuyActive                                   (bool)  economy switched on?
   Si_Buy.Buy.GrantExternalCredits(long steamId, int amount, string reason) -> bool
   Si_Buy.Buy.SpendCredits(long steamId, int amount, string reason)         -> bool
   Si_Buy.Buy.GetCreditsBySteamId(long steamId)                             -> int
   Si_Buy.Buy.GetCredits(Player player)                                     -> int

 Consumers resolve the type by name via reflection (no compile-time dependency):
   AppDomain.CurrentDomain.GetAssemblies() ... asm.GetType("Si_Buy.Buy")
 The method names and primitive signatures are the same ones Si_KingOfTheHill
 exposed before the split, so existing bridges only need the type name changed.

 Credit sources: starter credits, kill rewards, structure-kill rewards, headhunter
 bounties, and anything another mod grants (Si_Logistics delivery bonus,
 Si_KingOfTheHill zone presence). Credit sink: /buy.

 Server-only mod (no client component required).
 Deploy: <server>\Mods\Si_Buy.dll
 Config: UserData\cfg\Buy\Si_Buy_Config.json  (+ Bounties.json next to it)
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_Buy.Buy), "Buy", "1.2.1", "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod", "Si_UnitBalance")]

namespace Si_Buy
{
    public partial class Buy : MelonMod
    {
        /// <summary>Economy master switch (config Enabled). All public API calls return false / 0 while off.</summary>
        public static bool BuyActive => Cfg != null && Cfg.Enabled;

        // Time.time stamp of the current round's start. Used to gate time-locked
        // purchases (Shrimp buy unlock). 0 = no round started yet (fail-open).
        internal static float _roundStartTime = 0f;

        // Starter credits tracking (per Steam ID, per round). Cleared on game end.
        static readonly HashSet<long> _starterCreditsGranted = new HashSet<long>();
        // Periodic poll for starter credits to mid-round joiners + bounty announcements.
        static float _starterCreditsCheckTimer = 0f;
        const float STARTER_CREDITS_CHECK_INTERVAL = 5.0f;

        public override void OnInitializeMelon()
        {
            _configPath = Path.Combine(Paths.ConfigDir, "Si_Buy_Config.json");
            LoadConfig();

            // Reward system (kill events).
            HookRewards();

            // /buy menu /1-/N chat navigation.
            HookBuyChat();

            // Headhunter bounty list (separate JSON next to the config).
            LoadBountyConfig();

            MelonLogger.Msg($"[Buy] Si_Buy v1.2.1 loaded. Enabled={Cfg.Enabled} starter={Cfg.StarterCreditsPerPlayer}cr " +
                            $"killFraction={Cfg.RewardKillFraction:0.##} bounties={Cfg.BountiesEnabled} structures={Cfg.BuyStructuresEnabled}");
        }

        public override void OnLateInitializeMelon()
        {
            // /buy registered as a player command (hidden from chat). Admin-only sub-actions
            // are gated inside the handler via caller.CanAdminExecute(Power.Generic).
            PlayerMethods.RegisterPlayerCommand("buy", OnBuyCommand, true);
            MelonLogger.Msg("[Buy] Registered /buy command.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // GameEvents.* gets cleared across scene transitions — re-hook.
            GameEvents.OnGameStarted -= OnGameStarted;
            GameEvents.OnGameStarted += OnGameStarted;
            GameEvents.OnGameEnded -= OnGameEnded;
            GameEvents.OnGameEnded += OnGameEnded;
            ReHookRewards();
            ReHookBuyChat();
        }

        static void OnGameStarted(GameMode mode)
        {
            // Stamp round start for time-locked purchases (Shrimp unlock, etc.).
            _roundStartTime = Time.time;

            // Always load the dump — /buy prices and kill rewards need it regardless of Enabled
            // so a mid-round /buy on works without a restart.
            LoadUnitDump();
            // Apply Si_UnitBalance's cost_mult overrides on top of the vanilla dump snapshot.
            // Reading ConstructionData.ResourceCost would return vanilla (OM is a runtime
            // overlay, not a field mutation), so we parse Si_UnitBalance_Config.json instead.
            // Re-applied each round start so live config edits take effect on next round.
            ApplyUnitBalanceCostOverrides();

            _starterCreditsGranted.Clear();
            _starterCreditsCheckTimer = STARTER_CREDITS_CHECK_INTERVAL;
            if (!BuyActive) return;

            MelonLogger.Msg($"[Buy] OnGameStarted — map={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
            if (Cfg.StarterCreditsPerPlayer > 0) GrantStarterCreditsToAll();
        }

        static void OnGameEnded(GameMode mode, Team winner)
        {
            // Reset credits between rounds if configured.
            if (Cfg.RewardResetOnGameEnd) ResetAllCredits();
            // Clear any active buy menus so they don't carry stale state across rounds.
            ResetBuyState();
            // Re-announce bounties at the start of the next round.
            _announcedBounties.Clear();
            MelonLogger.Msg("[Buy] OnGameEnded — menus cleared" + (Cfg.RewardResetOnGameEnd ? ", credits reset." : "."));
        }

        public override void OnUpdate()
        {
            if (!NetworkGameServer.GetServerStarted()) return;
            if (!BuyActive) return;

            // Starter-credits + bounty-announcement poll. Both walk Player.Players,
            // so we cycle them on the same 5-second timer.
            _starterCreditsCheckTimer -= Time.deltaTime;
            if (_starterCreditsCheckTimer <= 0f)
            {
                _starterCreditsCheckTimer = STARTER_CREDITS_CHECK_INTERVAL;
                if (Cfg.StarterCreditsPerPlayer > 0) GrantStarterCreditsToAll();
                AnnounceBountiesIfNeeded();
            }
        }

        // ----- small shared helpers -----

        static long GetPlayerSteamId(Player p)
        {
            // NetworkID has m_ID (ulong) — same as the Steam ID bits.
            try { return (long)p.PlayerID.m_ID; }
            catch { return 0; }
        }

        /// <summary>Wildlife / Gamemaster / Worm teams: never a valid buyer or a blocker for spawn checks.</summary>
        static bool IsGamemasterTeam(Team? team)
        {
            if (team == null) return false;
            string s = "";
            try { s = team.TeamShortName ?? ""; } catch { }
            string g = "";
            try { g = team.name ?? ""; } catch { }
            return s == "Wildlife" || s == "GM" || s == "Master" || s == "Worm"
                || g.IndexOf("Wildlife", StringComparison.OrdinalIgnoreCase) >= 0
                || g.IndexOf("Master", StringComparison.OrdinalIgnoreCase) >= 0
                || g.IndexOf("Worm", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
