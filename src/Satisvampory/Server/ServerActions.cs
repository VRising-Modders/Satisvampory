// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Players;
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
            /// <summary>Whose island-wide settings apply: the standing castle owner's clan, else the owner (Stores.SettingsKey).</summary>
            public string OwnerKey = string.Empty;
            public IslandState State = new();
            public CastleRelation Relation;
        }

        /// <summary>Null when the player is not standing on a plot they or their clan may use.</summary>
        /// <param name="cachedRows">Reuse castles' container rows while their structure holds (background callers only).</param>
        /// <param name="standingTerritory">The plot to resolve from, when the caller already knows it (Progenitor's material context); otherwise where the character stands.</param>
        internal static Island? Resolve(SatisContext ctx, ServerHalf half, Entity userEntity, User user, bool cachedRows = false, int? standingTerritory = null)
        {
            var character = user.LocalCharacter._Entity;
            var standing = standingTerritory is { } t
                ? ctx.Castles.All().FirstOrDefault(c => c.Enabled && c.Territory.Index == t)
                : ctx.Castles.At(character);
            if (standing == null || !standing.Enabled) return null;

            var relation = ctx.Castles.RelationTo(userEntity, standing);
            if (relation is CastleRelation.None) return null;
            var member = relation is CastleRelation.Owner or CastleRelation.ClanMember;

            // The standing castle's owner decides the clan, so an allied visitor sees the host's island.
            var clan = ctx.Clans.Of(standing.Owner.User.ToEntity());
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

            var ownerKey = Stores.SettingsKey(new PluginContextLike(ctx), standing);
            var set = new HashSet<PlotId>(scope);
            var castles = clanCastles.Where(c => set.Contains(ProgenitorWorld.PlotOf(c))).ToList();
            return new Island
            {
                Standing = standing,
                Castles = castles,
                Scope = scope,
                OwnerKey = ownerKey,
                State = Stores.Load(ctx.Save, ownerKey, ProgenitorWorld.ContainerIds(castles, cachedRows)),
                Relation = relation,
            };
        }

        /// <summary>
        /// "Stash from anywhere" (spec 07): the island of the player's own castle, used when they
        /// stand on no plot. Null when they own no castle.
        /// </summary>
        internal static Island? Home(SatisContext ctx, ServerHalf half, Entity userEntity, ServerSettings server)
        {
            var home = ctx.Castles.ForUser(userEntity);
            if (!home.Enabled || half.Ticker == null) return null;
            var all = ctx.Castles.All().Where(c => c.Enabled).ToList();
            var (key, members) = half.Ticker.IslandOf(home, all, Ticker.Sharing(server));
            return new Island
            {
                Standing = home,
                Castles = members,
                Scope = members.Select(ProgenitorWorld.PlotOf).ToList(),
                OwnerKey = key,
                State = Stores.Load(ctx.Save, key, ProgenitorWorld.ContainerIds(members)),
                Relation = CastleRelation.Owner,
            };
        }

        /// <summary>
        /// Stash, pull and tidy gates (spec 07): refused while dead, downed or in PvP combat. Bat
        /// and other forms are fine. When Progenitor cannot read the state at all the action runs.
        /// </summary>
        internal static StateRefusal StateGate(SatisContext ctx, Entity userEntity)
        {
            var st = ctx.Players.State(userEntity);
            switch (st.Status)
            {
                case PlayerStateStatus.Unavailable: return StateRefusal.None;
                case PlayerStateStatus.Ok: break;
                default: return StateRefusal.NoCharacter;
            }
            // An offline character keeps its last buffs; only an online one's state means anything.
            if (!st.Online) return StateRefusal.NoCharacter;
            if (st.Dead) return StateRefusal.Dead;
            if (st.Downed) return StateRefusal.Downed;
            if (st.InPvpCombat) return StateRefusal.PvpCombat;
            return StateRefusal.None;
        }

        internal static string Words(StateRefusal r) => r switch
        {
            StateRefusal.Dead => "Not while dead.",
            StateRefusal.Downed => "Not while downed.",
            StateRefusal.PvpCombat => "Not during PvP combat.",
            StateRefusal.NoCharacter => "Your character is not in the world.",
            _ => "Not now.",
        };

        /// <summary>
        /// Where a stash, tidy or servant loot went, one line per destination chest
        /// ("20 Copper Ore, 1 Thread Coarse → Metal1 (Plot 6)"), then what found no room.
        /// </summary>
        internal static List<string> MoveLines(IWorld world, ItemCatalog catalog, Func<PlotId, string> plotName,
            IEnumerable<(ContainerId To, ItemId Item, int Amount)> moves, IReadOnlyDictionary<ItemId, int> left, string leftIn)
        {
            var lines = new List<string>();
            foreach (var g in moves.GroupBy(m => m.To))
            {
                var c = world.Find(g.Key);
                var items = string.Join(", ", g.GroupBy(m => m.Item).Select(x => $"{x.Sum(m => m.Amount):N0} {catalog.NameOf(x.Key)}"));
                lines.Add(c == null ? $"{items} → {g.Key}" : $"{items} → {c.DisplayName} ({plotName(c.Plot)})");
            }
            if (left.Count > 0)
                lines.Add($"no room, left in {leftIn}: " + string.Join(", ", left.Select(kv => $"{kv.Value:N0} {catalog.NameOf(kv.Key)}")));
            return lines;
        }

        /// <summary>Plot names as the Castle page shows them.</summary>
        internal static Func<PlotId, string> PlotNames(Progenitor.Persistence.IModStore save, IReadOnlyList<ICastleHandle> castles) =>
            p => castles.FirstOrDefault(c => ProgenitorWorld.PlotOf(c) == p) is { } c ? Labels.Name(save, c) : "Plot " + p.Value;

        /// <summary>The breakdown to the server log and, for players who keep "Log my deposits" on, to their chat.</summary>
        internal static void Report(SatisContext ctx, ServerHalf half, string header, List<string> lines, IEnumerable<(Entity User, ulong Account)> readers)
        {
            if (lines.Count == 0) return;
            ctx.Log.LogInfo($"Satisvampory: {header} | " + string.Join(" | ", lines));
            foreach (var (user, account) in readers)
            {
                if (!half.PlayerFor(account).LogMyDeposits) continue;
                ctx.Chat.SendTo(user, header);
                foreach (var line in lines) ctx.Chat.SendTo(user, "  " + line, prefix: false);
            }
        }

        static void ReportAction(SatisContext ctx, ServerHalf half, string verb, Entity userEntity, User user, IWorld world, ItemCatalog catalog, Island island, ActionResult r)
        {
            if (r.Moves.Count == 0 && r.Left.Count == 0) return;
            var lines = MoveLines(world, catalog, PlotNames(ctx.Save, island.Castles), r.Moves.Select(m => (m.To, m.Item, m.Amount)), r.Left, "your bag");
            Report(ctx, half, $"{verb} {r.Items:N0} items:", lines, new[] { (userEntity, user.PlatformId) });
        }

        internal static NoticePacket Run(SatisContext ctx, ServerHalf half, Entity userEntity, User user, ActionKind kind)
        {
            var gate = StateGate(ctx, userEntity);
            if (gate != StateRefusal.None) return Notice(NoticeCode.NotNow, (int)gate);
            var server = half.Config.Snapshot();
            var island = Resolve(ctx, half, userEntity, user);
            if (island == null && kind == ActionKind.Stash && server.AllowStashFromAnywhere && half.PlayerFor(user.PlatformId).StashFromAnywhere)
                island = Home(ctx, half, userEntity, server);
            if (island == null) return Notice(NoticeCode.Refused);

            var world = new ProgenitorWorld(ctx.Inventory, ctx.Stations, island.Castles, id => island.State.Deposit.TryGetValue(id, out var d) ? d : null, logistics: island.State)
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
                    if (island.Relation is not (CastleRelation.Owner or CastleRelation.ClanMember)) return Notice(NoticeCode.Refused);
                    var r = PlayerActions.Tidy(world, deposit, island.State.Graph);
                    ReportAction(ctx, half, "Tidied", userEntity, user, world, deposit.Catalog, island, r);
                    return Notice(NoticeCode.Tidied, r.Items, r.Containers(byDestination: true));
                }
                case ActionKind.Stash:
                {
                    if (!server.AllowQuickStash) return Notice(NoticeCode.Refused);
                    var character = user.LocalCharacter._Entity;
                    var bagRef = ctx.Inventory.Of(character);
                    var bagId = world.AddBag(bagRef, player: true);
                    var stashable = ctx.Inventory.Read(bagRef).Slots
                        .Where(s => s.Slot >= HotbarSlots && s.Item != 0 && s.Amount > 0)
                        .Select(s => new ItemStack(new ItemId(s.Item), s.Amount))
                        .ToList();
                    var r = PlayerActions.Stash(world, deposit, bagId, stashable);
                    ReportAction(ctx, half, "Stashed", userEntity, user, world, deposit.Catalog, island, r);
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
