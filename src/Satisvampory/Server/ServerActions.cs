// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using ProjectM.Network;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Satisvampory.Engine.Player;
using Satisvampory.Engine.World;
using Satisvampory.Net;
using Satisvampory.World;
using Unity.Entities;

namespace Satisvampory.Server
{
    /// <summary>
    /// Player actions on the live world: resolve the actor's island (spec 06), load its saved
    /// state, build the engine's world over Progenitor, run the engine, report.
    /// </summary>
    internal static class ServerActions
    {
        /// <summary>Slots below this index are the hotbar, which quick stash never touches.</summary>
        const int HotbarSlots = 8;

        internal sealed class Island
        {
            public ICastleHandle Standing = null!;
            public List<ICastleHandle> Castles = new();
            public IReadOnlyList<PlotId> Scope = Array.Empty<PlotId>();
            public string OwnerKey = string.Empty;
            public IslandState State = new();
        }

        /// <summary>Null when the player is not standing on a plot they or their clan may use.</summary>
        internal static Island? Resolve(SatisContext ctx, ServerHalf half, Entity userEntity, User user)
        {
            var character = user.LocalCharacter._Entity;
            var standing = ctx.Castles.At(character);
            if (!standing.Enabled) return null;

            var relation = ctx.Castles.RelationTo(userEntity, standing);
            if (relation is CastleRelation.None) return null;
            var member = relation is CastleRelation.Owner or CastleRelation.ClanMember;

            var clan = ctx.Clans.Of(userEntity);
            var clanCastles = clan.IsNone ? ctx.Castles.AllForUser(standing.Owner.User.ToEntity()).ToList() : ctx.Clans.Castles(clan).ToList();
            if (!clanCastles.Any(c => ProgenitorWorld.PlotOf(c) == ProgenitorWorld.PlotOf(standing))) clanCastles.Add(standing);

            var server = half.Config.Snapshot();
            var sharing = new SharingState
            {
                CastleShareAllowed = server.CastleShare,
                PlayersMayLeaveCastleShare = server.PlayersMayLeaveCastleShare,
                ClanShareAllowed = server.ClanShare,
                PlayersMayLeaveClanShare = server.PlayersMayLeaveClanShare,
            };

            PlotInfo Info(ICastleHandle c)
            {
                var prefs = half.PlayerFor(c.Owner.PlatformId);
                return new PlotInfo(ProgenitorWorld.PlotOf(c), c.Owner.PlatformId, Excluded: Stores.Castle(ctx.Save, c.Territory.Index).ExcludeFromSharing,
                    OwnerSharesCastles: prefs.ShareBetweenMyCastles, OwnerInClanShare: prefs.TakePartInClanShare);
            }

            var standingInfo = Info(standing);
            var actor = half.PlayerFor(user.PlatformId);
            var raided = new HashSet<PlotId>(clanCastles.Where(c => c.Raided).Select(ProgenitorWorld.PlotOf));
            var scope = Scope.Resolve(standingInfo, member, actor.TakePartInClanShare, sharing, clanCastles.Select(Info), new RaidView(raided));

            var clanShare = !clan.IsNone && sharing.ClanShare(actor.TakePartInClanShare) && sharing.ClanShare(standingInfo.OwnerInClanShare) && scope.Count > 1;
            var castleShare = sharing.CastleShare(standingInfo.OwnerSharesCastles) && scope.Count > 1;
            var ownerKey = clanShare ? "clan/" + clan.ClanGuid.ToString("N")
                : castleShare ? "owner/" + standing.Owner.PlatformId
                : "castle/" + standing.Territory.Index;

            var save = Stores.Island(ctx.Save, ownerKey);
            var state = save == null ? new IslandState() : SaveCodec.Restore(save, out _);

            var set = new HashSet<PlotId>(scope);
            return new Island
            {
                Standing = standing,
                Castles = clanCastles.Where(c => set.Contains(ProgenitorWorld.PlotOf(c))).ToList(),
                Scope = scope,
                OwnerKey = ownerKey,
                State = state,
            };
        }

        internal static NoticePacket Run(SatisContext ctx, ServerHalf half, Entity userEntity, User user, ActionKind kind)
        {
            var server = half.Config.Snapshot();
            var island = Resolve(ctx, half, userEntity, user);
            if (island == null) return Notice(NoticeCode.Refused);

            var world = new ProgenitorWorld(ctx.Inventory, ctx.Stations, island.Castles, id => island.State.Deposit.TryGetValue(id, out var d) ? d : null)
            {
                TableRecipesOn = id => island.State.TableRecipesOn.TryGetValue(id, out var on) ? on : null,
            };
            var deposit = new DepositContext(half.Catalog.Current.WithEdits(island.State.Categories), world, island.Scope, ProgenitorWorld.PlotOf(island.Standing))
            {
                Settings = island.State.MassDeposit,
                Belts = island.State.Graph.BeltMembers(),
            };

            switch (kind)
            {
                case ActionKind.Tidy:
                {
                    var r = PlayerActions.Tidy(world, deposit, island.State.Graph);
                    return Notice(NoticeCode.Tidied, r.Items, r.Containers(byDestination: true));
                }
                case ActionKind.Stash:
                {
                    if (!server.AllowQuickStash) return Notice(NoticeCode.Refused);
                    var character = user.LocalCharacter._Entity;
                    var bagRef = ctx.Inventory.Of(character);
                    var bagId = world.AddBag(bagRef);
                    var stashable = ctx.Inventory.Read(bagRef).Slots
                        .Where(s => s.Slot >= HotbarSlots && s.Item != 0 && s.Amount > 0)
                        .Select(s => new ItemStack(new ItemId(s.Item), s.Amount))
                        .ToList();
                    var r = PlayerActions.Stash(world, deposit, bagId, stashable);
                    return Notice(NoticeCode.Stashed, r.Moves.Count, r.Containers(byDestination: true));
                }
                default:
                    return Notice(NoticeCode.Refused);
            }
        }

        static NoticePacket Notice(NoticeCode code, int a = 0, int b = 0) =>
            new() { Magic = EventIds.Magic, Code = (int)code, A = a, B = b };

        /// <summary>Only raid state is needed by scope resolution before the full world is built.</summary>
        sealed class RaidView : IWorldView
        {
            readonly HashSet<PlotId> _raided;
            public RaidView(HashSet<PlotId> raided) => _raided = raided;
            public bool IsRaided(PlotId plot) => _raided.Contains(plot);
            public IReadOnlyList<ContainerInfo> ContainersOn(PlotId plot) => Array.Empty<ContainerInfo>();
            public ContainerInfo? Find(Engine.Model.ContainerId id) => null;
            public IReadOnlyList<ItemStack> Contents(Engine.Model.ContainerId id, InvPart part = InvPart.Main) => Array.Empty<ItemStack>();
            public int Count(Engine.Model.ContainerId id, ItemId item, InvPart part = InvPart.Main) => 0;
            public int FreeSpace(Engine.Model.ContainerId id, ItemId item) => 0;
            public IReadOnlyList<Engine.Stations.Recipe> Recipes(Engine.Model.ContainerId station) => Array.Empty<Engine.Stations.Recipe>();
        }
    }
}
