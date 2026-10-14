// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Persistence;
using Satisvampory.Engine.Persistence;
using EngineId = Satisvampory.Engine.Model.ContainerId;

namespace Satisvampory.Server
{
    /// <summary>
    /// Keys in the world save (spec 12). Nothing is keyed by the sharing island, which changes
    /// whenever someone builds, toggles sharing, excludes a castle or is raided:
    /// <list type="bullet">
    /// <item>"container/&lt;id&gt;": one container's lists, deposit settings and recipe switches.</item>
    /// <item>"settings/clan/&lt;guid&gt;" or "settings/owner/&lt;platform id&gt;": belts, Mass Deposit,
    /// reserves, caps, categories and loop modes, shared by every castle of that clan (or of a
    /// clanless owner).</item>
    /// <item>"castle-settings/&lt;territory&gt;": the Castle page.</item>
    /// <item>"player/&lt;platform id&gt;": the Me page.</item>
    /// </list>
    /// </summary>
    internal static class Stores
    {
        public static string PlayerKey(ulong platformId) => "player/" + platformId;

        public static PlayerSettings? Player(IModStore store, ulong platformId) =>
            store.Get<PlayerSettings>(PlayerKey(platformId));

        public static void SetPlayer(IModStore store, ulong platformId, PlayerSettings settings) =>
            store.Set(PlayerKey(platformId), settings);

        public static string CastleKey(int territory) => "castle-settings/" + territory;

        /// <summary>Castle page settings (spec 10), kept on the castle's territory so they follow the heart, not the owner.</summary>
        public static CastleSettings Castle(IModStore store, int territory) =>
            store.Get<CastleSettings>(CastleKey(territory)) ?? new CastleSettings();

        public static void SetCastle(IModStore store, int territory, CastleSettings settings) =>
            store.Set(CastleKey(territory), settings);

        /// <summary>Whose island-wide settings a castle uses: its owner's clan, else its owner.</summary>
        public static string SettingsKey(PluginContextLike ctx, ICastleHandle castle)
        {
            if (castle.Owner.HasClan)
            {
                var clan = ctx.ClanOf(castle);
                if (clan != null) return "clan/" + clan;
            }
            return "owner/" + castle.Owner.PlatformId;
        }

        static string ContainerKey(EngineId id) => "container/" + id.Value.ToString("N");

        /// <summary>
        /// Load the island-wide settings under <paramref name="settingsKey"/> and every listed
        /// container's own record into one engine state.
        /// </summary>
        public static IslandState Load(IModStore store, string settingsKey, IEnumerable<EngineId> containers)
        {
            var island = store.Get<IslandSave>("settings/" + settingsKey) ?? new IslandSave();
            island.Containers = new Dictionary<string, ContainerDto>();
            island.TableRecipesOn = new Dictionary<string, List<int>>();
            foreach (var id in containers)
            {
                var rec = store.Get<ContainerRecord>(ContainerKey(id));
                if (rec == null) continue;
                var key = id.Value.ToString("N");
                if (rec.Container != null) island.Containers[key] = rec.Container;
                if (rec.TableRecipesOn is { Count: > 0 }) island.TableRecipesOn[key] = rec.TableRecipesOn;
            }
            return SaveCodec.Restore(island, out _);
        }

        /// <summary>
        /// Save the island-wide part under <paramref name="settingsKey"/> and each container's part
        /// under its own id. Containers whose record became empty are removed.
        /// </summary>
        public static void Save(IModStore store, string settingsKey, IslandState state)
        {
            var save = SaveCodec.Capture(state, state.Graph.Sequence);
            var containers = save.Containers;
            var recipes = save.TableRecipesOn;
            save.Containers = new Dictionary<string, ContainerDto>();
            save.TableRecipesOn = new Dictionary<string, List<int>>();
            store.Set("settings/" + settingsKey, save);

            var ids = new HashSet<string>(containers.Keys.Concat(recipes.Keys));
            foreach (var key in state.Touched.Select(t => t.Value.ToString("N"))) ids.Add(key);
            foreach (var key in ids)
            {
                containers.TryGetValue(key, out var dto);
                recipes.TryGetValue(key, out var on);
                var empty = (dto == null || IsDefault(dto)) && (on == null || on.Count == 0);
                if (empty) store.Remove("container/" + key);
                else store.Set("container/" + key, new ContainerRecord { Container = dto, TableRecipesOn = on });
            }
            state.Touched.Clear();
        }

        static bool IsDefault(ContainerDto d) =>
            d.Input.Count == 0 && d.Output.Count == 0 && d.PullOrder == "ListOrder" && d.SendOrder == "FillInOrder"
            && d.Takes == "Anything" && d.BasePriority == 0 && d.ItemPriority.Count == 0 && d.CategoryPriority.Count == 0
            && d.BannedItems.Count == 0 && d.BannedCategories.Count == 0 && d.Special == "None";
    }

    /// <summary>One container's saved record.</summary>
    internal sealed class ContainerRecord
    {
        public ContainerDto? Container { get; set; }
        public List<int>? TableRecipesOn { get; set; }
    }

    /// <summary>The two context calls <see cref="Stores.SettingsKey"/> needs, so it can be called from anywhere server-side.</summary>
    internal sealed class PluginContextLike
    {
        readonly SatisContext _ctx;

        public PluginContextLike(SatisContext ctx) => _ctx = ctx;

        public string? ClanOf(ICastleHandle castle)
        {
            var clan = _ctx.Clans.Of(castle.Owner.User.ToEntity());
            return clan.IsNone ? null : clan.ClanGuid.ToString("N");
        }
    }
}
