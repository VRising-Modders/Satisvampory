// SPDX-License-Identifier: AGPL-3.0-only
namespace Satisvampory.Engine.Persistence
{
    /// <summary>Me page (spec 10). Defaults are the product defaults.</summary>
    public sealed class PlayerSettings
    {
        public bool StashOnDoubleSort { get; set; } = true;
        public bool StashFromAnywhere { get; set; }
        public bool SilentStash { get; set; }
        public bool CraftFromStorage { get; set; } = true;
        public bool NeverPullLastStack { get; set; } = true;
        public bool SilentPull { get; set; }
        public bool ServantLootStash { get; set; } = true;
        public bool TidyConfirm { get; set; } = true;
        public bool ShareBetweenMyCastles { get; set; } = true;
        public bool TakePartInClanShare { get; set; } = true;
        /// <summary>My servants go back out on the same hunt (when the server allows repeat hunts).</summary>
        public bool RepeatMyHunts { get; set; } = true;
        /// <summary>The server log lists what my stash and tidy moved, item by item and chest by chest.</summary>
        public bool LogMyDeposits { get; set; } = true;
        public bool ShowTermHints { get; set; } = true;
    }

    /// <summary>Castle page (spec 10), stored on the heart.</summary>
    public sealed class CastleSettings
    {
        public string PlotLabel { get; set; } = string.Empty;
        public bool LogisticsOn { get; set; } = true;
        public bool FeedSalvage { get; set; }
        public bool HeartAutoFeed { get; set; } = true;
        public bool RepeatHunts { get; set; } = true;
        public bool ExcludeFromSharing { get; set; }
    }

    /// <summary>Server page (spec 10). Every allow defaults on except repeat hunts and unmodded clients.</summary>
    public sealed class ServerSettings
    {
        public bool CastleShare { get; set; } = true;
        public bool PlayersMayLeaveCastleShare { get; set; } = true;
        public bool ClanShare { get; set; } = true;
        public bool PlayersMayLeaveClanShare { get; set; } = true;
        public bool AllowQuickStash { get; set; } = true;
        public bool AllowStashFromAnywhere { get; set; } = true;
        public bool AllowPull { get; set; } = true;
        public bool AllowCraftFromStorage { get; set; } = true;
        public bool AllowServantLootStash { get; set; } = true;
        public bool AllowLogistics { get; set; } = true;
        public bool AllowLoops { get; set; } = true;
        public int LoopMovesPerTick { get; set; } = 12;
        public bool AllowSalvage { get; set; } = true;
        public bool AllowSpawnerChests { get; set; } = true;
        public bool AllowTrash { get; set; } = true;
        public bool AllowHeartBloodFeed { get; set; } = true;
        /// <summary>How hearts are topped up (admin only): on visiting a plot, at login, and beyond the island.</summary>
        public bool HeartTopUpOnVisit { get; set; } = true;
        public bool HeartTopUpIslandOnLogin { get; set; }
        public bool HeartTopUpOutsideIsland { get; set; }
        public bool AllowRepeatHunts { get; set; }
        public bool RepeatHuntsOnlyWhileClanOnline { get; set; }
        public int RepeatHuntSuccessCap { get; set; } = 99;
        public int MovesPerPlot { get; set; } = 12;
        public int MovesPerTick { get; set; } = 120;
        public bool AllowUnmoddedClients { get; set; }
        /// <summary>Admin extras on top of the built-in aliases: "ior = Iron Ore; rb = Ruby".</summary>
        public string ItemAliases { get; set; } = "";
        public bool CleanNamesAfterImport { get; set; }
        public bool ImportDone { get; set; }
    }
}
