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

        /// <summary>
        /// Items whose display name matches the text, best first: the server knows vanilla items only
        /// by prefab name ("Prison Potion Bloodwine" for Blood Merlot). When nothing contains the
        /// text, names whose words are each within one typo of the query's words count too.
        /// </summary>
        public static List<int> Matching(PluginContext ctx, string text, bool partial, int max = 30)
        {
            var map = Map(ctx);
            var needle = NameFold.Fold(text);
            if (map == null || needle.Length == 0) return new List<int>();
            var ranked = map.Select(kv => (kv.Value, Rank: Engine.Ui.Find.MatchRank(kv.Key, needle), kv.Key))
                .Where(x => x.Rank < (partial ? 4 : 1))
                .OrderBy(x => x.Rank).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Value).Distinct().Take(max).ToList();
            if (ranked.Count > 0 || !partial) return ranked;
            var words = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return map.Where(kv => Close(kv.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries), words))
                .OrderBy(kv => kv.Key.Length).Select(kv => kv.Value).Distinct().Take(max).ToList();
        }

        /// <summary>Every query word matches a name word with at most one edit (words under four letters exactly).</summary>
        static bool Close(string[] name, string[] query) =>
            query.All(q => name.Any(n => q.Length < 4 ? n == q : n.StartsWith(q, StringComparison.Ordinal) || Edits(n, q) <= 1));

        static int Edits(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 1) return 2;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) prev[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }
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

        /// <summary>An item's name as the game shows it, or null when the catalog has none.</summary>
        public static string? NameOf(PluginContext ctx, int guid)
        {
            try
            {
                foreach (var e in ctx.Items.Catalog())
                    if (e.PrefabGuid == guid) return string.IsNullOrWhiteSpace(e.DisplayName) ? null : e.DisplayName;
            }
            catch (InvalidOperationException) { }
            return null;
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
