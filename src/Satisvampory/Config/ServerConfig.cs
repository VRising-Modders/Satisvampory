// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Plugins;
using Satisvampory.Engine.Persistence;

namespace Satisvampory.Config
{
    /// <summary>
    /// The Server page (spec 10): every allow as a synced config entry. The server's value wins
    /// on every client while joined; admins edit it in-game from the settings page.
    /// </summary>
    public sealed class ServerConfig
    {
        const string Section = "Server";

        readonly List<Action<ServerSettings>> _apply = new();
        readonly List<Action<ServerSettings>> _write = new();

        public List<(ConfigEntryBase Entry, string Label, string Help)> Rows { get; } = new();

        /// <summary>Synced: admin's extra item aliases.</summary>
        public ConfigEntry<string> Aliases { get; private set; } = null!;
        /// <summary>Local only: what Give to storage gives.</summary>
        public ConfigEntry<string> GiveItem { get; private set; } = null!;
        public ConfigEntry<int> GiveAmount { get; private set; } = null!;

        ServerConfig()
        {
        }

        public static ServerConfig Bind(PluginContext ctx)
        {
            var c = new ServerConfig();
            var d = new ServerSettings();

            c.Flag(ctx, "CastleShare", d.CastleShare, "Castle share", "A player's own castles act as one island.", (s, v) => s.CastleShare = v);
            c.Flag(ctx, "PlayersMayLeaveCastleShare", d.PlayersMayLeaveCastleShare, "Players may turn castle share off", "Off locks every player's castle share on.", (s, v) => s.PlayersMayLeaveCastleShare = v);
            c.Flag(ctx, "ClanShare", d.ClanShare, "Clan share", "Every taking-part clan member's castles act as one island.", (s, v) => s.ClanShare = v);
            c.Flag(ctx, "PlayersMayLeaveClanShare", d.PlayersMayLeaveClanShare, "Players may turn clan share off", "Off locks every player's clan share on.", (s, v) => s.PlayersMayLeaveClanShare = v);
            c.Flag(ctx, "AllowLogistics", d.AllowLogistics, "Allow input and output lists", "Off stops every conveyor on the server.", (s, v) => s.AllowLogistics = v);
            c.Flag(ctx, "AllowLoops", d.AllowLoops, "Allow loops", "Off: the edge that closes a loop never runs.", (s, v) => s.AllowLoops = v);
            c.Int(ctx, "LoopMovesPerTick", d.LoopMovesPerTick, 1, 120, "Loop moves per tick", "Moves one loop may make each tick.", (s, v) => s.LoopMovesPerTick = v);
            c.Int(ctx, "MovesPerPlot", d.MovesPerPlot, 1, 200, "Moves per plot per tick", "Conveyor budget for one plot.", (s, v) => s.MovesPerPlot = v);
            c.Int(ctx, "MovesPerTick", d.MovesPerTick, 10, 2000, "Moves per tick", "Conveyor budget for the whole server.", (s, v) => s.MovesPerTick = v);
            c.Flag(ctx, "AllowQuickStash", d.AllowQuickStash, "Allow quick stash", "Double-tap sort, or the Stash button.", (s, v) => s.AllowQuickStash = v);
            c.Flag(ctx, "AllowStashFromAnywhere", d.AllowStashFromAnywhere, "Allow stash from anywhere", "Players may stash while off every plot.", (s, v) => s.AllowStashFromAnywhere = v);
            c.Flag(ctx, "AllowPull", d.AllowPull, "Allow pull", "Players may take items from storage into bags.", (s, v) => s.AllowPull = v);
            c.Flag(ctx, "AllowCraftFromStorage", d.AllowCraftFromStorage, "Allow craft from storage", "Crafting and building use chests on the island.", (s, v) => s.AllowCraftFromStorage = v);
            c.Flag(ctx, "AllowServantLootStash", d.AllowServantLootStash, "Allow servant loot stash", "Returning servants' loot goes home.", (s, v) => s.AllowServantLootStash = v);
            c.Flag(ctx, "AllowSalvage", d.AllowSalvage, "Allow salvage chests", "Salvage chests feed the devourer.", (s, v) => s.AllowSalvage = v);
            c.Flag(ctx, "AllowSpawnerChests", d.AllowSpawnerChests, "Allow spawner chests", "Spawner chests feed unit spawners.", (s, v) => s.AllowSpawnerChests = v);
            c.Flag(ctx, "AllowTrash", d.AllowTrash, "Allow trash chests", "Trash chests can be emptied.", (s, v) => s.AllowTrash = v);
            c.Flag(ctx, "AllowHeartBloodFeed", d.AllowHeartBloodFeed, "Allow heart blood feed", "Hearts are topped up on visit or login.", (s, v) => s.AllowHeartBloodFeed = v);
            c.Flag(ctx, "AllowRepeatHunts", d.AllowRepeatHunts, "Allow repeat hunts", "Servants go back out on the same hunt.", (s, v) => s.AllowRepeatHunts = v);
            c.Flag(ctx, "RepeatHuntsOnlyWhileClanOnline", d.RepeatHuntsOnlyWhileClanOnline, "Repeat hunts only while a clan member is online", "", (s, v) => s.RepeatHuntsOnlyWhileClanOnline = v);
            c.Int(ctx, "RepeatHuntSuccessCap", d.RepeatHuntSuccessCap, 1, 100, "Repeat hunt success cap (shown)", "Shown in notices; the roll stays vanilla.", (s, v) => s.RepeatHuntSuccessCap = v);
            c.Aliases = ctx.Bind(Section, "ItemAliases", d.ItemAliases, "Extra item aliases, e.g. ior = Iron Ore; rb = Ruby", synced: true);
            c.Rows.Add((c.Aliases, "Item aliases (server)", "Built-ins (be gbe pbe abe gss sgs dsi ot) always work. Add more as alias = item; separate with ; or new lines."));
            c._apply.Add(s => s.ItemAliases = c.Aliases.Value);
            c.GiveItem = ctx.Bind("AdminTools", "GiveItem", "", "Item for Give to storage (local, admins only)");
            c.GiveAmount = ctx.Bind("AdminTools", "GiveAmount", 100, "Amount for Give to storage (local, admins only)", acceptableValues: new AcceptableValueRange<int>(1, 100000));
            c.Flag(ctx, "CleanNamesAfterImport", d.CleanNamesAfterImport, "Clean names after 1.x import", "Remove converted codes like s1 or ++ from chest names.", (s, v) => s.CleanNamesAfterImport = v);
            return c;
        }

        /// <summary>Set every entry from a settings object (the import).</summary>
        public void Write(ServerSettings s)
        {
            foreach (var w in _write) w(s);
        }

        /// <summary>The current values as the engine's settings object.</summary>
        public ServerSettings Snapshot()
        {
            var s = new ServerSettings();
            foreach (var a in _apply) a(s);
            return s;
        }

        void Flag(PluginContext ctx, string key, bool value, string label, string help, Action<ServerSettings, bool> set)
        {
            var e = ctx.Bind(Section, key, value, help, synced: true);
            Rows.Add((e, label, help));
            _apply.Add(s => set(s, e.Value));
            var getter = typeof(ServerSettings).GetProperty(key);
            if (getter != null) _write.Add(s => e.Value = (bool)getter.GetValue(s)!);
        }

        void Int(PluginContext ctx, string key, int value, int min, int max, string label, string help, Action<ServerSettings, int> set)
        {
            var e = ctx.Bind(Section, key, value, help, synced: true, acceptableValues: new AcceptableValueRange<int>(min, max));
            Rows.Add((e, label, help));
            _apply.Add(s => set(s, e.Value));
            var getter = typeof(ServerSettings).GetProperty(key);
            if (getter != null) _write.Add(s => e.Value = Math.Clamp((int)getter.GetValue(s)!, min, max));
        }
    }
}
