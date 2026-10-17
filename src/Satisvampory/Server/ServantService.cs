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
    /// Deposit, repeat hunts resend healthy survivors, clan members get a notice, and owners keep a
    /// hunt log. Thrones list only their own castle's servants (vanilla).
    /// </summary>
    internal sealed class ServantService
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
                        foreach (var user in ClanOnline(castle)) Notify(user, SentText(castle, m, names, server));
                    }
                }
                _knownHunts.IntersectWith(seen);
                _seeded = true;
                CatchUp(castles, outNow, server);
                RetryHeld(castles, server);
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
                var trip = CountTrip(e.Territory, e.MissionGuid);
                var detail = new List<string>();
                lines.Add($"{plot}: {string.Join(", ", e.Servants.Select(s => s.Name + Outcome(s.Outcome) + (s.CapForcedFailure ? " (the repeat-hunt cap)" : "")))} back from {Mission(e.MissionGuid, e.MissionName)}{TripText(trip)}"
                    + (lootStacks > 0 ? $" with {lootStacks} kinds of loot" : ""));

                if (server.AllowServantLootStash && owner.ServantLootStash && alive.Count > 0)
                {
                    // The servants' live inventories, read now: the event's own references can be stale
                    // by the time a handler runs, and then nothing would move.
                    var home = _ctx.Servants.List(castle.Heart.ToEntity()).ToDictionary(x => x.Servant);
                    var bags = alive.Select(s => (home.TryGetValue(s.Servant, out var info) ? info.Inventory : s.Inventory, s.Outcome == ServantOutcome.Injured)).ToList();
                    var moved = StashLoot(castle, bags, server, out detail);
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
                var again = false;
                if (resend.Count > 0)
                {
                    var r = _ctx.Servants.Resend(e, RepeatCap(server));
                    again = r.Ok;
                    if (!r.Ok && r.Refusal == ServantSendRefusal.ServantHasItems)
                    {
                        Hold(new OutHunt { Territory = e.Territory, MissionGuid = e.MissionGuid, MissionName = e.MissionName, DataSlot = e.DataSlot,
                            Servants = alive.Select(s => s.Name).ToList() });
                        lines.Add("  their bags are still full: stashing and sending them again as soon as there is room");
                    }
                    else lines.Add(r.Ok ? "  sending them again" : "  could not send them again: " + r.Reason);
                }
                EndTrip(e.Territory, e.MissionGuid, again);

                var text = string.Join("\n", lines);
                foreach (var user in clanOnline) Notify(user, text);
                Log(castle.Owner.PlatformId, text, plot);
                ChatReport(castle, lines, detail);
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
            var trip = CountTrip(hunt.Territory, hunt.MissionGuid);
            var detail = new List<string>();
            var lines = new List<string> { $"{plot}: {string.Join(", ", servants.Select(s => s.Name))} came back from {Mission(hunt.MissionGuid, hunt.MissionName)}{TripText(trip)} while the server was not watching" };
            var alive = servants.Where(s => s.State != Progenitor.Servants.ServantState.Dead).ToList();
            if (server.AllowServantLootStash && owner.ServantLootStash && alive.Any(s => s.ItemStacks > 0))
            {
                var moved = StashLoot(castle, alive.Select(s => (s.Inventory, s.State == Progenitor.Servants.ServantState.Injured)), server, out detail);
                if (moved > 0) lines.Add($"  loot stashed ({moved:N0} items)");
            }

            var clanOnline = ClanOnline(castle);
            var again = false;
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
                        MaxSuccess = RepeatCap(server),
                    });
                    if (!r.Ok && r.Refusal is ServantSendRefusal.ZoneBusy or ServantSendRefusal.ServantBusy) return; // already back out
                    if (!r.Ok && r.Refusal == ServantSendRefusal.ServantHasItems)
                    {
                        Hold(hunt);
                        lines.Add("  their bags are still full: stashing and sending them again as soon as there is room");
                    }
                    else lines.Add(r.Ok ? "  sending them again" : "  could not send them again: " + r.Reason);
                    again = r.Ok;
                }
            }
            EndTrip(hunt.Territory, hunt.MissionGuid, again);

            var text = string.Join("\n", lines);
            _ctx.Log.LogInfo("Satisvampory: " + ForLog(text).Replace("\n", " |"));
            foreach (var user in clanOnline) Notify(user, text);
            Log(castle.Owner.PlatformId, text, plot);
            ChatReport(castle, lines, detail);
        }

        static string Outcome(ServantOutcome o) => o switch
        {
            ServantOutcome.Dead => " (died)",
            ServantOutcome.Injured => " (injured)",
            _ => "",
        };

        /// <summary>Loot goes to Mass Deposit homes on the castle's island, its own plot first, then spoils chests.</summary>
        int StashLoot(ICastleHandle castle, List<ReturnedServant> servants, Engine.Persistence.ServerSettings server) =>
            StashLoot(castle, servants.Select(s => (s.Inventory, s.Outcome == ServantOutcome.Injured)), server, out _);

        int StashLoot(ICastleHandle castle, IEnumerable<(Progenitor.Inventory.InventoryRef Inventory, bool Injured)> servants, Engine.Persistence.ServerSettings server,
            out List<string> detail)
        {
            _half.Catalog.Refresh(_ctx); // cheap when unchanged; never stash against an empty catalog
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
            var moves = Satisvampory.Engine.Stations.Servants.StashLoot(world, home, states);
            var moved = moves.Sum(m => m.Amount);
            var left = states.SelectMany(st => world.Contents(st.Inventory)).GroupBy(x => x.Item).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            // What came home first, then where it went ("stored in" sets it apart from a quick stash).
            detail = ServerActions.LootLines(moves.Select(m => (m.Item, m.Amount)).Concat(left.Select(kv => (kv.Key, kv.Value))));
            var plotNames = ServerActions.PlotNames(_ctx.Save, members);
            detail.AddRange(ServerActions.MoveLines(world, home.Catalog, plotNames,
                moves.Select(m => (m.To, m.Item, m.Amount)), left, "the servants' bags", ServerActions.ChestLinks(_ctx, world, plotNames)).Select(IntoLine));
            if (left.Count > 0)
                _ctx.Log.LogInfo($"Satisvampory: servant loot left after stash on plot {castle.Territory.Index} (moved {moved}): "
                    + string.Join(", ", left.Select(kv => $"{kv.Key.Guid} x{kv.Value} (homes {DepositPlanner.Plan(home, kv.Key, kv.Value).Sum(x => x.Amount)})"))
                    + $"; {states.Count} servant(s), {world.ContainersOn(home.Standing).Count(c => c.Deposit.Special == SpecialKind.Spoils)} spoils chest(s)"
                    // Why nothing had a home: no containers seen, none taking anything, or no free space read.
                    + $"; island {key}: {members.Count} castle(s), {home.Scope.Count} plot(s), "
                    + $"{home.Scope.Sum(p => world.ContainersOn(p).Count(c => c.Kind == NodeKind.Chest))} chest(s), "
                    + $"{home.Scope.Sum(p => world.ContainersOn(p).Count(c => c.Kind == NodeKind.Chest && c.Deposit.Takes == Takes.Anything))} take anything, "
                    + $"free for {left.Keys.First().Guid}: {home.Scope.SelectMany(p => world.ContainersOn(p)).Where(c => c.Kind == NodeKind.Chest).Sum(c => world.FreeSpace(c.Id, left.Keys.First()))}");
            return moved;
        }

        /// <summary>
        /// Who left, where to, for how long, and whether the hunt will repeat (with its trip number and the
        /// success chance once Progenitor reports it).
        /// </summary>
        string SentText(ICastleHandle castle, ServantMission m, Dictionary<CastleId, string> names, Engine.Persistence.ServerSettings server)
        {
            var who = m.Servants.Select(s => names.TryGetValue(s, out var n) ? n : "a servant").ToList();
            var whoText = who.Count <= 1 ? string.Join("", who) : string.Join(", ", who.Take(who.Count - 1)) + " and " + who[who.Count - 1];
            var minutes = m.LengthSeconds / 60f;
            var length = minutes >= 60 ? $"{minutes / 60f:0.#} h" : $"{minutes:0} min";
            var lines = new List<string>
            {
                $"{Labels.Name(_ctx.Save, castle)}: {whoText} left to hunt {Mission(m.MissionGuid, m.MissionName)} · back in {length}",
            };
            if (m.Chances.Count > 0) lines.Add(SuccessText(m.Chances, names));

            var castleOn = Stores.Castle(_ctx.Save, castle.Territory.Index).RepeatHunts;
            var ownerOn = _half.PlayerFor(castle.Owner.PlatformId).RepeatMyHunts;
            if (server.AllowRepeatHunts && castleOn && ownerOn)
            {
                var trips = _ctx.Save.Get<Dictionary<string, int>>(TripsKey);
                var trip = (trips != null && trips.TryGetValue(HuntKey(castle.Territory.Index, m.MissionGuid), out var done) ? done : 0) + 1;
                lines.Add($"Repeat hunt: on, trip {trip}"
                    + (RepeatCap(server) is { } cap ? $" · repeats succeed at most {cap * 100:0}%" : "")
                    + (server.RepeatHuntsOnlyWhileClanOnline ? " · only while the clan is online" : ""));
            }
            else
            {
                var why = !server.AllowRepeatHunts ? "the server does not allow it" : !castleOn ? "off for this castle" : "off in the owner's settings";
                lines.Add($"Repeat hunt: off ({why})");
            }
            return string.Join("\n", lines);
        }

        /// <summary>Readable names of missions seen, for the server log (the game's names live on clients).</summary>
        readonly Dictionary<int, string> _missionNames = new();

        /// <summary>A mission as a <c>[mission:guid]</c> token: chat and Satisvampory's client print the game's name.</summary>
        /// <summary>A mission as "Fishing Lake (Farbane)": the game's name by token, then its region from the prefab.</summary>
        string Mission(int guid, string prefab)
        {
            var region = Region(prefab);
            // The log's stand-in for the token: the place alone, since the region follows it.
            var pretty = ProgenitorWorld.PrettyMission(prefab);
            var paren = pretty.IndexOf(" (", StringComparison.Ordinal);
            _missionNames[guid] = paren > 0 ? pretty.Substring(0, paren) : pretty;
            return _ctx.Servants.MissionToken(guid) + (region.Length > 0 ? $" ({region})" : "");
        }

        /// <summary>"ServantMission_Silverlight_Brighthaven_Harbor" → "Silverlight": the prefab's region word.</summary>
        static string Region(string prefab)
        {
            var parts = (prefab ?? "").Split('_', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3 && parts[0].Equals("ServantMission", StringComparison.OrdinalIgnoreCase) ? parts[1] : "";
        }

        /// <summary>"20 [Iron Ore] → Metal1 (Plot 6)" reads as "into Metal1 (Plot 6): 20 [Iron Ore]" under a hunt.</summary>
        static string IntoLine(string line)
        {
            var at = line.LastIndexOf(" → ", StringComparison.Ordinal);
            return at < 0 ? line : $"into {line.Substring(at + 3)}: {line.Substring(0, at)}";
        }

        static readonly System.Text.RegularExpressions.Regex MissionTokens = new(@"\[mission:(-?\d+)\]");

        string ForLog(string text) => MissionTokens.Replace(text, m =>
            int.TryParse(m.Groups[1].Value, out var g) && _missionNames.TryGetValue(g, out var n) ? n : "a hunt");

        /// <summary>"Success: 100%", or per servant when they differ ("Alexander 87%, PewPewPew 92%").</summary>
        static string SuccessText(IReadOnlyList<ServantChance> chances, Dictionary<CastleId, string> names)
        {
            static string Pct(float f) => $"{Math.Round(f * 100):0}%";
            var applied = chances.Select(c => c.Applied).Distinct().ToList();
            var text = applied.Count == 1
                ? "Success: " + Pct(applied[0])
                : "Success: " + string.Join(", ", chances.Select(c => $"{(names.TryGetValue(c.Servant, out var n) ? n : "a servant")} {Pct(c.Applied)}"));
            // A capped repeat shows what the cap cost ("game 100%, capped for repeat hunts").
            if (chances.Any(c => c.MaxSuccess is { } cap && cap < c.Vanilla))
                text += $" (game {string.Join("/", chances.Select(c => Pct(c.Vanilla)).Distinct())}, capped for repeat hunts)";
            return text;
        }

        /// <summary>
        /// The success cap a repeat send carries (risk for the trips it saves): null at 100%, where the game's
        /// own chance stands. A capped failure is the game's: the servant dies.
        /// </summary>
        static float? RepeatCap(Engine.Persistence.ServerSettings server) =>
            server.RepeatHuntSuccessCap >= 100 ? null : Math.Clamp(server.RepeatHuntSuccessCap, 1, 99) / 100f;

        // ---------------- repeats held back by full bags ----------------

        /// <summary>A repeat the game refused because the servants still carried loot (no room to stash it).</summary>
        sealed class HeldHunt
        {
            public OutHunt Hunt { get; set; } = new();
            public DateTime Since { get; set; }
        }

        const string HeldKey = "hunts/held";
        /// <summary>How long a held repeat keeps trying before it gives up.</summary>
        static readonly TimeSpan HoldFor = TimeSpan.FromHours(1);
        double _lastHeldTry;

        Dictionary<string, HeldHunt> HeldHunts() => _ctx.Save.Get<Dictionary<string, HeldHunt>>(HeldKey) ?? new();

        void Hold(OutHunt hunt)
        {
            var held = HeldHunts();
            held[HuntKey(hunt.Territory, hunt.MissionGuid)] = new HeldHunt { Hunt = hunt, Since = DateTime.UtcNow };
            _ctx.Save.Set(HeldKey, held);
        }

        /// <summary>
        /// About once a minute: stash what held servants carry and, once their bags are empty, send them out
        /// again (with the repeat cap). Gives up when they are already out, dead, or after an hour.
        /// </summary>
        void RetryHeld(List<ICastleHandle> castles, Engine.Persistence.ServerSettings server)
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (now - _lastHeldTry < 60) return;
            _lastHeldTry = now;
            var held = HeldHunts();
            if (held.Count == 0) return;
            var changed = false;
            foreach (var (key, h) in held.ToList())
            {
                var hunt = h.Hunt;
                var castle = castles.FirstOrDefault(c => c.Territory.Index == hunt.Territory);
                if (castle == null) continue;
                string? outcome = null;
                try
                {
                    var heart = castle.Heart.ToEntity();
                    List<ServantInfo> Mine() => _ctx.Servants.List(heart).Where(s => hunt.Servants.Contains(s.Name)).ToList();
                    var servants = Mine();
                    if (servants.Count == 0 || servants.Any(s => s.State == Progenitor.Servants.ServantState.AwayOnHunt)
                        || servants.All(s => s.State == Progenitor.Servants.ServantState.Dead))
                    { held.Remove(key); changed = true; continue; }
                    var loaded = servants.Where(s => s.ItemStacks > 0).ToList();
                    var stashed = 0;
                    var detail = new List<string>();
                    if (loaded.Count > 0)
                        stashed = StashLoot(castle, loaded.Select(s => (s.Inventory, s.State == Progenitor.Servants.ServantState.Injured)), server, out detail);
                    var ready = Mine().Where(s => s.State == Progenitor.Servants.ServantState.Free).Select(s => s.Servant).ToList();
                    if (ready.Count > 0 && ready.Count == Mine().Count(s => s.State != Progenitor.Servants.ServantState.Dead))
                    {
                        var r = _ctx.Servants.Send(new ServantSendRequest
                        {
                            Heart = castle.Heart, Servants = ready, MissionGuid = hunt.MissionGuid, DataSlot = hunt.DataSlot, CheckDiscovered = false,
                            MaxSuccess = RepeatCap(server),
                        });
                        if (r.Ok || r.Refusal is not ServantSendRefusal.ServantHasItems)
                        {
                            held.Remove(key); changed = true;
                            outcome = r.Ok ? $"stashed {stashed:N0} items they still carried and sent them again" : "could not send them again: " + r.Reason;
                        }
                    }
                    if (outcome == null && DateTime.UtcNow - h.Since > HoldFor)
                    {
                        held.Remove(key); changed = true;
                        outcome = "gave up sending them again: their loot still has no room in storage";
                    }
                    if (outcome != null)
                    {
                        var plot = Labels.Name(_ctx.Save, castle);
                        var text = $"{plot}: {string.Join(", ", hunt.Servants)} ({Mission(hunt.MissionGuid, hunt.MissionName)}): {outcome}";
                        _ctx.Log.LogInfo("Satisvampory: " + ForLog(text));
                        foreach (var user in ClanOnline(castle)) Notify(user, text);
                        Log(castle.Owner.PlatformId, text, plot);
                        if (detail.Count > 0) ServerActions.Report(_ctx, _half, text, detail, ClanOnlineAccounts(castle), ForLog);
                    }
                }
                catch (Exception e)
                {
                    _ctx.Log.LogError($"Satisvampory: held hunt on plot {hunt.Territory} failed: {e.Message}");
                }
            }
            if (changed) _ctx.Save.Set(HeldKey, held);
        }

        // ---------------- repeat-hunt trips ----------------

        const string TripsKey = "hunts/trips";

        /// <summary>This return's number in an unbroken run of repeat hunts (1 for the first).</summary>
        int CountTrip(int territory, int mission)
        {
            var trips = _ctx.Save.Get<Dictionary<string, int>>(TripsKey) ?? new();
            var key = HuntKey(territory, mission);
            var n = (trips.TryGetValue(key, out var had) ? had : 0) + 1;
            trips[key] = n;
            _ctx.Save.Set(TripsKey, trips);
            return n;
        }

        /// <summary>A hunt sent out again keeps its count; any other ending starts over next time.</summary>
        void EndTrip(int territory, int mission, bool sentAgain)
        {
            if (sentAgain) return;
            var trips = _ctx.Save.Get<Dictionary<string, int>>(TripsKey) ?? new();
            if (trips.Remove(HuntKey(territory, mission))) _ctx.Save.Set(TripsKey, trips);
        }

        static string TripText(int trip) => trip > 1 ? $" (trip {trip})" : "";

        /// <summary>The return and where its loot went, in chat for clan members who keep "Log my deposits" on.</summary>
        void ChatReport(ICastleHandle castle, List<string> lines, List<string> detail)
        {
            var body = lines.Skip(1).Select(l => l.Trim()).Where(l => !l.StartsWith("loot stashed")).Concat(detail).ToList();
            if (body.Count == 0) body.Add("no loot to stash");
            // After the game's own "returned" and "Hunt in … started" lines, so this hunt's block reads as one piece.
            ServerActions.Report(_ctx, _half, lines[0], body, ClanOnlineAccounts(castle), ForLog, delaySeconds: 1.5f);
        }

        List<(Entity User, ulong Account)> ClanOnlineAccounts(ICastleHandle castle)
        {
            var list = new List<(Entity, ulong)>();
            var clan = _ctx.Clans.Of(castle.Owner.User.ToEntity());
            if (!clan.IsNone)
                foreach (var m in _ctx.Clans.Members(clan))
                    if (m.IsConnected) list.Add((m.User.ToEntity(), m.PlatformId));
            if (list.Count == 0 && _ctx.Players.TryGetByAccount(castle.Owner.PlatformId, out var p) && p.IsOnline && _ctx.Players.IsFresh(p))
                list.Add((castle.Owner.User.ToEntity(), castle.Owner.PlatformId));
            return list;
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
    }

    internal sealed class HuntLogEntry
    {
        public DateTime Utc { get; set; }
        public string Text { get; set; } = "";
        public string Plot { get; set; } = "";
    }
}
