// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Plugins;
using Satisvampory.Engine.Persistence;

namespace Satisvampory.Config
{
    /// <summary>
    /// The Me page (spec 10). Stored in each player's own config and sent to the server as a
    /// bitfield (<see cref="PlayerPrefsPacket"/>), because Progenitor has no server-read
    /// per-player settings yet.
    /// </summary>
    public sealed class PlayerConfig
    {
        const string Section = "Me";

        readonly List<(ConfigEntry<bool> Entry, int Bit)> _flags = new();

        public List<(ConfigEntry<bool> Entry, string Label, string Help)> Rows { get; } = new();

        /// <summary>Raised on the client when any Me setting changes.</summary>
        public event Action? Changed;

        PlayerConfig()
        {
        }

        public static PlayerConfig Bind(PluginContext ctx)
        {
            var c = new PlayerConfig();
            var d = new PlayerSettings();
            c.Flag(ctx, 0, "ShareBetweenMyCastles", d.ShareBetweenMyCastles, "Share between my castles", "My castles act as one: craft at one, use chests at another.");
            c.Flag(ctx, 1, "TakePartInClanShare", d.TakePartInClanShare, "Take part in clan share", "My castles join the clan island. Off: I neither give nor take.");
            c.Flag(ctx, 2, "StashOnDoubleSort", d.StashOnDoubleSort, "Quick stash on double-tap sort", "Sort twice within a second to stash.");
            c.Flag(ctx, 3, "StashFromAnywhere", d.StashFromAnywhere, "Stash from anywhere", "Stash even when off every plot (if the server allows).");
            c.Flag(ctx, 4, "SilentStash", d.SilentStash, "Silent stash", "No stash summary notice.");
            c.Flag(ctx, 5, "CraftFromStorage", d.CraftFromStorage, "Craft from storage", "Crafting and building use chests on my island.");
            c.Flag(ctx, 6, "NeverPullLastStack", d.NeverPullLastStack, "Never pull the last stack", "Pulls leave each chest's last stack.");
            c.Flag(ctx, 7, "SilentPull", d.SilentPull, "Silent pull", "No pull summary notice.");
            c.Flag(ctx, 8, "ServantLootStash", d.ServantLootStash, "Servant loot auto-stash", "Loot from my servants goes home.");
            c.Flag(ctx, 9, "TidyConfirm", d.TidyConfirm, "Tidy needs a second press", "Press the tidy key twice within three seconds.");
            c.Flag(ctx, 10, "HeartTopUpOnVisit", d.HeartTopUpOnVisit, "Top up hearts when I visit", "Entering one of my or my clan's plots tops up its heart.");
            c.Flag(ctx, 11, "HeartTopUpIslandOnLogin", d.HeartTopUpIslandOnLogin, "Top up my island's hearts at login", "Every heart on my island, once per login.");
            c.Flag(ctx, 12, "HeartTopUpOutsideIsland", d.HeartTopUpOutsideIsland, "Also top up hearts outside my island", "Plots of mine or my clan that are not on my island.");
            c.Flag(ctx, 13, "ShowTermHints", d.ShowTermHints, "Show term hints (ⓘ)", "Small markers that explain Belt, Category and the rest.");
            var redistribution = ctx.Bind(Section, "ShowRedistribution", false, "Show the game redistribution tab beside Production, Input and Output.");
            redistribution.SettingChanged += (_, _) => c.Changed?.Invoke();
            c.Rows.Add((redistribution, "Show vanilla redistribution", "Show the game redistribution tab. Existing game routes remain under your control."));
            var autoPin = ctx.Bind(Section, "AutoPinFind", true, "Automatically pin the chest or station selected in Find.");
            autoPin.SettingChanged += (_, _) => c.Changed?.Invoke();
            c.Rows.Add((autoPin, "Auto-pin Find selections", "Selecting a chest or station in Find pins its location for one minute or until you get close. Replaces the previous Find pin."));
            return c;
        }

        public bool ShowRedistribution => Rows.Find(r => r.Entry.Definition.Key == "ShowRedistribution").Entry?.Value ?? false;
        public bool AutoPinFind => Rows.Find(r => r.Entry.Definition.Key == "AutoPinFind").Entry?.Value ?? true;

        public uint Bits()
        {
            uint b = 0;
            foreach (var (e, bit) in _flags)
                if (e.Value) b |= 1u << bit;
            return b;
        }

        /// <summary>Server side: rebuild a player's settings from the bits they sent.</summary>
        public static PlayerSettings FromBits(uint b)
        {
            bool On(int bit) => (b & (1u << bit)) != 0;
            return new PlayerSettings
            {
                ShareBetweenMyCastles = On(0),
                TakePartInClanShare = On(1),
                StashOnDoubleSort = On(2),
                StashFromAnywhere = On(3),
                SilentStash = On(4),
                CraftFromStorage = On(5),
                NeverPullLastStack = On(6),
                SilentPull = On(7),
                ServantLootStash = On(8),
                TidyConfirm = On(9),
                HeartTopUpOnVisit = On(10),
                HeartTopUpIslandOnLogin = On(11),
                HeartTopUpOutsideIsland = On(12),
                ShowTermHints = On(13),
            };
        }

        void Flag(PluginContext ctx, int bit, string key, bool value, string label, string help)
        {
            var e = ctx.Bind(Section, key, value, help);
            e.SettingChanged += (_, _) => Changed?.Invoke();
            _flags.Add((e, bit));
            Rows.Add((e, label, help));
        }
    }
}
