// SPDX-License-Identifier: AGPL-3.0-only
using Satisvampory.Engine.Model;

namespace Satisvampory.Server
{
    /// <summary>
    /// The server's item catalog (spec 01, "Categories"). Built by <see cref="CatalogBuilder"/>
    /// from Progenitor's item list once that API lands; until then it is empty, so routing works by
    /// exact item only and no category applies.
    /// </summary>
    internal sealed class CatalogProvider
    {
        public ItemCatalog Current { get; private set; } = new();

        public void Replace(ItemCatalog catalog) => Current = catalog;
    }
}
