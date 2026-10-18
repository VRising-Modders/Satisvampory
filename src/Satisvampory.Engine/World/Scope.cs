// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.World
{
    /// <summary>
    /// Server limits for the two sharing kinds (spec 06). Whether a player shares is the player's
    /// own setting, carried on <see cref="PlotInfo"/> and passed for the actor.
    /// </summary>
    public sealed class SharingState
    {
        public bool CastleShareAllowed { get; set; } = true;
        public bool PlayersMayLeaveCastleShare { get; set; } = true;
        public bool ClanShareAllowed { get; set; } = true;
        public bool PlayersMayLeaveClanShare { get; set; } = true;

        /// <summary>A player's castle share choice after the server's limits.</summary>
        public bool CastleShare(bool playerChoice) => CastleShareAllowed && (playerChoice || !PlayersMayLeaveCastleShare);

        /// <summary>A player's clan share choice after the server's limits.</summary>
        public bool ClanShare(bool playerChoice) => ClanShareAllowed && (playerChoice || !PlayersMayLeaveClanShare);
    }

    /// <summary>A plot as scope resolution needs it, with its owner's sharing choices (both default on).</summary>
    public readonly record struct PlotInfo(
        PlotId Id,
        ulong Owner,
        bool Excluded,
        bool OwnerSharesCastles = true,
        bool OwnerInClanShare = true);

    public static class Scope
    {
        /// <summary>
        /// Plots reachable from <paramref name="standing"/>.
        /// <list type="bullet">
        /// <item>Clan share on and the acting player takes part: every non-excluded plot whose owner takes part.
        /// Players who opt out are invisible to others and reach only their castle-share plots.</item>
        /// <item>Castle share on: every non-excluded plot owned by the standing plot's owner.</item>
        /// <item>Otherwise, a guest, or standing on an excluded plot: the standing plot only.</item>
        /// </list>
        /// Raided plots drop out entirely; a raided standing plot reaches nothing.
        /// </summary>
        /// <param name="isClanMember">The acting player is the owner or in the owner's clan.</param>
        /// <param name="actorInClanShare">The acting player's own "Take part in clan share" setting.</param>
        /// <param name="clanPlots">Every plot of the owner's clan (or just the owner's plots when clanless).</param>
        public static IReadOnlyList<PlotId> Resolve(
            PlotInfo standing,
            bool isClanMember,
            bool actorInClanShare,
            SharingState sharing,
            IEnumerable<PlotInfo> clanPlots,
            IWorldView world)
        {
            if (world.IsRaided(standing.Id)) return new PlotId[0];
            if (!isClanMember || standing.Excluded) return new[] { standing.Id };

            var clan = sharing.ClanShare(actorInClanShare) && sharing.ClanShare(standing.OwnerInClanShare);
            var castle = sharing.CastleShare(standing.OwnerSharesCastles);
            bool Reach(PlotInfo p) =>
                (clan && sharing.ClanShare(p.OwnerInClanShare)) || (castle && p.Owner == standing.Owner);

            var result = new List<PlotId> { standing.Id };
            foreach (var p in clanPlots)
            {
                if (p.Id == standing.Id || p.Excluded || world.IsRaided(p.Id) || !Reach(p)) continue;
                result.Add(p.Id);
            }
            return result;
        }

        /// <summary>Every shared chest (stations and private chests excluded) on the given plots.</summary>
        public static IEnumerable<ContainerInfo> Chests(IWorldView world, IEnumerable<PlotId> plots) =>
            plots.SelectMany(world.ContainersOn).Where(c => c.Shared);
    }
}
