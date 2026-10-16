// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Progenitor.Plugins;
using System.Collections.Generic;
using System;
using Satisvampory.Engine.Model;

namespace Satisvampory.Server
{
    /// <summary>
    /// The server's item catalog (spec 01, "Categories"), built by <see cref="CatalogBuilder"/>
    /// from Progenitor's ctx.Items.Catalog. The server has prefab names only (display names exist
    /// on the client), which the builder turns into readable names. Rebuilt whenever Progenitor's
    /// catalog rebuilds (new world, item edits).
    /// </summary>
    internal sealed class CatalogProvider
    {
        int _builds = -1;
        int _epoch = -1;

        public ItemCatalog Current { get; private set; } = new();

        /// <summary>The admin's extra aliases (synced setting); read on each refresh.</summary>
        public Func<string>? Aliases { get; set; }

        string _aliasText = "";
        List<GameItem>? _items;
        readonly HashSet<int> _construction = new();
        public bool Findable(ItemId item) => !_construction.Contains(item.Guid);

        /// <summary>Main thread only. Cheap when nothing changed.</summary>
        public void Refresh(PluginContext ctx)
        {
            // Catalog() builds on first use and then returns the cached list; CatalogInfo() never
            // builds, so asking it first would wait forever after a Core swap.
            var entries = ctx.Items.Catalog();
            var info = ctx.Items.CatalogInfo();
            var aliases = Aliases?.Invoke() ?? "";
            var fresh = entries.Count > 0 && info.Built && (info.Builds != _builds || info.Epoch != _epoch);
            if (!fresh && (aliases == _aliasText || _items == null)) return;
            if (fresh)
            {
                _items = entries.Select(e => new GameItem(e.PrefabGuid, e.PrefabName, e.DisplayName, e.MaxAmount, e.ItemCategory, e.ItemType)).ToList();
                _construction.Clear();
                foreach (var item in _items.Where(x => x.ItemType == (int)GameItemType.ItemBuilding)) _construction.Add(item.Guid);
                _builds = info.Builds;
                _epoch = info.Epoch;
                ctx.Log.LogInfo($"Satisvampory: item catalog built ({_items.Count} items).");
            }
            var catalog = CatalogBuilder.Build(_items!);
            var bad = ApplyAliases(catalog, aliases);
            if (bad.Count > 0) ctx.Log.LogWarning("Satisvampory: item aliases not understood: " + string.Join(", ", bad));
            _aliasText = aliases;
            Current = catalog;
        }

        public void Replace(ItemCatalog catalog) => Current = catalog;

        /// <summary>"ior = Iron Ore; rb = Ruby" (or one per line). Built-in aliases cannot be replaced. Returns entries it could not use.</summary>
        public static List<string> ApplyAliases(ItemCatalog catalog, string text)
        {
            var bad = new List<string>();
            foreach (var raw in (text ?? "").Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                var eq = part.IndexOf('=');
                if (eq <= 0) { bad.Add(part); continue; }
                var alias = part.Substring(0, eq).Trim();
                var name = part.Substring(eq + 1).Trim();
                if (catalog.TryResolveItem(alias, out _)) { bad.Add(part + " (already taken)"); continue; }
                if (int.TryParse(name, out var g)) { catalog.AddAlias(alias, new ItemId(g)); continue; }
                if (catalog.TryResolveItem(name, out var id)) catalog.AddAlias(alias, id);
                else bad.Add(part);
            }
            return bad;
        }

        /// <summary>The number of items in the built catalog, for diagnostics.</summary>
        public int ItemCount => _items?.Count ?? 0;
    }
}
