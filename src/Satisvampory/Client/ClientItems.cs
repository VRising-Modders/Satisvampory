// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Plugins;
using Satisvampory.Engine.Model;

namespace Satisvampory.Client
{
    /// <summary>
    /// Item names as the player sees them. The client has the game's localized display names;
    /// the server only has prefab names. So whatever the player types is turned into the item's
    /// guid here when it names an item, and sent as that guid; anything else (a category, an
    /// alias) goes as typed for the server to resolve.
    /// </summary>
    internal static class ClientItems
    {
        static Dictionary<string, int>? _byName;
        static int _builds = -1;

        public static string Resolve(PluginContext ctx, string text)
        {
            var t = (text ?? string.Empty).Trim();
            if (t.Length == 0 || int.TryParse(t, out _)) return t;
            if (t.StartsWith("category ", StringComparison.OrdinalIgnoreCase)) return t;
            var map = Map(ctx);
            return map != null && map.TryGetValue(NameFold.Fold(t), out var guid) ? guid.ToString() : t;
        }

        /// <summary>The game's display name for an item guid on this client, else the server's name.</summary>
        public static string NameOf(PluginContext ctx, int guid, string fallback)
        {
            try
            {
                var e = ctx.Items.CatalogEntry(guid);
                return e != null && !string.IsNullOrWhiteSpace(e.DisplayName) ? e.DisplayName! : fallback;
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }

        static Dictionary<string, int>? Map(PluginContext ctx)
        {
            try
            {
                // Catalog() builds on first use; CatalogInfo() alone never does.
                var entries = ctx.Items.Catalog();
                var info = ctx.Items.CatalogInfo();
                if (entries.Count == 0 || !info.Built) return _byName;
                if (_byName != null && info.Builds == _builds) return _byName;
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.DisplayName)) continue;
                    map.TryAdd(NameFold.Fold(e.DisplayName!), e.PrefabGuid);
                }
                _byName = map;
                _builds = info.Builds;
                return map;
            }
            catch (InvalidOperationException)
            {
                return _byName;
            }
        }
    }
}
