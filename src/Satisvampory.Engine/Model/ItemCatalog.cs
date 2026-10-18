// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Satisvampory.Engine.Model
{
    /// <summary>
    /// Item names, aliases and categories. Built by the plugin from the game catalog and the
    /// group data file; built by hand in tests. All lookups fold case and spelling.
    /// </summary>
    public sealed class ItemCatalog
    {
        readonly Dictionary<ItemId, string> _names = new();
        readonly Dictionary<string, ItemId> _byName = new(StringComparer.Ordinal);
        readonly Dictionary<string, ItemId> _aliases = new(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<ItemId>> _categories = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _categoryAliases = new(StringComparer.Ordinal);
        readonly Dictionary<ItemId, int> _maxStack = new();
        readonly HashSet<ItemId> _recipes = new();

        /// <summary>
        /// A recipe stand-in ("Feed Prisoner"): Find shows it as a Recipe and where it is made; it is never
        /// stored, so it is in no category, no Filter list and no exact-name lookup.
        /// </summary>
        public void AddRecipe(ItemId id, string name)
        {
            _names[id] = name;
            _maxStack[id] = 1;
            _recipes.Add(id);
        }

        public bool IsRecipe(ItemId id) => _recipes.Contains(id);

        /// <summary>Real items only (no recipe stand-ins).</summary>
        public IEnumerable<ItemId> StorableItems => _names.Keys.Where(k => !_recipes.Contains(k));

        public void AddItem(ItemId id, string name, int maxStack = 100)
        {
            _names[id] = name;
            _byName[NameFold.Fold(name)] = id;
            _maxStack[id] = maxStack;
        }

        public void AddAlias(string alias, ItemId id) => _aliases[NameFold.Fold(alias)] = id;

        public void AddCategory(string group, IEnumerable<ItemId> members, params string[] aliases)
        {
            var key = NameFold.Fold(group);
            if (!_categories.TryGetValue(key, out var set)) _categories[key] = set = new HashSet<ItemId>();
            foreach (var m in members) set.Add(m);
            foreach (var a in aliases) _categoryAliases[NameFold.Fold(a)] = key;
        }

        public string NameOf(ItemId id) => _names.TryGetValue(id, out var n) ? n : id.ToString();

        public int MaxStack(ItemId id) => _maxStack.TryGetValue(id, out var s) ? s : 100;

        public IEnumerable<ItemId> AllItems => _names.Keys;

        /// <summary>Exact item by folded name or alias.</summary>
        public bool TryResolveItem(string text, out ItemId id)
        {
            var key = NameFold.Fold(text);
            return _byName.TryGetValue(key, out id) || _aliases.TryGetValue(key, out id);
        }

        /// <summary>Canonical folded group key for a group name or group alias.</summary>
        public bool TryResolveCategory(string text, out string group)
        {
            var key = NameFold.Fold(text);
            if (_categories.ContainsKey(key)) { group = key; return true; }
            if (_categoryAliases.TryGetValue(key, out var g)) { group = g; return true; }
            group = string.Empty;
            return false;
        }

        public bool InCategory(ItemId item, string group) =>
            _categories.TryGetValue(group, out var set) && set.Contains(item);

        public IReadOnlyCollection<ItemId> Members(string group) =>
            _categories.TryGetValue(group, out var set) ? set : (IReadOnlyCollection<ItemId>)Array.Empty<ItemId>();

        public IEnumerable<string> CategoriesOf(ItemId item) =>
            _categories.Where(kv => kv.Value.Contains(item)).Select(kv => kv.Key);

        public IEnumerable<string> CategoryNames => _categories.Keys;

        public bool IsBuiltIn(string category) => _builtIn.Contains(NameFold.Fold(category));

        readonly HashSet<string> _builtIn = new(StringComparer.Ordinal);

        /// <summary>Marks every category defined so far as built-in (call once after the generated set is loaded).</summary>
        public void SealBuiltIns()
        {
            foreach (var k in _categories.Keys) _builtIn.Add(k);
        }

        /// <summary>
        /// A copy of this catalog with one island's edits applied (spec 14): edited built-ins replace
        /// the generated members, custom categories are added, castle aliases are added. Server
        /// catalog is never changed.
        /// </summary>
        public ItemCatalog WithEdits(CategoryEdits edits)
        {
            var c = new ItemCatalog();
            foreach (var kv in _names) c._names[kv.Key] = kv.Value;
            foreach (var kv in _byName) c._byName[kv.Key] = kv.Value;
            foreach (var kv in _aliases) c._aliases[kv.Key] = kv.Value;
            foreach (var kv in _maxStack) c._maxStack[kv.Key] = kv.Value;
            c._recipes.UnionWith(_recipes);
            foreach (var kv in _categoryAliases) c._categoryAliases[kv.Key] = kv.Value;
            foreach (var kv in _categories) c._categories[kv.Key] = new HashSet<ItemId>(kv.Value);
            c._builtIn.UnionWith(_builtIn);

            foreach (var (name, members) in edits.Edited)
            {
                var key = NameFold.Fold(name);
                if (c._categories.ContainsKey(key)) c._categories[key] = new HashSet<ItemId>(members);
            }
            foreach (var (name, members) in edits.Custom)
            {
                var key = NameFold.Fold(name);
                if (c._builtIn.Contains(key)) continue; // a custom category never shadows a built-in
                c._categories[key] = new HashSet<ItemId>(members);
            }
            foreach (var (alias, item) in edits.Aliases)
            {
                var key = NameFold.Fold(alias);
                if (!c._categories.ContainsKey(key) && !c._categoryAliases.ContainsKey(key)) c._aliases[key] = item;
            }
            return c;
        }
    }

    /// <summary>One island's category edits (spec 14).</summary>
    public sealed class CategoryEdits
    {
        /// <summary>Castle copies of built-ins: name to members.</summary>
        public Dictionary<string, List<ItemId>> Edited { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Custom categories: name to members.</summary>
        public Dictionary<string, List<ItemId>> Custom { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Castle item aliases: alias to item.</summary>
        public Dictionary<string, ItemId> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Case, whitespace and spelling fold shared by every name comparison.</summary>
    public static class NameFold
    {
        static readonly (string, string)[] Spellings =
        {
            ("fibre", "fiber"), ("sulphur", "sulfur"), ("armour", "armor"), ("grey", "gray"),
            ("jewellery", "jewelry"), ("colour", "color"),
        };

        public static string Fold(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var s = string.Join(" ", text.Trim().ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            foreach (var (from, to) in Spellings) s = s.Replace(from, to);
            return s;
        }
    }
}
