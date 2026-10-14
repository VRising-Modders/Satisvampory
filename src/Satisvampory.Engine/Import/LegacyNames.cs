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
        /// <summary>The name with every code removed; what the chest is called afterwards.</summary>
        public string CleanName { get; set; } = string.Empty;

        public bool HasCodes => SendsOn.Count > 0 || ReceivesOn.Count > 0 || Priority > 0 || Bans.Count > 0 || Special != SpecialKind.None;
    }

    public static class LegacyNames
    {
        static readonly Regex Line = new(@"^([sr])(\d{1,2})$", RegexOptions.IgnoreCase);

        public static LegacyName Parse(string name)
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
            var night = false;
            var prox = false;
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
                    case "brazier": result.Special = SpecialKind.Brazier; continue;
                    case "night": night = true; keep.Add(w); continue;
                    case "prox": prox = true; keep.Add(w); continue;
                }
                keep.Add(w);
            }

            // "night" / "prox" only mean something on a brazier chest; elsewhere they are plain words.
            if (result.Special == SpecialKind.Brazier)
            {
                result.Special = night ? SpecialKind.BrazierNight : prox ? SpecialKind.BrazierProximity : SpecialKind.Brazier;
                keep.RemoveAll(k => k.Equals("night", StringComparison.OrdinalIgnoreCase) || k.Equals("prox", StringComparison.OrdinalIgnoreCase));
            }

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
        public static ImportReport Apply(IEnumerable<ContainerInfo> containers, ItemCatalog catalog, LogisticsGraph graph)
        {
            var report = new ImportReport();
            var parsed = new List<(ContainerInfo C, LegacyName N)>();
            foreach (var c in containers)
            {
                report.ChestsRead++;
                var n = LegacyNames.Parse(c.Name);
                if (!n.HasCodes) continue;
                report.ChestsWithCodes++;
                parsed.Add((c, n));
                report.CleanNames[c.Id] = n.CleanName;

                if (n.Priority > 0) c.Deposit.BasePriority = n.Priority;
                if (n.Special != SpecialKind.None) c.Deposit.Special = n.Special;
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
                foreach (var s in senders)
                    if (!group.Members.Contains(s.Id)) group.Members.Add(s.Id);

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
    }
}
