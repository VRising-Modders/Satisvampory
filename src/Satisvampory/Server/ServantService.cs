// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Servants;
using ProjectM.Network;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Satisvampory.Net;
using Satisvampory.World;
using Unity.Entities;
using EngineServant = Satisvampory.Engine.Stations.ServantState;

namespace Satisvampory.Server
{
    /// <summary>
    /// Servants (spec 08) over Progenitor's ctx.Servants: returning loot goes home by Mass
    /// Deposit, repeat hunts resend healthy survivors, clan members get a notice, owners keep a
    /// hunt log, and a throne can show every castle on the player's island.
    /// </summary>
    internal sealed class ServantService : IServantListProvider
    {
        const int LogHours = 72;
        const int LogMax = 200;

        readonly SatisContext _ctx;
        readonly ServerHalf _half;

        internal ServantService(SatisContext ctx, ServerHalf half)
        {
            _ctx = ctx;
            _half = half;
        }

        internal void Attach()
        {
            _ctx.Events.On<ServantsReturned>(OnReturned);
            _ctx.Track(_ctx.Servants.RegisterListProvider(this));
            _ctx.Requests.Handle<GetHuntLogReq, HuntLogReply>((u, q, i) => HuntLog(u));
            _ctx.Track(new OnDispose(() => _stopped = true));
            Progenitor.Dev.MainThread.PostDelayed(SentPollSeconds, PollSent, _ctx.Owner);
        }

        // ---------------- sent notices (spec 08) ----------------

        const double SentPollSeconds = 15;
        bool _stopped;
        bool _seeded;
        readonly HashSet<string> _knownHunts = new();

        /// <summary>
        /// Watches every castle's hunts and tells the clan when one leaves: who went, where, for how
        /// long, and whether repeat hunts are on. The first pass only learns what is already out.
        /// </summary>
        void PollSent()
        {
            if (_stopped) return;
            try
            {
                var server = _half.Config.Snapshot();
                var seen = new HashSet<string>();
                foreach (var castle in _ctx.Castles.All().Where(c => c.Enabled))
                {
                    var heart = castle.Heart.ToEntity();
                    var missions = _ctx.Servants.Missions(heart);
                    if (missions.Count == 0) continue;
                    var names = _ctx.Servants.List(heart).ToDictionary(x => x.Servant, x => x.Name);
                    foreach (var m in missions)
                    {
                        var key = $"{castle.Territory.Index}:{m.Row}:{m.StartedUtc.Ticks}";
                        seen.Add(key);
                        if (!_knownHunts.Add(key) || !_seeded) continue;
                        var who = string.Join(", ", m.Servants.Select(s => names.TryGetValue(s, out var n) ? n : "a servant"));
                        var repeat = server.AllowRepeatHunts && Stores.Castle(_ctx.Save, castle.Territory.Index).RepeatHunts;
                        var hours = m.LengthSeconds / 3600f;
                        var text = $"{Labels.Name(_ctx.Save, castle)}: {who} sent on {m.MissionName} ({(hours >= 1 ? $"{hours:0.#} h" : $"{m.LengthSeconds / 60f:0} min")})"
                            + $" · repeat hunts {(repeat ? "on" : "off")}"
                            + (repeat ? $" · success cap {server.RepeatHuntSuccessCap}%" : "");
                        foreach (var user in ClanOnline(castle)) Notify(user, text);
                    }
                }
                _knownHunts.IntersectWith(seen);
                _seeded = true;
            }
            catch (Exception e)
            {
                _ctx.Log.LogError("Satisvampory: hunt watch failed: " + e.Message);
            }
            finally
            {
                if (!_stopped) Progenitor.Dev.MainThread.PostDelayed(SentPollSeconds, PollSent, _ctx.Owner);
            }
        }

        // ---------------- returning servants ----------------

        void OnReturned(ServantsReturned e)
        {
            try
            {
                var server = _half.Config.Snapshot();
                var castle = _ctx.Castles.ForTerritory(e.Territory);
                if (!castle.Enabled) return;
                var owner = _half.PlayerFor(castle.Owner.PlatformId);
                var lines = new List<string>();
                var plot = Labels.Name(_ctx.Save, castle);

                var alive = e.Servants.Where(s => s.Outcome != ServantOutcome.Dead).ToList();
                var lootStacks = e.TotalLoot.Count;
                lines.Add($"{plot}: {string.Join(", ", e.Servants.Select(s => s.Name + Outcome(s.Outcome)))} back from {e.MissionName}"
                    + (lootStacks > 0 ? $" with {lootStacks} kinds of loot" : ""));

                if (server.AllowServantLootStash && owner.ServantLootStash && alive.Count > 0)
                {
                    var moved = StashLoot(castle, alive, server);
                    if (moved > 0) lines.Add($"  loot stashed ({moved:N0} items)");
                }

                var castleSettings = Stores.Castle(_ctx.Save, castle.Territory.Index);
                var clanOnline = ClanOnline(castle);
                var resend = Satisvampory.Engine.Stations.Servants.Resend(
                    alive.Select(s => new EngineServant(default, true, s.Outcome == ServantOutcome.Injured)),
                    new Satisvampory.Engine.Stations.Servants.RepeatRules(server.AllowRepeatHunts, server.RepeatHuntsOnlyWhileClanOnline,
                        castleSettings.RepeatHunts, castle.Raided, clanOnline.Count > 0));
                if (resend.Count > 0)
                {
                    var r = _ctx.Servants.Resend(e);
                    lines.Add(r.Ok ? "  sending them again" : "  could not send them again: " + r.Reason);
                }

                var text = string.Join("\n", lines);
                foreach (var user in clanOnline) Notify(user, text);
                Log(castle.Owner.PlatformId, text, plot);
            }
            catch (Exception ex)
            {
                _ctx.Log.LogError("Satisvampory: servant return failed: " + ex);
            }
        }

        static string Outcome(ServantOutcome o) => o switch
        {
            ServantOutcome.Dead => " (died)",
            ServantOutcome.Injured => " (injured)",
            _ => "",
        };

        /// <summary>Loot goes to Mass Deposit homes on the castle's island, its own plot first, then spoils chests.</summary>
        int StashLoot(ICastleHandle castle, List<ReturnedServant> servants, Engine.Persistence.ServerSettings server)
        {
            var all = _ctx.Castles.All().Where(c => c.Enabled).ToList();
            var (key, members) = _half.Ticker!.IslandOf(castle, all, Ticker.Sharing(server));
            var state = Stores.Load(_ctx.Save, key, ProgenitorWorld.ContainerIds(members, ProgenitorWorld.BackgroundRowsMs));
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, members, id => state.Deposit.TryGetValue(id, out var d) ? d : null, ProgenitorWorld.BackgroundRowsMs);
            var home = new DepositContext(_half.Catalog.Current.WithEdits(state.Categories), world, members.Select(ProgenitorWorld.PlotOf).ToList(), ProgenitorWorld.PlotOf(castle))
            {
                Settings = state.MassDeposit,
                Belts = state.Graph.BeltMembers(),
            };
            var states = servants.Where(s => !s.Inventory.IsNone).Select(s => new EngineServant(world.AddBag(s.Inventory), true, s.Outcome == ServantOutcome.Injured)).ToList();
            return Satisvampory.Engine.Stations.Servants.StashLoot(world, home, states).Sum(m => m.Amount);
        }

        /// <summary>Online users of the castle's clan, or its owner when clanless.</summary>
        List<Entity> ClanOnline(ICastleHandle castle)
        {
            var list = new List<Entity>();
            var clan = _ctx.Clans.Of(castle.Owner.User.ToEntity());
            if (!clan.IsNone)
                foreach (var m in _ctx.Clans.Members(clan))
                    if (m.IsConnected) list.Add(m.User.ToEntity());
            if (list.Count == 0 && _ctx.Players.TryGetByAccount(castle.Owner.PlatformId, out var p) && p.IsOnline && _ctx.Players.IsFresh(p))
                list.Add(castle.Owner.User.ToEntity());
            return list;
        }

        void Notify(Entity user, string text) =>
            _ctx.Net.SendToUser(user, EventIds.Text, TextPacket.Of(ActionKind.Notice, text));

        // ---------------- hunt log ----------------

        static string LogKey(ulong platformId) => "huntlog/" + platformId;

        void Log(ulong owner, string text, string plot)
        {
            var entries = _ctx.Save.Get<List<HuntLogEntry>>(LogKey(owner)) ?? new List<HuntLogEntry>();
            var cutoff = DateTime.UtcNow.AddHours(-LogHours);
            entries.RemoveAll(x => x.Utc < cutoff);
            entries.Add(new HuntLogEntry { Utc = DateTime.UtcNow, Text = text, Plot = plot });
            if (entries.Count > LogMax) entries.RemoveRange(0, entries.Count - LogMax);
            _ctx.Save.Set(LogKey(owner), entries);
        }

        HuntLogReply HuntLog(Entity userEntity)
        {
            var user = _ctx.World!.EntityManager.GetComponentData<User>(userEntity);
            var reply = new HuntLogReply();
            var entries = _ctx.Save.Get<List<HuntLogEntry>>(LogKey(user.PlatformId)) ?? new List<HuntLogEntry>();
            // Spec 08: grouped by plot label, newest first within each.
            foreach (var group in entries.GroupBy(x => string.IsNullOrEmpty(x.Plot) ? "Other" : x.Plot).OrderByDescending(g => g.Max(x => x.Utc)))
            {
                reply.Lines.Add(group.Key);
                foreach (var e in group.OrderByDescending(x => x.Utc))
                    reply.Lines.Add($"  {Ago(e.Utc)} — {e.Text}");
            }
            foreach (var castle in _ctx.Castles.AllForUser(userEntity).Where(c => c.Enabled))
                foreach (var s in _ctx.Servants.List(castle.Heart.ToEntity()))
                    reply.Servants.Add($"{Labels.Name(_ctx.Save, castle)}: {s.Name} — {s.State}");
            return reply;
        }

        static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            return d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago" : d.TotalHours < 24 ? $"{(int)d.TotalHours} h ago" : $"{(int)d.TotalDays} d ago";
        }

        // ---------------- throne targeting ----------------

        /// <summary>
        /// Which castles' servants a throne shows to this player: every castle on their island when
        /// they are a member of the castle's clan and sharing joins more than one castle; otherwise
        /// vanilla. This is the authorization boundary for cross-castle sends.
        /// </summary>
        public IReadOnlyList<CastleId>? HeartsFor(ServantListRequest request)
        {
            try
            {
                var userEntity = request.User.ToEntity();
                var user = _ctx.World!.EntityManager.GetComponentData<User>(userEntity);
                var island = ServerActions.Resolve(_ctx, _half, userEntity, user);
                if (island == null || island.Castles.Count <= 1) return null;
                if (island.Relation is not (CastleRelation.Owner or CastleRelation.ClanMember)) return null;
                if (ProgenitorWorld.PlotOf(island.Standing).Value != (ulong)(uint)request.Territory) return null;
                return island.Castles.Where(c => !c.Raided).Select(c => c.Heart).ToList();
            }
            catch (Exception e)
            {
                _ctx.Log.LogError("Satisvampory: throne list failed: " + e.Message);
                return null;
            }
        }
    }

    internal sealed class HuntLogEntry
    {
        public DateTime Utc { get; set; }
        public string Text { get; set; } = "";
        public string Plot { get; set; } = "";
    }
}
