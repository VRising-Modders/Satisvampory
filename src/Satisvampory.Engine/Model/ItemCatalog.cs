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
