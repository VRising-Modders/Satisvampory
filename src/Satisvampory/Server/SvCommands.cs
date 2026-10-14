// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using Progenitor.Commands;
using Satisvampory.Engine.Import;
using Satisvampory.Engine.Model;

namespace Satisvampory.Server
{
    /// <summary>
    /// The small admin set for headless testing (spec 13). Players never need chat.
    /// </summary>
    [CommandGroup("sv")]
    internal static class SvCommands
    {
        [Command("version", adminOnly: true, usage: ".sv version", description: "Satisvampory and save versions.")]
        public static void Version(ICommandContext ctx)
        {
            ctx.Reply($"Satisvampory {MyPluginInfo.PLUGIN_VERSION} · island save v{Engine.Persistence.IslandSave.CurrentVersion}");
        }

        [Command("settings", adminOnly: true, usage: ".sv settings", description: "Show the server allows.")]
        public static void Settings(ICommandContext ctx)
        {
            var half = ServerHalf.Active;
            if (half == null) { ctx.Reply("Satisvampory is not running on this server."); return; }
            var s = half.Config.Snapshot();
            ctx.Reply($"Castle share {On(s.CastleShare)} · clan share {On(s.ClanShare)} · lists {On(s.AllowLogistics)} · loops {On(s.AllowLoops)} · craft from storage {On(s.AllowCraftFromStorage)} · repeat hunts {On(s.AllowRepeatHunts)}");
        }

        /// <summary>
        /// Dry run of the 1.x settings import (spec 12): reads the file and reports what would map
        /// and what would not. Nothing is written until the item catalog is generated in-game, so
        /// edited built-in categories are recognised correctly.
        /// </summary>
        [Command("import", adminOnly: true, usage: ".sv import [path to 1.x playerSettings.json]", description: "Dry run of the 1.x settings import.")]
        public static void Import(ICommandContext ctx, [Remainder] string path = "")
        {
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(BepInEx.Paths.ConfigPath, "Satisvampory", "playerSettings.json");
            if (!File.Exists(path)) { ctx.Reply($"No 1.x settings file at {path}"); return; }
            try
            {
                var r = LegacySettings.Read(File.ReadAllText(path), new ItemCatalog());
                ctx.Reply($"1.x import (dry run): {r.Players.Count} players · {r.ClanShareByClanKey.Count} clan share switches · "
                    + $"{r.Players.Sum(p => p.Rules.ItemReserve.Count)} reserves · {r.Players.Sum(p => p.Rules.ItemCap.Count)} caps · "
                    + $"{r.Players.Sum(p => p.Categories.Custom.Count + p.Categories.Edited.Count)} categories · {r.NotImported.Count} notes");
                foreach (var note in r.NotImported.Take(8)) ctx.Reply("· " + note);
            }
            catch (Exception e)
            {
                ctx.Reply("Could not read that file: " + e.Message);
            }
        }

        static string On(bool b) => b ? "on" : "off";
    }
}
