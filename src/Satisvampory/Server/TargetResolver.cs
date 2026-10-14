// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Satisvampory.Net;
using EngineId = Satisvampory.Engine.Model.ContainerId;

namespace Satisvampory.Server
{
    /// <summary>
    /// Turns a panel's target into the container's stable id. The client knows the game's network
    /// id of the window it shows; the server maps it with Progenitor's network-id resolver
    /// (in progress on Progenitor's side). A stable id the client got from an earlier reply is used
    /// directly, and is checked against the requester's island by the caller.
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
            // Wired to ctx.Inventory.TryResolveNetworkId when that API lands in Progenitor.
            why = "This build cannot identify that container yet: it needs Progenitor's network id resolver.";
            return null;
        }
    }
}
