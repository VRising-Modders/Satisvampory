// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;

namespace Satisvampory.Engine.Import
{
    /// <summary>One 1.x player record mapped onto 2.0 (spec 12, step 1).</summary>
    public sealed class ImportedPlayer
    {
        public ulong PlatformId { get; init; }
        public PlayerSettings Me { get; init; } = new();

        /// <summary>Castle rules the player held as a heart owner. The plugin applies them to that owner's island.</summary>
        public CastleRules Rules { get; init; } = new();

        public CategoryEdits Categories { get; init; } = new();

        /// <summary>Built-in categories this player deleted in 1.x. 2.0 cannot delete built-ins, so these are reported.</summary>
        public List<string> DeletedBuiltIns { get; } = new();

        /// <summary>Territory indexes with salvage feeding on. The plugin maps them to hearts.</summary>
        public List<int> SalvageTerritories { get; } = new();

        /// <summary>Territory indexes where repeat hunts were off.</summary>
        public List<int> RepeatHuntOffTerritories { get; } = new();
    }

    public sealed class SettingsImport
    {
        public ServerSettings Server { get; init; } = new();
        public List<ImportedPlayer> Players { get; } = new();

        /// <summary>Clan share per 1.x clan key, as written ("c(Normal 10406:1)" style). The plugin resolves clans.</summary>
        public Dictionary<string, bool> ClanShareByClanKey { get; } = new();

        public List<int> ExcludedTerritories { get; } = new();
        public Dictionary<string, ItemId> ServerAliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Everything read but not mapped, one line each, for the import report.</summary>
        public List<string> NotImported { get; } = new();
    }

    /// <summary>
    /// Reads a 1.x settings file by its field names and maps each onto a 2.0 setting. Built from
    /// the file's own shape, not from 1.x code. Unknown fields are reported, never fatal.
    /// </summary>
    public static class LegacySettings
    {
        const string ServerKey = "0";

        // Fields that have no 2.0 home, with the reason shown in the report.
        static readonly Dictionary<string, string> Dropped = new(StringComparer.Ordinal)
        {
            ["AutoScoop"] = "autoloot is a separate mod",
            ["AutoFilter"] = "autoloot is a separate mod",
            ["NotifyMode"] = "autoloot is a separate mod",
            ["Radius"] = "autoloot is a separate mod",
            ["ScoopMode"] = "autoloot is a separate mod",
            ["ScoopExcludes"] = "autoloot is a separate mod",
            ["ScoopExcludeNames"] = "autoloot is a separate mod",
            ["ScoopCaps"] = "autoloot is a separate mod",
            ["ScoopCapNames"] = "autoloot is a separate mod",
            ["StarterKitSeeded"] = "supply lend is not in 2.0",
            ["StarterKitChestSeeded"] = "supply lend is not in 2.0",
            ["StarterKitChestOptOut"] = "supply lend is not in 2.0",
            ["CoverSeeds"] = "supply lend is not in 2.0",
            ["HeartFuelSeeded"] = "supply lend is not in 2.0",
            ["HeartFuelOptOut"] = "supply lend is not in 2.0",
            ["NeedGoal"] = "need goals are not in 2.0",
            ["NeedTargets"] = "need goals are not in 2.0",
            ["HeartFeed"] = "per-plot heart feed values are ambiguous in the file; hearts keep the default (ON)",
            ["Pull"] = "pull has no per-player switch in 2.0",
            ["AppliedSgAllOn"] = "bookkeeping only",
            ["ConveyorLoops"] = "1.x guarded name-made loops; 2.0 loops are explicit and guarded, so the 2.0 default (ON) applies",
        };

        static readonly HashSet<string> Known = new(StringComparer.Ordinal)
        {
            "SortStash", "Pull", "CraftPull", "DontPullLast", "PullReserve", "ItemReserves", "ItemReserveNames",
            "ItemCaps", "ItemCapNames", "ItemGroups", "ItemGroupNames", "DeletedGroups", "OverlaidGroups",
            "AutoStashMissions", "Conveyor", "ConveyorLoops", "RepeatHuntRequireOnline", "Salvage", "UnitSpawner",
            "Brazier", "Named", "SilentPull", "SilentStash", "Trash", "RrGlobal", "RrGlobalAllow", "ClanShare",
            "PlotSalvage", "HeartFeed", "ClanShareByClan", "ClanShareExcludedTerritories", "RepeatHunt",
            "RepeatHuntOffPlots", "RepeatHuntMaxSuccess", "ItemAliases", "ItemAliasNames",
        };

        public static SettingsImport Read(string json, ItemCatalog catalog)
        {
            var result = new SettingsImport();
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var reportedDrops = new HashSet<string>();

            foreach (var record in doc.RootElement.EnumerateObject())
            {
                if (record.Value.ValueKind != JsonValueKind.Object) continue;
                var r = record.Value;

                foreach (var f in r.EnumerateObject())
                {
                    if (!Known.Contains(f.Name) && !Dropped.ContainsKey(f.Name) && reportedDrops.Add(f.Name))
                        result.NotImported.Add($"{f.Name}: unknown field");
                    else if (Dropped.TryGetValue(f.Name, out var why) && HasValue(f.Value) && reportedDrops.Add(f.Name))
                        result.NotImported.Add($"{f.Name}: {why}");
                }

                if (record.Name == ServerKey) ReadServer(r, result, catalog);
                else if (ulong.TryParse(record.Name, out var id)) result.Players.Add(ReadPlayer(id, r, catalog, result));
                else result.NotImported.Add($"record '{record.Name}': not a player id");
            }
            return result;
        }

        static void ReadServer(JsonElement r, SettingsImport result, ItemCatalog catalog)
        {
            var s = result.Server;
            s.AllowQuickStash = Bool(r, "SortStash", s.AllowQuickStash);
            s.AllowPull = Bool(r, "Pull", s.AllowPull);
            s.AllowCraftFromStorage = Bool(r, "CraftPull", s.AllowCraftFromStorage);
            s.AllowServantLootStash = Bool(r, "AutoStashMissions", s.AllowServantLootStash);
            s.AllowLogistics = Bool(r, "Conveyor", s.AllowLogistics);
            s.AllowSalvage = Bool(r, "Salvage", s.AllowSalvage);
            s.AllowSpawnerChests = Bool(r, "UnitSpawner", s.AllowSpawnerChests);
            s.AllowBrazierChests = Bool(r, "Brazier", s.AllowBrazierChests);
            s.AllowNightAndProximity = Bool(r, "Named", s.AllowNightAndProximity);
            s.AllowTrash = Bool(r, "Trash", s.AllowTrash);
            s.AllowStashFromAnywhere = Bool(r, "RrGlobalAllow", s.AllowStashFromAnywhere);
            s.AllowRepeatHunts = Bool(r, "RepeatHunt", s.AllowRepeatHunts);
            s.RepeatHuntsOnlyWhileClanOnline = Bool(r, "RepeatHuntRequireOnline", s.RepeatHuntsOnlyWhileClanOnline);
            s.RepeatHuntSuccessCap = Int(r, "RepeatHuntMaxSuccess") ?? s.RepeatHuntSuccessCap;

            if (r.TryGetProperty("ClanShareByClan", out var byClan) && byClan.ValueKind == JsonValueKind.Object)
                foreach (var c in byClan.EnumerateObject())
                    if (c.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        result.ClanShareByClanKey[c.Name] = c.Value.GetBoolean();
            result.ExcludedTerritories.AddRange(IntList(r, "ClanShareExcludedTerritories"));

            foreach (var (alias, item) in Aliases(r))
                result.ServerAliases[alias] = item;
        }

        static ImportedPlayer ReadPlayer(ulong id, JsonElement r, ItemCatalog catalog, SettingsImport result)
        {
            var p = new ImportedPlayer { PlatformId = id };
            var me = p.Me;
            me.StashOnDoubleSort = Bool(r, "SortStash", me.StashOnDoubleSort);
            me.CraftFromStorage = Bool(r, "CraftPull", me.CraftFromStorage);
            me.NeverPullLastStack = Bool(r, "DontPullLast", me.NeverPullLastStack);
            me.SilentPull = Bool(r, "SilentPull", me.SilentPull);
            me.SilentStash = Bool(r, "SilentStash", me.SilentStash);
            me.StashFromAnywhere = Bool(r, "RrGlobal", me.StashFromAnywhere);
            me.ServantLootStash = Bool(r, "AutoStashMissions", me.ServantLootStash);

            var rules = p.Rules;
            if (Int(r, "PullReserve") is { } reserve && reserve >= 0) rules.DefaultReserve = reserve;
            foreach (var (item, n) in ItemAmounts(r, "ItemReserves")) rules.ItemReserve[item] = n;
            foreach (var (item, n) in ItemAmounts(r, "ItemCaps")) rules.ItemCap[item] = n;

            if (r.TryGetProperty("ItemGroups", out var groups) && groups.ValueKind == JsonValueKind.Object)
                foreach (var g in groups.EnumerateObject())
                {
                    var members = g.Value.ValueKind == JsonValueKind.Array
                        ? g.Value.EnumerateArray().Select(ParseItem).Where(i => i != null).Select(i => i!.Value).ToList()
                        : new List<ItemId>();
                    // A built-in name is an edited copy (1.x lists those in OverlaidGroups too);
                    // anything else is a custom category.
                    if (catalog.IsBuiltIn(g.Name)) p.Categories.Edited[g.Name] = members;
                    else p.Categories.Custom[g.Name] = members;
                }

            foreach (var d in StringList(r, "DeletedGroups"))
            {
                if (catalog.IsBuiltIn(d)) p.DeletedBuiltIns.Add(d);
                result.NotImported.Add($"player category '{d}' was deleted in 1.x; built-ins cannot be deleted in 2.0, so it stays");
            }

            foreach (var (alias, item) in Aliases(r)) p.Categories.Aliases[alias] = item;

            if (r.TryGetProperty("PlotSalvage", out var sal) && sal.ValueKind == JsonValueKind.Object)
                foreach (var t in sal.EnumerateObject())
                    if (int.TryParse(t.Name, out var terr) && t.Value.ValueKind == JsonValueKind.True) p.SalvageTerritories.Add(terr);
            p.RepeatHuntOffTerritories.AddRange(IntList(r, "RepeatHuntOffPlots"));
            return p;
        }

        // ---------------- readers ----------------

        static bool HasValue(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.Object => v.EnumerateObject().Any(),
            _ => true,
        };

        static bool Bool(JsonElement r, string name, bool fallback) =>
            r.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

        static int? Int(JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

        static IEnumerable<int> IntList(JsonElement r, string name)
        {
            if (!r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) yield return n;
                else if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var s)) yield return s;
        }

        static IEnumerable<string> StringList(JsonElement r, string name)
        {
            if (!r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) yield return e.GetString()!;
        }

        static IEnumerable<(ItemId, int)> ItemAmounts(JsonElement r, string name)
        {
            if (!r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) yield break;
            foreach (var e in v.EnumerateObject())
                if (int.TryParse(e.Name, out var guid) && e.Value.ValueKind == JsonValueKind.Number && e.Value.TryGetInt32(out var n) && n >= 0)
                    yield return (new ItemId(guid), n);
        }

        static IEnumerable<(string, ItemId)> Aliases(JsonElement r)
        {
            if (!r.TryGetProperty("ItemAliases", out var v) || v.ValueKind != JsonValueKind.Object) yield break;
            foreach (var e in v.EnumerateObject())
                if (e.Value.ValueKind == JsonValueKind.Number && e.Value.TryGetInt32(out var guid)) yield return (e.Name, new ItemId(guid));
        }

        static ItemId? ParseItem(JsonElement e) =>
            e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? new ItemId(n)
            : e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var s) ? new ItemId(s)
            : null;
    }
}
