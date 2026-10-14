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
        /// <summary>The 1.x settings file on this server, where 1.x kept it.</summary>
        static string LegacyPath => Path.Combine(BepInEx.Paths.ConfigPath, "Satisvampory", "playerSettings.json");

        /// <summary>
        /// Dry run of the 1.x settings import (spec 12): what would carry over and what would not.
        /// Writes nothing.
        /// </summary>
        public static string ImportCheck()
        {
            var path = LegacyPath;
            if (!File.Exists(path)) return "No Satisvampory 1.x settings file on this server.";
            try
            {
                var r = LegacySettings.Read(File.ReadAllText(path), new ItemCatalog());
                var notes = r.NotImported.Count == 0 ? "" : " Not carried over: " + string.Join("; ", r.NotImported.Take(4)) + (r.NotImported.Count > 4 ? "; …" : ".");
                return $"Would import {r.Players.Count} players, {r.ClanShareByClanKey.Count} clan share switches, "
                    + $"{r.Players.Sum(p => p.Rules.ItemReserve.Count)} reserves, {r.Players.Sum(p => p.Rules.ItemCap.Count)} caps, "
                    + $"{r.Players.Sum(p => p.Categories.Custom.Count + p.Categories.Edited.Count)} categories.{notes}";
            }
            catch (Exception e)
            {
                return "Could not read the 1.x settings file: " + e.Message;
            }
        }
    }
}
