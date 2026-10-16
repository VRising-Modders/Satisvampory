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
                var castles = _ctx.Castles.All().Where(c => c.Enabled).ToList();
                var outNow = new HashSet<string>();
                foreach (var castle in castles)
                {
                    var heart = castle.Heart.ToEntity();
                    var missions = _ctx.Servants.Missions(heart);
                    if (missions.Count == 0) continue;
                    var names = _ctx.Servants.List(heart).ToDictionary(x => x.Servant, x => x.Name);
                    foreach (var m in missions)
                    {
                        outNow.Add(Remember(castle, m, names));
                        var key = $"{castle.Territory.Index}:{m.Row}:{m.StartedUtc.Ticks}";
                        seen.Add(key);
                        if (!_knownHunts.Add(key) || !_seeded) continue;
                        var who = string.Join(", ", m.Servants.Select(s => names.TryGetValue(s, out var n) ? n : "a servant"));
                        var repeat = server.AllowRepeatHunts && Stores.Castle(_ctx.Save, castle.Territory.Index).RepeatHunts
                            && _half.PlayerFor(castle.Owner.PlatformId).RepeatMyHunts;
                        var hours = m.LengthSeconds / 3600f;
                        var text = $"{Labels.Name(_ctx.Save, castle)}: {who} sent on {m.MissionName} ({(hours >= 1 ? $"{hours:0.#} h" : $"{m.LengthSeconds / 60f:0} min")})"
                            + $" · repeat hunts {(repeat ? "on" : "off")}"
                            + (repeat ? $" · success cap {server.RepeatHuntSuccessCap}%" : "");
                        foreach (var user in ClanOnline(castle)) Notify(user, text);
                    }
                }
                _knownHunts.IntersectWith(seen);
                _seeded = true;
                CatchUp(castles, outNow, server);
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
                    // The servants' live inventories, read now: the event's own references can be stale
                    // by the time a handler runs, and then nothing would move.
                    var home = _ctx.Servants.List(castle.Heart.ToEntity()).ToDictionary(x => x.Servant);
                    var bags = alive.Select(s => (home.TryGetValue(s.Servant, out var info) ? info.Inventory : s.Inventory, s.Outcome == ServantOutcome.Injured)).ToList();
                    var moved = StashLoot(castle, bags, server);
                    if (moved > 0) lines.Add($"  loot stashed ({moved:N0} items)");
                    else _ctx.Log.LogInfo($"Satisvampory: returning servants' loot on {plot} found no home ({bags.Count} servant(s))");
                }

                Forget(HuntKey(e.Territory, e.MissionGuid));
                var castleSettings = Stores.Castle(_ctx.Save, castle.Territory.Index);
                var clanOnline = ClanOnline(castle);
                var resend = Satisvampory.Engine.Stations.Servants.Resend(
                    alive.Select(s => new EngineServant(default, true, s.Outcome == ServantOutcome.Injured)),
                    new Satisvampory.Engine.Stations.Servants.RepeatRules(server.AllowRepeatHunts, server.RepeatHuntsOnlyWhileClanOnline,
                        castleSettings.RepeatHunts, castle.Raided, clanOnline.Count > 0, owner.RepeatMyHunts));
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

        // ---------------- hunts across restarts ----------------

        /// <summary>A hunt seen out, kept in the save so a return the server missed (it ended while
        /// the server was down, or before this mod loaded) can still be handled.</summary>
        sealed class OutHunt
        {
            public int Territory { get; set; }
            public int MissionGuid { get; set; }
            public string MissionName { get; set; } = "";
            public int DataSlot { get; set; }
            public List<string> Servants { get; set; } = new();
        }

        const string HuntsKey = "hunts/out";
        /// <summary>How long a hunt must be gone before it counts as missed (a live return is announced sooner).</summary>
        const double MissedAfterSeconds = 60;
        readonly Dictionary<string, double> _goneSince = new();
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        static string HuntKey(int territory, int mission) => $"{territory}:{mission}";

        Dictionary<string, OutHunt> OutHunts() => _ctx.Save.Get<Dictionary<string, OutHunt>>(HuntsKey) ?? new();

        string Remember(ICastleHandle castle, ServantMission m, Dictionary<CastleId, string> names)
        {
            var key = HuntKey(castle.Territory.Index, m.MissionGuid);
            var hunts = OutHunts();
            var servants = m.Servants.Select(s => names.TryGetValue(s, out var n) ? n : "").Where(n => n.Length > 0).ToList();
            if (hunts.TryGetValue(key, out var had) && had.DataSlot == m.DataSlot && had.Servants.SequenceEqual(servants)) return key;
            hunts[key] = new OutHunt { Territory = castle.Territory.Index, MissionGuid = m.MissionGuid, MissionName = m.MissionName, DataSlot = m.DataSlot, Servants = servants };
            _ctx.Save.Set(HuntsKey, hunts);
            return key;
        }

        void Forget(string key)
        {
            _goneSince.Remove(key);
            var hunts = OutHunts();
            if (hunts.Remove(key)) _ctx.Save.Set(HuntsKey, hunts);
        }

        /// <summary>
        /// Hunts the save says were out that are no longer in their heart's record and whose return
        /// nobody announced: stash the loot and, when repeat hunts allow it, send them again.
        /// </summary>
        void CatchUp(List<ICastleHandle> castles, HashSet<string> outNow, Engine.Persistence.ServerSettings server)
        {
            var now = _clock.Elapsed.TotalSeconds;
            foreach (var (key, hunt) in OutHunts())
            {
                if (outNow.Contains(key)) { _goneSince.Remove(key); continue; }
                var castle = castles.FirstOrDefault(c => c.Territory.Index == hunt.Territory);
                if (castle == null) continue; // castle not loaded yet, or gone: keep it for now
                if (!_goneSince.TryGetValue(key, out var since)) { _goneSince[key] = now; continue; }
                if (now - since < MissedAfterSeconds) continue;
                Forget(key);
                try { HandleMissedReturn(castle, hunt, server); }
                catch (Exception e) { _ctx.Log.LogError($"Satisvampory: missed hunt return on plot {hunt.Territory} failed: {e.Message}"); }
            }
        }

        void HandleMissedReturn(ICastleHandle castle, OutHunt hunt, Engine.Persistence.ServerSettings server)
        {
            var heart = castle.Heart.ToEntity();
            List<ServantInfo> Mine() => _ctx.Servants.List(heart).Where(s => hunt.Servants.Contains(s.Name)).ToList();
            var servants = Mine();
            if (servants.Count == 0) return;
            // Already back out (the live path or a player sent them): nothing was missed.
            if (servants.Any(s => s.State == Progenitor.Servants.ServantState.AwayOnHunt)) return;

            var owner = _half.PlayerFor(castle.Owner.PlatformId);
            var plot = Labels.Name(_ctx.Save, castle);
            var lines = new List<string> { $"{plot}: {string.Join(", ", servants.Select(s => s.Name))} came back from {hunt.MissionName} while the server was not watching" };
            var alive = servants.Where(s => s.State != Progenitor.Servants.ServantState.Dead).ToList();
            if (server.AllowServantLootStash && owner.ServantLootStash && alive.Any(s => s.ItemStacks > 0))
            {
                var moved = StashLoot(castle, alive.Select(s => (s.Inventory, s.State == Progenitor.Servants.ServantState.Injured)), server);
                if (moved > 0) lines.Add($"  loot stashed ({moved:N0} items)");
            }

            var clanOnline = ClanOnline(castle);
            var resend = Satisvampory.Engine.Stations.Servants.Resend(
                alive.Select(s => new EngineServant(default, true, s.State == Progenitor.Servants.ServantState.Injured)),
                new Satisvampory.Engine.Stations.Servants.RepeatRules(server.AllowRepeatHunts, server.RepeatHuntsOnlyWhileClanOnline,
                    Stores.Castle(_ctx.Save, castle.Territory.Index).RepeatHunts, castle.Raided, clanOnline.Count > 0, owner.RepeatMyHunts));
            if (resend.Count > 0)
            {
                var ready = Mine().Where(s => s.State == Progenitor.Servants.ServantState.Free).Select(s => s.Servant).ToList();
                if (ready.Count == 0) lines.Add("  could not send them again: none is home with empty hands");
                else
                {
                    var r = _ctx.Servants.Send(new ServantSendRequest
                    {
                        Heart = castle.Heart, Servants = ready, MissionGuid = hunt.MissionGuid, DataSlot = hunt.DataSlot, CheckDiscovered = false,
                    });
                    if (!r.Ok && r.Refusal is ServantSendRefusal.ZoneBusy or ServantSendRefusal.ServantBusy) return; // already back out
                    lines.Add(r.Ok ? "  sending them again" : "  could not send them again: " + r.Reason);
                }
            }

            var text = string.Join("\n", lines);
            _ctx.Log.LogInfo("Satisvampory: " + text.Replace("\n", " |"));
            foreach (var user in clanOnline) Notify(user, text);
            Log(castle.Owner.PlatformId, text, plot);
        }

        static string Outcome(ServantOutcome o) => o switch
        {
            ServantOutcome.Dead => " (died)",
            ServantOutcome.Injured => " (injured)",
            _ => "",
        };

        /// <summary>Loot goes to Mass Deposit homes on the castle's island, its own plot first, then spoils chests.</summary>
        int StashLoot(ICastleHandle castle, List<ReturnedServant> servants, Engine.Persistence.ServerSettings server) =>
            StashLoot(castle, servants.Select(s => (s.Inventory, s.Outcome == ServantOutcome.Injured)), server);

        int StashLoot(ICastleHandle castle, IEnumerable<(Progenitor.Inventory.InventoryRef Inventory, bool Injured)> servants, Engine.Persistence.ServerSettings server)
        {
            var all = _ctx.Castles.All().Where(c => c.Enabled).ToList();
            var (key, members) = _half.Ticker!.IslandOf(castle, all, Ticker.Sharing(server));
            var state = Stores.Load(_ctx.Save, key, ProgenitorWorld.ContainerIds(members, cachedRows: true));
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, members, id => state.Deposit.TryGetValue(id, out var d) ? d : null, cachedRows: true, logistics: state);
            var home = new DepositContext(_half.Catalog.Current.WithEdits(state.Categories), world, members.Select(ProgenitorWorld.PlotOf).ToList(), ProgenitorWorld.PlotOf(castle))
            {
                Settings = state.MassDeposit,
                Belts = state.Graph.BeltMembers(),
            };
            var states = servants.Where(s => !s.Inventory.IsNone).Select(s => new EngineServant(world.AddBag(s.Inventory), true, s.Injured)).ToList();
            var moved = Satisvampory.Engine.Stations.Servants.StashLoot(world, home, states).Sum(m => m.Amount);
            var left = states.SelectMany(st => world.Contents(st.Inventory)).GroupBy(x => x.Item).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            if (left.Count > 0)
                _ctx.Log.LogInfo($"Satisvampory: servant loot left after stash on plot {castle.Territory.Index} (moved {moved}): "
                    + string.Join(", ", left.Select(kv => $"{kv.Key.Guid} x{kv.Value} (homes {DepositPlanner.Plan(home, kv.Key, kv.Value).Sum(x => x.Amount)})"))
                    + $"; {states.Count} servant(s), {world.ContainersOn(home.Standing).Count(c => c.Deposit.Special == SpecialKind.Spoils)} spoils chest(s)");
            return moved;
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
