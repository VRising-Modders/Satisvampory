// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;

namespace Satisvampory.World
{
    /// <summary>Facts about items read from the game's own prefabs (server), cached per item.</summary>
    internal static class GameItems
    {
        static readonly Dictionary<int, bool> Salvage = new();

        /// <summary>The server world, set at load.</summary>
        internal static Unity.Entities.World? World { get; set; }

        /// <summary>
        /// Whether the Devourer will take the item: its prefab carries the game's Salvageable component (weapons,
        /// armour, tools); raw materials do not. Unknown items count as salvageable so nothing is held back by mistake.
        /// </summary>
        internal static bool Salvageable(int guid)
        {
            if (Salvage.TryGetValue(guid, out var known)) return known;
            var world = World;
            if (world == null) return true;
            var prefabs = world.GetExistingSystemManaged<ProjectM.PrefabCollectionSystem>();
            if (prefabs == null || !prefabs._PrefabGuidToEntityMap.TryGetValue(new Stunlock.Core.PrefabGUID(guid), out var entity))
                return true;
            return Salvage[guid] = world.EntityManager.HasComponent<ProjectM.Shared.Salvageable>(entity);
        }
    }
}
