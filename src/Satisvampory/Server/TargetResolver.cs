// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Satisvampory.Net;
using EngineId = Satisvampory.Engine.Model.ContainerId;

namespace Satisvampory.Server
{
    /// <summary>
    /// Turns a panel's target into the container's stable id: a stable id the client got from an
    /// earlier reply, else the game's network id words through Progenitor's resolver. The resolver
    /// does not authorize; callers check that the container is on the requester's island.
    /// </summary>
    internal static class TargetResolver
    {
        public static EngineId? Resolve(SatisContext ctx, TargetMsg target, out string why)
        {
            if (!string.IsNullOrEmpty(target.ContainerId) && Guid.TryParse(target.ContainerId, out var g))
            {
                why = string.Empty;
                return new EngineId(g);
            }
            return Resolve(ctx, target.NetIndex, target.NetGeneration, target.NetType, out why);
        }

        public static EngineId? Resolve(SatisContext ctx, int index, int generation, int type, out string why)
        {
            if (ctx.Inventory.TryResolveNetworkId(index, generation, type, out var id, out _, out var reason) && !id.IsNone)
            {
                why = string.Empty;
                return new EngineId(id.Value);
            }
            why = string.IsNullOrEmpty(reason) ? "That container could not be found." : reason;
            return null;
        }

        /// <summary>The network id words for a container, for the client's Locate; false when unknown.</summary>
        public static bool Words(SatisContext ctx, EngineId id, out int index, out int generation, out int type) =>
            ctx.Inventory.TryGetNetworkId(new Progenitor.Inventory.ContainerId(id.Value), out index, out generation, out type);
    }
}
