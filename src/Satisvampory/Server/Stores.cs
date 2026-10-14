// SPDX-License-Identifier: AGPL-3.0-only
using Progenitor.Persistence;
using Satisvampory.Engine.Persistence;

namespace Satisvampory.Server
{
    /// <summary>
    /// Keys in the world save (spec 12). Players by platform id; islands by their settings owner:
    /// "owner/&lt;platform id&gt;" under castle share, "clan/&lt;clan guid&gt;" under clan share,
    /// "castle/&lt;heart id&gt;" when unshared. Values are the engine's plain save records.
    /// </summary>
    internal static class Stores
    {
        public static string PlayerKey(ulong platformId) => "player/" + platformId;

        public static string IslandKey(string owner) => "island/" + owner;

        public static PlayerSettings? Player(IModStore store, ulong platformId) =>
            store.Get<PlayerSettings>(PlayerKey(platformId));

        public static void SetPlayer(IModStore store, ulong platformId, PlayerSettings settings) =>
            store.Set(PlayerKey(platformId), settings);

        public static IslandSave? Island(IModStore store, string owner) =>
            store.Get<IslandSave>(IslandKey(owner));

        public static void SetIsland(IModStore store, string owner, IslandSave save) =>
            store.Set(IslandKey(owner), save);
    }
}
