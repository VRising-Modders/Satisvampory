// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Progenitor.Plugins;
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

        /// <summary>Main thread only. Cheap when nothing changed.</summary>
        public void Refresh(PluginContext ctx)
        {
            var info = ctx.Items.CatalogInfo();
            if (!info.Built || (info.Builds == _builds && info.Epoch == _epoch)) return;
            var entries = ctx.Items.Catalog();
            if (entries.Count == 0) return;
            Current = CatalogBuilder.Build(entries.Select(e =>
                new GameItem(e.PrefabGuid, e.PrefabName, e.DisplayName, e.MaxAmount, e.ItemCategory, e.ItemType)));
            _builds = info.Builds;
            _epoch = info.Epoch;
            ctx.Log.LogInfo($"Satisvampory: item catalog built ({entries.Count} items).");
        }

        public void Replace(ItemCatalog catalog) => Current = catalog;
    }
}
