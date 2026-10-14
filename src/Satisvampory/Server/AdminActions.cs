// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using Satisvampory.Engine.Import;
using Satisvampory.Engine.Model;

namespace Satisvampory.Server
{
    /// <summary>
    /// Admin actions requested from the Server section of the settings page. Callers check
    /// admin rights before calling. Results are short plain text for an info row.
    /// </summary>
    internal static class AdminActions
    {
        /// <summary>
        /// Where the older mods kept their settings, newest first. Satisvampory 1.x began as a fork of
        /// KindredLogistics and both use the same file layout (1.x adds fields), so one reader serves both.
        /// </summary>
        static readonly (string Mod, string Folder)[] Sources =
        {
            ("Satisvampory 1.x", "Satisvampory"),
            ("KindredLogistics", "KindredLogistics"),
        };

        /// <summary>
        /// Dry run of the Satisvampory 1.x or KindredLogistics settings import (spec 12): what would carry over and what would not.
        /// Writes nothing.
        /// </summary>
        public static string ImportCheck()
        {
            var found = Sources
                .Select(s => (s.Mod, Path: Path.Combine(BepInEx.Paths.ConfigPath, s.Folder, "playerSettings.json")))
                .FirstOrDefault(s => File.Exists(s.Path));
            if (found.Path == null) return "No Satisvampory 1.x or KindredLogistics settings file on this server.";
            var path = found.Path;
            try
            {
                var r = LegacySettings.Read(File.ReadAllText(path), new ItemCatalog());
                var notes = r.NotImported.Count == 0 ? "" : " Not carried over: " + string.Join("; ", r.NotImported.Take(4)) + (r.NotImported.Count > 4 ? "; …" : ".");
                return $"From {found.Mod}: would import {r.Players.Count} players, {r.ClanShareByClanKey.Count} clan share switches, "
                    + $"{r.Players.Sum(p => p.Rules.ItemReserve.Count)} reserves, {r.Players.Sum(p => p.Rules.ItemCap.Count)} caps, "
                    + $"{r.Players.Sum(p => p.Categories.Custom.Count + p.Categories.Edited.Count)} categories.{notes}";
            }
            catch (Exception e)
            {
                return $"Could not read the {found.Mod} settings file: " + e.Message;
            }
        }
    }
}
