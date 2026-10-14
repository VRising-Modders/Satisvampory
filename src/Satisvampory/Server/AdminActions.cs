// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Satisvampory.Engine.Persistence;
using Satisvampory.World;
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
        /// <summary>
        /// Spec 12: on the first boot with no 2.0 data, import once by itself. Waits for castles to
        /// load (up to ten minutes); skips when any castle already has 2.0 settings or no older
        /// settings file exists. The report goes to the server log either way.
        /// </summary>
        internal static void AutoImportOnFirstBoot(SatisContext ctx, ServerHalf half, int attempt = 0)
        {
            const string Seen = "first-boot-checked";
            try
            {
                if (ctx.Save.Get<string>("import-done") != null || ctx.Save.Get<string>(Seen) != null) return;
                var castles = ctx.Castles.All().Where(c => c.Enabled).ToList();
                if (castles.Count == 0)
                {
                    if (attempt < 20) Progenitor.Dev.MainThread.PostDelayed(30, () => AutoImportOnFirstBoot(ctx, half, attempt + 1), ctx.Owner);
                    return;
                }
                ctx.Save.Set(Seen, DateTime.UtcNow.ToString("o"));
                var like = new PluginContextLike(ctx);
                if (castles.Any(c => ctx.Save.Get<Engine.Persistence.IslandSave>("settings/" + Stores.SettingsKey(like, c)) != null))
                {
                    ctx.Log.LogInfo("Satisvampory: 2.0 data found; no automatic import (an admin can still import from the settings page).");
                    return;
                }
                if (!Sources.Any(s => File.Exists(Path.Combine(BepInEx.Paths.ConfigPath, s.Folder, "playerSettings.json")))) return;
                ctx.Log.LogInfo("Satisvampory: first boot with no 2.0 data; importing the older mod's settings once. " + ImportApply(ctx, half));
            }
            catch (Exception e)
            {
                ctx.Log.LogError("Satisvampory: automatic import failed: " + e);
            }
        }

        /// <summary>Server-page admin tools (spec 10): empty trash here, give to storage, diagnostics.</summary>
        internal static Net.TextReply Run(SatisContext ctx, ServerHalf half, Unity.Entities.Entity userEntity, Net.AdminReq q)
        {
            var user = ctx.World!.EntityManager.GetComponentData<ProjectM.Network.User>(userEntity);
            if (!user.IsAdmin) throw new Progenitor.Networking.RequestRefusedException("Only server admins can use this.");
            if (q.Op == 3) return new Net.TextReply { Text = half.Ticker?.Diagnostics(half) ?? "Not running." };

            var castle = ctx.Castles.At(user.LocalCharacter._Entity);
            if (!castle.Enabled) throw new Progenitor.Networking.RequestRefusedException("Stand on a castle plot first.");
            half.Catalog.Refresh(ctx);
            var key = Stores.SettingsKey(new PluginContextLike(ctx), castle);
            var state = Stores.Load(ctx.Save, key, World.ProgenitorWorld.ContainerIds(new[] { castle }));
            var world = new World.ProgenitorWorld(ctx.Inventory, ctx.Stations, new[] { castle }, id => state.Deposit.TryGetValue(id, out var d) ? d : null);
            var plot = World.ProgenitorWorld.PlotOf(castle);
            var label = Labels.Name(ctx.Save, castle);
            switch (q.Op)
            {
                case 1:
                {
                    var r = Engine.Stations.SpecialJobs.EmptyTrash(world, world.ContainersOn(plot), new Engine.Stations.SpecialSwitches());
                    var n = r.Destroyed.Values.Sum();
                    ctx.Log.LogInfo($"Satisvampory: admin {user.CharacterName} emptied the trash on {label} ({n} items).");
                    return new Net.TextReply { Text = n == 0 ? $"No trash on {label}." : $"Destroyed {n:N0} items in {label}'s trash chests." };
                }
                case 2:
                {
                    var catalog = half.Catalog.Current.WithEdits(state.Categories);
                    var text = (q.Item ?? "").Trim();
                    ItemId item;
                    if (int.TryParse(text, out var g)) item = new ItemId(g);
                    else if (!catalog.TryResolveItem(text, out item)) throw new Progenitor.Networking.RequestRefusedException($"No item called \"{text}\".");
                    var amount = Math.Clamp(q.Amount, 1, 100000);
                    var deposit = new Engine.Deposit.DepositContext(catalog, world, new[] { plot }, plot)
                    {
                        Settings = state.MassDeposit,
                        Belts = state.Graph.BeltMembers(),
                    };
                    var left = amount;
                    foreach (var p in Engine.Deposit.DepositPlanner.Plan(deposit, item, amount))
                    {
                        if (left <= 0) break;
                        left -= world.Create(p.To, item, Math.Min(left, p.Amount));
                    }
                    var given = amount - left;
                    ctx.Log.LogInfo($"Satisvampory: admin {user.CharacterName} gave {given} x {catalog.NameOf(item)} to storage on {label}.");
                    return new Net.TextReply { Text = $"Gave {given:N0} {catalog.NameOf(item)} to {label}" + (left > 0 ? $"; {left:N0} did not fit." : ".") };
                }
                default:
                    throw new Progenitor.Networking.RequestRefusedException("Unknown admin tool.");
            }
        }

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
    
        /// <summary>
        /// Applies the older mod's data once (spec 12). Server allows go into the synced settings;
        /// players' settings are written only where a player has none yet; reserves, caps,
        /// categories and aliases merge into the island each player's castles belong to; chest names
        /// become deposit settings and belts on every castle; per-castle switches carry over by
        /// territory. Running it again adds nothing new.
        /// </summary>
        public static string ImportApply(SatisContext ctx, ServerHalf half)
        {
            var found = Sources
                .Select(s => (s.Mod, Path: Path.Combine(BepInEx.Paths.ConfigPath, s.Folder, "playerSettings.json")))
                .FirstOrDefault(s => File.Exists(s.Path));
            var catalog = half.Catalog.Current;
            var notes = new List<string>();
            int players = 0, islands = 0, chests = 0, belts = 0, renamed = 0, castles = 0;

            var server = half.Config.Snapshot();
            var all = ctx.Castles.All().Where(c => c.Enabled).ToList();
            var ticker = half.Ticker!;

            if (found.Path != null)
            {
                var r = LegacySettings.Read(File.ReadAllText(found.Path), catalog);
                var imported = r.Server;
                imported.CleanNamesAfterImport = server.CleanNamesAfterImport;
                imported.ImportDone = true;
                half.Config.Write(imported);
                if (r.ServerAliases.Count > 0)
                {
                    var have = half.Config.Aliases.Value ?? "";
                    var add = r.ServerAliases
                        .Where(kv => !have.Contains(kv.Key + " =", StringComparison.OrdinalIgnoreCase))
                        .Select(kv => $"{kv.Key} = {kv.Value.Guid}");
                    half.Config.Aliases.Value = string.Join("; ", new[] { have.Trim() }.Concat(add).Where(x => x.Length > 0));
                }
                server = half.Config.Snapshot();

                foreach (var p in r.Players)
                {
                    if (Stores.Player(ctx.Save, p.PlatformId) == null)
                    {
                        Stores.SetPlayer(ctx.Save, p.PlatformId, p.Me);
                        half.ForgetPlayer(p.PlatformId);
                        players++;
                    }
                    var mine = all.Where(c => c.Owner.PlatformId == p.PlatformId).ToList();
                    if (mine.Count == 0) continue;
                    var (key, members) = ticker.IslandOf(mine[0], all, Ticker.Sharing(server));
                    var state = Stores.Load(ctx.Save, key, ProgenitorWorld.ContainerIds(members));
                    if (p.Rules.DefaultReserve != 10) state.Rules.DefaultReserve = p.Rules.DefaultReserve;
                    foreach (var kv in p.Rules.ItemReserve) state.Rules.ItemReserve.TryAdd(kv.Key, kv.Value);
                    foreach (var kv in p.Rules.ItemCap) state.Rules.ItemCap.TryAdd(kv.Key, kv.Value);
                    foreach (var kv in p.Categories.Custom) state.Categories.Custom.TryAdd(kv.Key, kv.Value);
                    foreach (var kv in p.Categories.Edited) state.Categories.Edited.TryAdd(kv.Key, kv.Value);
                    foreach (var kv in p.Categories.Aliases) state.Categories.Aliases.TryAdd(kv.Key, kv.Value);
                    Stores.Save(ctx.Save, key, state);
                    islands++;

                    foreach (var c in mine)
                    {
                        var cs = Stores.Castle(ctx.Save, c.Territory.Index);
                        cs.FeedSalvage |= p.SalvageTerritories.Contains(c.Territory.Index);
                        if (p.RepeatHuntOffTerritories.Contains(c.Territory.Index)) cs.RepeatHunts = false;
                        Stores.SetCastle(ctx.Save, c.Territory.Index, cs);
                    }
                }
                foreach (var t in r.ExcludedTerritories)
                {
                    var cs = Stores.Castle(ctx.Save, t);
                    cs.ExcludeFromSharing = true;
                    Stores.SetCastle(ctx.Save, t, cs);
                    castles++;
                }
                if (r.ClanShareByClanKey.Count > 0) notes.Add("clan share switches are not carried over: clan share is a per-player choice in 2.0 (default on)");
                notes.AddRange(r.NotImported.Take(3));
            }
            else
            {
                notes.Add("no older settings file; chest names only");
            }

            // Chest names → deposit settings and belts, island by island.
            var done = new HashSet<Engine.Model.PlotId>();
            foreach (var castle in all)
            {
                var plot = ProgenitorWorld.PlotOf(castle);
                if (done.Contains(plot)) continue;
                var (key, members) = ticker.IslandOf(castle, all, Ticker.Sharing(server));
                foreach (var m in members) done.Add(ProgenitorWorld.PlotOf(m));
                var state = Stores.Load(ctx.Save, key, ProgenitorWorld.ContainerIds(members));
                var world = new ProgenitorWorld(ctx.Inventory, ctx.Stations, members, id => state.Deposit.TryGetValue(id, out var d) ? d : null);
                var containers = members.SelectMany(m => world.ContainersOn(ProgenitorWorld.PlotOf(m))).Where(c => c.Kind != Engine.Model.NodeKind.Heart).ToList();
                var report = LegacyImporter.Apply(containers, catalog.WithEdits(state.Categories), state.Graph, world);
                foreach (var c in containers)
                    if (!state.Deposit.ContainsKey(c.Id)) { state.Deposit[c.Id] = c.Deposit; state.Touched.Add(c.Id); chests++; }
                belts += report.GroupsCreated;
                foreach (var id in state.Graph.Lists.Keys) state.Touched.Add(id);
                Stores.Save(ctx.Save, key, state);

                if (server.CleanNamesAfterImport)
                    foreach (var (id, clean) in report.CleanNames)
                        if (ctx.Castles.TrySetNameplate(world.EntityOf(id), clean, out _)) renamed++;
            }
            half.MaterialPolicy.Invalidate();
            ctx.Save.Set("import-done", System.DateTime.UtcNow.ToString("o"));

            return $"Imported{(found.Path != null ? " from " + found.Mod : "")}: {players} players, {islands} islands' reserves/caps/categories, "
                + $"{castles} excluded castles, {chests} chests set from their names, {belts} belts"
                + (server.CleanNamesAfterImport ? $", {renamed} names cleaned" : "")
                + (notes.Count > 0 ? ". Notes: " + string.Join("; ", notes) : ".");
        }

    }
}
