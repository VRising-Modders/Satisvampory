// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Import
{
    /// <summary>What a 1.x-style chest name said, read once by the importer (spec 12).</summary>
    public sealed class LegacyName
    {
        public List<int> SendsOn { get; } = new();
        public List<int> ReceivesOn { get; } = new();
        public int Priority { get; set; }
        /// <summary>Words after "--", unresolved.</summary>
        public List<string> Bans { get; } = new();
        public SpecialKind Special { get; set; }
        /// <summary>A 1.x brazier chest; 2.0 makes it an input of the plot's braziers.</summary>
        public bool WasBrazier { get; set; }
        /// <summary>The name with every code removed; what the chest is called afterwards.</summary>
        public string CleanName { get; set; } = string.Empty;

        public bool HasCodes => SendsOn.Count > 0 || ReceivesOn.Count > 0 || Priority > 0 || Bans.Count > 0 || Special != SpecialKind.None || WasBrazier;
    }

    public static class LegacyNames
    {
        static readonly Regex Line = new(@"^([sr])(\d{1,2})$", RegexOptions.IgnoreCase);

        /// <param name="chest">Only a chest can be a brazier chest; on anything else "brazier" is a plain word.</param>
        public static LegacyName Parse(string name, bool chest = true)
        {
            var result = new LegacyName();
            var text = (name ?? string.Empty).Trim();

            // Skip: "''" (or "' '") at the start or end.
            foreach (var mark in new[] { "''", "' '" })
            {
                if (text.StartsWith(mark, StringComparison.Ordinal)) { result.Special = SpecialKind.Skip; text = text.Substring(mark.Length).Trim(); }
                if (text.EndsWith(mark, StringComparison.Ordinal)) { result.Special = SpecialKind.Skip; text = text.Substring(0, text.Length - mark.Length).Trim(); }
            }

            // Priority: trailing plus signs.
            while (text.EndsWith("+", StringComparison.Ordinal))
            {
                result.Priority++;
                text = text.Substring(0, text.Length - 1).TrimEnd();
            }

            var keep = new List<string>();
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var brazierWords = new List<string>();
            for (var i = 0; i < words.Length; i++)
            {
                var w = words[i];
                var lower = w.ToLowerInvariant();
                Match m;
                if ((m = Line.Match(w)).Success)
                {
                    var n = int.Parse(m.Groups[2].Value);
                    (char.ToLowerInvariant(m.Groups[1].Value[0]) == 's' ? result.SendsOn : result.ReceivesOn).Add(n);
                    continue;
                }
                if (lower.StartsWith("--", StringComparison.Ordinal) && lower.Length > 2)
                {
                    result.Bans.Add(lower.Substring(2).TrimEnd('+'));
                    continue;
                }
                switch (lower)
                {
                    case "ns": result.Special = SpecialKind.Skip; continue;
                    case "overflow": result.Special = SpecialKind.Overflow; continue;
                    case "salvage": result.Special = SpecialKind.Salvage; continue;
                    case "spoils": result.Special = SpecialKind.Spoils; continue;
                    case "trash": result.Special = SpecialKind.Trash; continue;
                    case "spawner": result.Special = SpecialKind.Spawner; continue;
                    case "brazier" when chest: result.WasBrazier = true; continue;
                    case "night":
                    case "prox":
                        brazierWords.Add(w);
                        keep.Add(w);
                        continue;
                }
                keep.Add(w);
            }

            // 2.0 has no brazier chests: a brazier is fed from its own Input list. The importer
            // adds a 1.x brazier chest to the Input of every brazier on its plot. "night" / "prox"
            // are dropped with it; elsewhere they are plain words.
            if (result.WasBrazier)
                keep.RemoveAll(k => brazierWords.Contains(k));

            result.CleanName = string.Join(" ", keep);
            return result;
        }
    }

    public sealed class ImportReport
    {
        public int ChestsRead { get; set; }
        public int ChestsWithCodes { get; set; }
        public int GroupsCreated { get; set; }
        public int EntriesCreated { get; set; }
        public List<string> Unmapped { get; } = new();
        /// <summary>Containers whose name should change if the admin chose "Clean names after import".</summary>
        public Dictionary<ContainerId, string> CleanNames { get; } = new();
    }

    /// <summary>
    /// Turns 1.x name codes into 2.0 lists, groups and deposit options. Idempotent: a group or
    /// entry that already exists is not added again.
    /// </summary>
    public static class LegacyImporter
    {
        public static ImportReport Apply(IEnumerable<ContainerInfo> containers, ItemCatalog catalog, LogisticsGraph graph, World.IWorldView? world = null)
        {
            var report = new ImportReport();
            var parsed = new List<(ContainerInfo C, LegacyName N)>();
            foreach (var c in containers)
            {
                report.ChestsRead++;
                var n = LegacyNames.Parse(c.Name, c.Kind == NodeKind.Chest);
                if (!n.HasCodes)
                {
                    // Plain names still say what the chest is for; 2.0 keeps that as settings, not as a name rule.
                    ApplyNameAsSettings(c, n.CleanName, catalog, world, report);
                    continue;
                }
                report.ChestsWithCodes++;
                parsed.Add((c, n));
                report.CleanNames[c.Id] = n.CleanName;

                if (n.Priority > 0) c.Deposit.BasePriority = n.Priority;
                ApplyNameAsSettings(c, n.CleanName, catalog, world, report);
                if (n.Special != SpecialKind.None) c.Deposit.Special = n.Special;
                if (n.WasBrazier) BrazierChest(c, containers, graph, world, report);
                foreach (var word in n.Bans)
                {
                    if (catalog.TryResolveCategory(word, out var g)) c.Deposit.BannedCategories.Add(g);
                    else if (catalog.TryResolveItem(word, out var item)) c.Deposit.BannedItems.Add(item);
                    else report.Unmapped.Add($"{c.Name}: ban '--{word}' matches no item or category");
                }
            }

            // Each line's senders become one belt; every receiver takes that group as input.
            var lines = parsed.SelectMany(p => p.N.SendsOn.Concat(p.N.ReceivesOn)).Distinct().OrderBy(n => n);
            foreach (var line in lines)
            {
                var senders = parsed.Where(p => p.N.SendsOn.Contains(line)).Select(p => p.C).OrderBy(c => c.Id.Value).ToList();
                var receivers = parsed.Where(p => p.N.ReceivesOn.Contains(line)).Select(p => p.C).ToList();
                if (senders.Count == 0 || receivers.Count == 0)
                {
                    report.Unmapped.Add($"Line {line}: {(senders.Count == 0 ? "no senders" : "no receivers")}");
                    if (senders.Count == 0) continue;
                }

                var name = $"Line {line}";
                var existed = graph.FindBelt(name) != null;
                var group = graph.BeltOrCreate(name);
                if (!existed) report.GroupsCreated++;
                report.EntriesCreated += BeltOps.AddMembers(graph, group.Id, senders.Select(s => s.Id), output: true);

                foreach (var r in receivers)
                {
                    var input = graph.For(r.Id).Input;
                    if (input.Any(e => e.Kind == EntryKind.Belt && e.Belt == group.Id)) continue;
                    input.Add(ListEntry.OfBelt(group.Id, graph.NextSequence()));
                    report.EntriesCreated++;
                }
            }
            return report;
        }

        /// <summary>
        /// 2.0 has no brazier chests: the chest goes on the Input list of every brazier on its plot,
        /// and keeps taking only the fuel it holds now so Mass Deposit does not fill it with other things.
        /// </summary>
        static void BrazierChest(ContainerInfo chest, IEnumerable<ContainerInfo> all, LogisticsGraph graph, World.IWorldView? world, ImportReport report)
        {
            var braziers = all.Where(b => b.Plot == chest.Plot && b.Kind == NodeKind.Station && b.Role == StationRole.Brazier).ToList();
            if (braziers.Count == 0) report.Unmapped.Add($"{chest.DisplayName}: brazier chest, but no brazier on its plot");
            foreach (var b in braziers)
            {
                var input = graph.For(b.Id).Input;
                if (input.Any(e => e.Kind == EntryKind.Container && e.Container == chest.Id)) continue;
                input.Add(ListEntry.Of(chest.Id, graph.NextSequence()));
                report.EntriesCreated++;
            }
            if (chest.Deposit.Takes == Takes.Anything && world != null)
            {
                var held = world.Contents(chest.Id).Select(s => s.Item).ToList();
                if (held.Count > 0)
                {
                    chest.Deposit.Takes = Takes.OnlyListed;
                    foreach (var i in held) chest.Deposit.ItemPriority[i] = 0;
                }
            }
        }

        static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
        {
            "", "chest", "chests", "container", "storage", "empty", "general", "misc", "miscellaneous",
            "everything", "everything else", "all", "other", "others", "extra", "dump", "stuff",
        };

        /// <summary>
        /// 1.x read a chest's purpose from its name; 2.0 reads it from deposit settings. Convert once:
        /// an item name or alias → only that item; a category word (anywhere in the name) → only that
        /// category; a blank, generic or furniture name → anything; any other name → only what the
        /// chest holds now (1.x sent a named chest only items it already held).
        /// </summary>
        static void ApplyNameAsSettings(ContainerInfo c, string cleanName, ItemCatalog catalog, World.IWorldView? world, ImportReport report)
        {
            var o = c.Deposit;
            if (o.Special != SpecialKind.None) return;
            var name = NameFold.Fold(cleanName);
            if (GenericWords.Contains(name) || name == NameFold.Fold(c.FurnitureName)) { o.Takes = Takes.Anything; return; }

            if (catalog.TryResolveItem(name, out var item))
            {
                o.Takes = Takes.OnlyListed;
                o.ItemPriority[item] = 0;
                return;
            }
            if (catalog.TryResolveCategory(name, out var whole))
            {
                o.Takes = Takes.OnlyListed;
                o.CategoryPriority[whole] = 0;
                return;
            }
            var found = false;
            foreach (var word in name.Split(' '))
                if (catalog.TryResolveCategory(word, out var cat))
                {
                    o.CategoryPriority[cat] = 0;
                    found = true;
                }
            if (found) { o.Takes = Takes.OnlyListed; return; }

            o.Takes = Takes.OnlyListed;
            if (world != null)
                foreach (var s in world.Contents(c.Id))
                    o.ItemPriority[s.Item] = 0;
            if (o.ItemPriority.Count == 0)
                report.Unmapped.Add($"{c.DisplayName}: name matches no item or category and the chest is empty; it takes nothing until set");
        }
    }
}
