// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.World
{
    /// <summary>
    /// Read side of the game, as the engine needs it. The plugin implements this over Progenitor's
    /// ctx.Inventory / ctx.Castles / ctx.Clans (spec 11). Tests implement it in memory.
    /// </summary>
    public interface IWorldView
    {
        /// <summary>Every container and station on the plot, stations included.</summary>
        IReadOnlyList<ContainerInfo> ContainersOn(PlotId plot);

        ContainerInfo? Find(ContainerId id);

        /// <summary>Items in one inventory, summed per item.</summary>
        IReadOnlyList<ItemStack> Contents(ContainerId id, InvPart part = InvPart.Main);

        int Count(ContainerId id, ItemId item, InvPart part = InvPart.Main);

        /// <summary>
        /// How many more of <paramref name="item"/> fit in the main (input) inventory, honoring stack
        /// size and slot filters (treasury chests refuse Blood Essence).
        /// </summary>
        int FreeSpace(ContainerId id, ItemId item);

        bool IsRaided(PlotId plot);

        /// <summary>A station's recipes with their switch state; empty for chests.</summary>
        IReadOnlyList<Stations.Recipe> Recipes(ContainerId station);
    }

    /// <summary>Write side. Every move goes through one atomic transfer.</summary>
    public interface IWorldMutator
    {
        /// <summary>Moves up to <paramref name="amount"/>; returns how many moved. Never duplicates or destroys.</summary>
        /// <remarks>Always lands in the main (input) inventory of <paramref name="to"/>.</remarks>
        int Transfer(ContainerId from, InvPart fromPart, ContainerId to, ItemId item, int amount);

        /// <summary>Destroys up to <paramref name="amount"/> (trash chests only); returns how many were removed.</summary>
        int Destroy(ContainerId from, ItemId item, int amount);
    }

    public interface IWorld : IWorldView, IWorldMutator
    {
    }
}
