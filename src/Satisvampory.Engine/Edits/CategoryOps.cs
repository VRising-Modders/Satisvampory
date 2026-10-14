// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;

namespace Satisvampory.Engine.Edits
{
    /// <summary>
    /// Category housekeeping for the Belts and categories screen (spec 14): what names a category,
    /// renaming a custom one everywhere, and duplicating one.
    /// </summary>
    public static class CategoryOps
    {
        /// <summary>Everything on the island that names <paramref name="category"/> (folded key).</summary>
        public static List<string> Usages(IslandState state, string category, Func<ContainerId, string> nameOf)
        {
            var cat = NameFold.Fold(category);
            var uses = new List<string>();
            foreach (var (owner, lists) in state.Graph.Lists)
            {
                if (lists.Input.Any(e => Names(e.Filter, cat))) uses.Add($"Input filter of {nameOf(owner)}");
                if (lists.Output.Any(e => Names(e.Filter, cat))) uses.Add($"Output filter of {nameOf(owner)}");
            }
            foreach (var (id, d) in state.Deposit)
            {
                if (d.CategoryPriority.ContainsKey(cat)) uses.Add($"Deposit priority of {nameOf(id)}");
                if (d.BannedCategories.Contains(cat)) uses.Add($"Deposit ban of {nameOf(id)}");
            }
            for (var i = 0; i < state.MassDeposit.ItemRules.Count; i++)
                if (state.MassDeposit.ItemRules[i].Selector.Kind == ItemSelectorKind.Category && state.MassDeposit.ItemRules[i].Selector.Category == cat)
                    uses.Add($"Mass Deposit override {i + 1}");
            var lines = (state.MassDeposit.AdvancedScript ?? "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (ScriptMention(cat).IsMatch(lines[i])) uses.Add($"Advanced rule, line {i + 1}");
            if (state.Rules.CategoryReserve.ContainsKey(cat)) uses.Add("Category reserve");
            if (state.Rules.CategoryCap.ContainsKey(cat)) uses.Add("Category cap");
            return uses;
        }

        /// <summary>
        /// Renames a custom category and every reference to it: list filters, deposit priorities and
        /// bans, Mass Deposit overrides and Advanced lines, reserves and caps. Built-ins keep their names.
        /// </summary>
        public static EditResult Rename(IslandState state, ItemCatalog catalog, string from, string to)
        {
            var a = NameFold.Fold(from);
            var b = NameFold.Fold(to);
            if (b.Length == 0) return EditResult.No("A category needs a name.");
            if (!state.Categories.Custom.TryGetValue(a, out var members)) return EditResult.No("Only categories made on this island can be renamed.");
            if (a == b) return EditResult.Done;
            if (catalog.IsBuiltIn(b) || state.Categories.Custom.ContainsKey(b) || state.Categories.Edited.ContainsKey(b))
                return EditResult.No("That name is already a category.");

            state.Categories.Custom.Remove(a);
            state.Categories.Custom[b] = members;
            foreach (var (owner, lists) in state.Graph.Lists)
            {
                var changed = false;
                foreach (var e in lists.Input.Concat(lists.Output)) changed |= Swap(e.Filter.Categories, a, b) | Swap(e.Filter.ExcludeCategories, a, b);
                if (changed) state.Touched.Add(owner);
            }
            foreach (var (id, d) in state.Deposit)
            {
                var changed = false;
                if (d.CategoryPriority.Remove(a, out var p)) { d.CategoryPriority[b] = p; changed = true; }
                changed |= Swap(d.BannedCategories, a, b);
                if (changed) state.Touched.Add(id);
            }
            var rules = state.MassDeposit.ItemRules;
            for (var i = 0; i < rules.Count; i++)
                if (rules[i].Selector.Kind == ItemSelectorKind.Category && rules[i].Selector.Category == a)
                    rules[i] = (rules[i].Selector with { Category = b }, rules[i].Target);
            if (!string.IsNullOrEmpty(state.MassDeposit.AdvancedScript))
                state.MassDeposit.AdvancedScript = ScriptMention(a).Replace(state.MassDeposit.AdvancedScript, m => m.Groups[1].Value + Quote(b, m.Groups[2].Value));
            if (state.Rules.CategoryReserve.Remove(a, out var r)) state.Rules.CategoryReserve[b] = r;
            if (state.Rules.CategoryCap.Remove(a, out var c)) state.Rules.CategoryCap[b] = c;
            return EditResult.Done;
        }

        /// <summary>Copies a category's current members into a new custom category.</summary>
        public static EditResult Duplicate(IslandState state, ItemCatalog island, string from, string to)
        {
            var a = NameFold.Fold(from);
            var b = NameFold.Fold(to);
            if (b.Length == 0) return EditResult.No("A category needs a name.");
            if (island.IsBuiltIn(b) || state.Categories.Custom.ContainsKey(b) || state.Categories.Edited.ContainsKey(b) || island.TryResolveCategory(b, out _))
                return EditResult.No("That name is already a category.");
            if (!island.TryResolveCategory(a, out var key)) return EditResult.No("That category no longer exists.");
            state.Categories.Custom[b] = island.Members(key).ToList();
            return EditResult.Done;
        }

        static bool Names(Lists.ItemFilter f, string cat) => f.Categories.Contains(cat) || f.ExcludeCategories.Contains(cat);

        static bool Swap(HashSet<string> set, string a, string b)
        {
            if (!set.Remove(a)) return false;
            set.Add(b);
            return true;
        }

        /// <summary>`category ore` or `category "ore"` in an Advanced script, as a whole word.</summary>
        static Regex ScriptMention(string cat) =>
            new(@"(\bcategory\s+)(""?)" + Regex.Escape(cat) + @"\2(?![\w])", RegexOptions.IgnoreCase);

        static string Quote(string name, string quote) => quote.Length > 0 || name.Contains(' ') ? $"\"{name}\"" : name;
    }
}
