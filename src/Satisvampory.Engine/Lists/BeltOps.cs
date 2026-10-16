// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Lists
{
    public enum BeltUseKind
    {
        InputEntry,
        OutputEntry,
        MassDepositItemRule,
        ScriptLine,
    }

    /// <summary>One place a belt is used, for the Groups screen's "Used by" list (spec 14).</summary>
    public sealed record BeltUsage(BeltUseKind Kind, ContainerId? Container = null, int Index = 0);

    public sealed record BeltEdit(bool Ok, string Error = "")
    {
        public static BeltEdit Done { get; } = new(true);
        public static BeltEdit Fail(string error) => new(false, error);
    }

    /// <summary>
    /// The island's Mass Deposit settings that refer to belts by name. Passed to group
    /// operations so a rename or delete updates them in the same step.
    /// </summary>
    public sealed class BeltReferences
    {
        public MassDepositSettings? MassDeposit { get; init; }
    }

    /// <summary>Create, rename, duplicate, delete, usages and add-by-name for belts (spec 14).</summary>
    public static class BeltOps
    {
        public const int MaxNameLength = 40;

        public static BeltEdit Create(LogisticsGraph graph, string name, out Belt? group)
        {
            group = null;
            var check = CheckName(graph, name, except: null);
            if (!check.Ok) return check;
            group = graph.BeltOrCreate(name);
            return BeltEdit.Done;
        }

        public static BeltEdit Rename(LogisticsGraph graph, BeltId id, string newName, BeltReferences refs)
        {
            if (id == DefaultBelt.Id) return BeltEdit.Fail("The Default Belt cannot be renamed");
            if (!graph.Belts.TryGetValue(id, out var g)) return BeltEdit.Fail("No such group");
            var check = CheckName(graph, newName, except: id);
            if (!check.Ok) return check;

            var old = g.Name;
            g.Name = newName.Trim();

            if (refs.MassDeposit is { } md)
            {
                ReplaceTargets(md, old, t => t with { Name = g.Name });
                md.AdvancedScript = RewriteScript(md.AdvancedScript, old, g.Name);
            }
            return BeltEdit.Done;
        }

        public static BeltEdit Duplicate(LogisticsGraph graph, BeltId id, string newName, out Belt? copy)
        {
            copy = null;
            if (id == DefaultBelt.Id) return BeltEdit.Fail("The Default Belt cannot be duplicated");
            if (!graph.Belts.TryGetValue(id, out var g)) return BeltEdit.Fail("No such group");
            var result = Create(graph, newName, out copy);
            var copyId = copy?.Id;
            if (result.Ok)
                foreach (var lists in graph.Lists.Values)
                    foreach (var list in new[] { lists.Input, lists.Output })
                        list.AddRange(list.Where(e => e.Kind == EntryKind.Belt && e.Belt == id).Select(e => e.Copy(copyId)).ToArray());
            return result;
        }

        /// <summary>
        /// Delete a group and every list entry and Mass Deposit row naming it. Advanced script
        /// lines that name it are reported, not edited: the script fails its check and keeps
        /// running on its last good version until a player fixes it.
        /// </summary>
        public static List<BeltUsage> Delete(LogisticsGraph graph, BeltId id, BeltReferences refs)
        {
            if (id == DefaultBelt.Id) return new List<BeltUsage>();
            var usages = Usages(graph, id, refs);
            if (!graph.Belts.TryGetValue(id, out var g)) return usages;

            foreach (var lists in graph.Lists.Values)
            {
                var removed = lists.Input.RemoveAll(e => e.Kind == EntryKind.Belt && e.Belt == id)
                    + lists.Output.RemoveAll(e => e.Kind == EntryKind.Belt && e.Belt == id);
                if (removed > 0) lists.KeepEmpty = true;
            }
            if (refs.MassDeposit is { } md)
            {
                md.ItemRules.RemoveAll(r => IsGroupTarget(r.Target, g.Name));
            }
            graph.Belts.Remove(id);
            return usages;
        }

        public static List<BeltUsage> Usages(LogisticsGraph graph, BeltId id, BeltReferences refs)
        {
            var result = new List<BeltUsage>();
            if (!graph.Belts.TryGetValue(id, out var g)) return result;

            foreach (var (owner, lists) in graph.Lists)
            {
                for (var i = 0; i < lists.Input.Count; i++)
                    if (lists.Input[i].Kind == EntryKind.Belt && lists.Input[i].Belt == id)
                        result.Add(new BeltUsage(BeltUseKind.InputEntry, owner, i));
                for (var i = 0; i < lists.Output.Count; i++)
                    if (lists.Output[i].Kind == EntryKind.Belt && lists.Output[i].Belt == id)
                        result.Add(new BeltUsage(BeltUseKind.OutputEntry, owner, i));
            }

            if (refs.MassDeposit is { } md)
            {
                for (var i = 0; i < md.ItemRules.Count; i++)
                    if (IsGroupTarget(md.ItemRules[i].Target, g.Name)) result.Add(new BeltUsage(BeltUseKind.MassDepositItemRule, Index: i));
                foreach (var line in ScriptLinesNaming(md.AdvancedScript, g.Name))
                    result.Add(new BeltUsage(BeltUseKind.ScriptLine, Index: line));
            }
            return result;
        }

        /// <summary>
        /// Containers in scope whose name contains <paramref name="text"/> as whole words
        /// ("ore" matches "Ore", "Ore 2", "Copper ore", not "Core"). Hearts are never offered.
        /// </summary>
        public static List<ContainerInfo> MatchByName(IWorldView world, IEnumerable<PlotId> scope, string text)
        {
            var needle = NameFold.Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (needle.Length == 0) return new List<ContainerInfo>();
            return scope.SelectMany(world.ContainersOn)
                .Where(c => c.Kind != NodeKind.Heart)
                .Where(c => ContainsWords(NameFold.Fold(c.DisplayName).Split(' '), needle))
                .ToList();
        }

        /// <summary>Output connections are suppliers; Input connections are receivers.</summary>
        public static int AddMembers(LogisticsGraph graph, BeltId id, IEnumerable<ContainerId> members, bool output = true)
        {
            if (!graph.Belts.ContainsKey(id)) return 0;
            var added = 0;
            foreach (var m in members)
            {
                var io = graph.For(m);
                io.KeepEmpty = true;
                var list = output ? io.Output : io.Input;
                if (list.Any(e => e.Kind == EntryKind.Belt && e.Belt == id)) continue;
                list.Add(ListEntry.OfBelt(id, graph.NextSequence()));
                added++;
            }
            return added;
        }

        public static bool Remove(LogisticsGraph graph, BeltId id, ContainerId member, bool output = true)
        {
            if (!graph.Lists.TryGetValue(member, out var io)) return false;
            io.KeepEmpty = true;
            return (output ? io.Output : io.Input).RemoveAll(e => e.Kind == EntryKind.Belt && e.Belt == id) > 0;
        }

        public static bool Move(LogisticsGraph graph, BeltId id, int from, int to, bool output = true)
        {
            var members = (output ? graph.Suppliers(id) : graph.Receivers(id)).ToList();
            if (from < 0 || from >= members.Count || to < 0 || to >= members.Count) return false;
            var member = members[from]; members.RemoveAt(from); members.Insert(to, member);
            foreach (var m in members)
            {
                var order = graph.NextSequence();
                foreach (var c in graph.Connections(id, output).Where(c => c.Owner == m)) c.Entry.BeltOrder = order;
            }
            return true;
        }

        // ---------------- helpers ----------------

        static BeltEdit CheckName(LogisticsGraph graph, string name, BeltId? except)
        {
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0) return BeltEdit.Fail("A group needs a name");
            if (trimmed.Length > MaxNameLength) return BeltEdit.Fail($"Names are at most {MaxNameLength} characters");
            if (trimmed.IndexOf('"') >= 0) return BeltEdit.Fail("Names cannot contain quotes");
            var existing = graph.FindBelt(trimmed);
            if (existing != null && existing.Id != except) return BeltEdit.Fail($"A group named '{existing.Name}' already exists");
            return BeltEdit.Done;
        }

        static bool IsGroupTarget(Target? t, string name) =>
            t is { Kind: TargetKind.Belt } && NameFold.Fold(t.Name) == NameFold.Fold(name);

        static void ReplaceTargets(MassDepositSettings md, string oldName, Func<Target, Target> replace)
        {
            for (var i = 0; i < md.ItemRules.Count; i++)
                if (IsGroupTarget(md.ItemRules[i].Target, oldName))
                    md.ItemRules[i] = (md.ItemRules[i].Selector, replace(md.ItemRules[i].Target));
        }

        static Regex BeltRef(string name) =>
            new("(\\bbelt\\s+)\"" + Regex.Escape(name.Trim()) + "\"", RegexOptions.IgnoreCase);

        internal static string RewriteScript(string script, string oldName, string newName) =>
            string.IsNullOrEmpty(script) ? script : BeltRef(oldName).Replace(script, m => m.Groups[1].Value + "\"" + newName + "\"");

        static IEnumerable<int> ScriptLinesNaming(string script, string name)
        {
            if (string.IsNullOrEmpty(script)) yield break;
            var re = BeltRef(name);
            var lines = script.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i];
                var hash = code.IndexOf('#');
                if (hash >= 0) code = code.Substring(0, hash);
                if (re.IsMatch(code)) yield return i + 1;
            }
        }

        static bool ContainsWords(string[] hay, string[] needle)
        {
            for (var i = 0; i + needle.Length <= hay.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length && ok; j++) ok = hay[i + j] == needle[j];
                if (ok) return true;
            }
            return false;
        }
    }
}
