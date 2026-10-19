// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using Satisvampory.Engine.Model;
using Satisvampory.World;
using Unity.Entities;
using IslandScope = Satisvampory.Engine.World.Scope;

namespace Satisvampory.Server
{
    /// <summary>
    /// The optional link for the Autoloot mod (spec 00): while Satisvampory runs, three plain
    /// delegates are published under an AppDomain key, and cleared on unload. Neither mod references
    /// the other's assembly. Everything answers for the player's home island (their own castle's),
    /// since autoloot runs out in the world.
    /// </summary>
    /// <remarks>
    /// Key <c>satisvampory.autoloot.v1</c>: <c>object[] { 1, storage, categories, members }</c>.
    /// <c>Func&lt;ulong, int, long[]&gt; storage(platformId, item)</c> is <c>{ island total, cap }</c>, or <c>{ 0, -1 }</c> when the item has no cap (nothing is counted then), or null
    /// without a castle. <c>Func&lt;ulong, string[]&gt; categories(platformId)</c> lists category keys (built-in plus the
    /// island's own). <c>Func&lt;ulong, string, int[]&gt; members(platformId, key)</c> lists an island category's items.
    /// </remarks>
    internal sealed class AutolootBridge
    {
        public const string Key = "satisvampory.autoloot.v1";

        /// <summary>A player's island and catalog are reused this long: a burst of drops asks many times in one tick.</summary>
        const double ReuseSeconds = 10;

        readonly System.Collections.Generic.Dictionary<ulong, (double At, ServerActions.Island? Island, ItemCatalog Catalog)> _homes = new();
        // Every item's stored total per island (its settings owner, so clan members share one walk over the chests).
        readonly System.Collections.Generic.Dictionary<string, (double At, System.Collections.Generic.Dictionary<int, long> Totals)> _totals = new();
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        // The cheap path for caps and categories: a player's settings key (their home castle's clan or owner), and per
        // key the island-wide rules and the catalog with its edits. No container records are read for these; they are
        // kept until that key's island state is saved again (Stores.KeyRevision).
        readonly System.Collections.Generic.Dictionary<ulong, (double At, string? Key)> _keys = new();
        readonly System.Collections.Generic.Dictionary<string, (long Revision, Engine.Lists.CastleRules Rules, ItemCatalog Catalog)> _rules = new();

        readonly SatisContext _ctx;
        readonly ServerHalf _half;

        internal AutolootBridge(SatisContext ctx, ServerHalf half)
        {
            _ctx = ctx;
            _half = half;
        }

        internal void Attach()
        {
            var api = new object[]
            {
                1,
                new Func<ulong, int, long[]?>(Storage),
                new Func<ulong, string[]>(Categories),
                new Func<ulong, string, int[]>(Members),
            };
            AppDomain.CurrentDomain.SetData(Key, api);
            _ctx.Track(new OnDispose(() =>
            {
                if (ReferenceEquals(AppDomain.CurrentDomain.GetData(Key), api)) AppDomain.CurrentDomain.SetData(Key, null);
            }));
        }

        ItemCatalog CatalogOf(ulong platformId)
        {
            var settings = Settings(platformId);
            if (settings != null) return settings.Value.Catalog;
            _half.Catalog.Refresh(_ctx);
            return _half.Catalog.Current;
        }

        /// <summary>The rules and catalog of the player's island settings, without its containers; null without a castle.</summary>
        (Engine.Lists.CastleRules Rules, ItemCatalog Catalog)? Settings(ulong platformId)
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (!_keys.TryGetValue(platformId, out var k) || now - k.At >= ReuseSeconds)
            {
                if (_keys.Count > 256) _keys.Clear();
                string? key = null;
                if (_ctx.Players.TryGetByAccount(platformId, out var p) && p != null)
                {
                    var home = _ctx.Castles.ForUser(new Entity { Index = p.Session.UserIndex, Version = p.Session.UserVersion });
                    if (home.Enabled) key = Stores.SettingsKey(new PluginContextLike(_ctx), home);
                }
                _keys[platformId] = k = (now, key);
            }
            if (k.Key == null) return null;
            // Only a save of this key's island state changes its rules or categories (other islands' saves do not).
            var revision = Stores.KeyRevision(k.Key);
            if (!_rules.TryGetValue(k.Key, out var r) || r.Revision != revision)
            {
                if (_rules.Count > 256) _rules.Clear();
                var state = Stores.Load(_ctx.Save, k.Key, Array.Empty<Engine.Model.ContainerId>());
                _half.Catalog.Refresh(_ctx);
                _rules[k.Key] = r = (revision, state.Rules, _half.Catalog.Current.WithEdits(state.Categories));
            }
            return (r.Rules, r.Catalog);
        }

        (ServerActions.Island? Island, ItemCatalog Catalog) Home(ulong platformId)
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (_homes.TryGetValue(platformId, out var hit) && now - hit.At < ReuseSeconds) return (hit.Island, hit.Catalog);
            if (_homes.Count > 256) { _homes.Clear(); _totals.Clear(); }
            ServerActions.Island? island = null;
            if (_ctx.Players.TryGetByAccount(platformId, out var p) && p != null)
                island = ServerActions.Home(_ctx, _half, new Entity { Index = p.Session.UserIndex, Version = p.Session.UserVersion }, _half.Config.Snapshot());
            _half.Catalog.Refresh(_ctx);
            var catalog = island == null ? _half.Catalog.Current : _half.Catalog.Current.WithEdits(island.State.Categories);
            _homes[platformId] = (now, island, catalog);
            return (island, catalog);
        }

        /// <summary>
        /// A loot burst asks for many items at once: the island's chests are walked once and every
        /// item's total kept for <see cref="ReuseSeconds"/>, so each further item costs a lookup.
        /// </summary>
        long[]? Storage(ulong platformId, int item)
        {
            var settings = Settings(platformId);
            if (settings == null) return null;
            var id = new ItemId(item);
            // No cap, nothing to count: most items never cost the island lookup or a walk over the chests.
            var cap = settings.Value.Rules.Cap(id, settings.Value.Catalog);
            if (cap == null) return new long[] { 0, -1 };
            var (island, _) = Home(platformId);
            if (island == null) return null;
            var now = _clock.Elapsed.TotalSeconds;
            if (!_totals.TryGetValue(island.OwnerKey, out var hit) || now - hit.At >= ReuseSeconds)
            {
                var totals = new System.Collections.Generic.Dictionary<int, long>();
                var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, island.Castles,
                    c => island.State.Deposit.TryGetValue(c, out var d) ? d : null, cachedRows: true, logistics: island.State);
                foreach (var c in IslandScope.Chests(world, island.Scope))
                    foreach (var stack in world.Contents(c.Id))
                        if (stack.Amount > 0) totals[stack.Item.Guid] = (totals.TryGetValue(stack.Item.Guid, out var n) ? n : 0) + stack.Amount;
                _totals[island.OwnerKey] = hit = (now, totals);
            }
            return new[] { hit.Totals.TryGetValue(item, out var total) ? total : 0, (long)cap.Value };
        }

        string[] Categories(ulong platformId) => CatalogOf(platformId).CategoryNames.ToArray();

        int[] Members(ulong platformId, string category)
        {
            var catalog = CatalogOf(platformId);
            return catalog.TryResolveCategory(category, out var key) ? catalog.Members(key).Select(i => i.Guid).ToArray() : Array.Empty<int>();
        }
    }
}
