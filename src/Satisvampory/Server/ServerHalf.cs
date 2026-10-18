// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using ProjectM.Network;
using Satisvampory.Engine.Logging;
using Satisvampory.Config;
using Satisvampory.Engine.Persistence;
using Satisvampory.Net;
using Unity.Entities;

namespace Satisvampory.Server
{
    /// <summary>
    /// The server part: per-player settings received from clients (kept in the world save),
    /// action requests and admin requests. The engine reaches the world through Progenitor's
    /// inventory, castle and station APIs (<c>World/ProgenitorWorld</c>).
    /// </summary>
    internal sealed class ServerHalf
    {
        static ServerHalf? _active;

        readonly SatisContext _ctx;
        readonly ServerConfig _server;
        readonly Dictionary<ulong, PlayerSettings> _players = new();
        readonly Dictionary<ulong, uint> _lastBits = new();
        readonly Dictionary<ulong, double> _lastAction = new();
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        internal ServerHalf(SatisContext ctx, ServerConfig server)
        {
            _ctx = ctx;
            _server = server;
        }

        internal static ServerHalf? Active => _active;

        internal ServerConfig Config => _server;

        internal SatisContext Context => _ctx;

        internal CatalogProvider Catalog { get; } = new();

        /// <summary>Policy half of crafting from shared storage; registered with Progenitor's material source when that lands.</summary>
        internal Engine.World.MaterialPolicy MaterialPolicy { get; } = new();

        internal Ticker? Ticker { get; private set; }

        /// <summary>What Satisvampory moved, per castle, last 24 hours (the Log tab).</summary>
        internal MoveLog MoveLog { get; } = new();

        const string LogKey = "log/";

        void SaveLogLoop()
        {
            SaveLog();
            Progenitor.Dev.MainThread.PostDelayed(60, SaveLogLoop, _ctx.Owner);
        }

        /// <summary>The castles with new Log entries, written to the mod's store (saved with the world).</summary>
        void SaveLog()
        {
            try
            {
                foreach (var plot in MoveLog.TakeDirty())
                    _ctx.Save.Set(LogKey + plot.Value, MoveLog.CastleEntries(plot));
            }
            catch (System.Exception e) { _ctx.Log.LogWarning("Satisvampory: log not saved: " + e.Message); }
        }

        internal unsafe void Attach()
        {
            _active = this;
            _ctx.Track(new OnDispose(() => { if (ReferenceEquals(_active, this)) _active = null; }));
            _ctx.Net.OnReceiveFromClient(EventIds.PlayerPrefs, OnPrefs);
            _ctx.Net.OnReceiveFromClient(EventIds.Action, OnAction);
            Catalog.Aliases = () => Config.Snapshot().ItemAliases;
            // Allows, shares and pull rules change every player's craft-from-storage answers.
            System.Action serverChanged = MaterialPolicy.Invalidate;
            _server.Changed += serverChanged;
            _ctx.Track(new OnDispose(() => _server.Changed -= serverChanged));
            World.ProgenitorWorld.FailureLog = t => _ctx.Log.LogInfo(t);
            _ctx.Track(new OnDispose(() => World.ProgenitorWorld.FailureLog = null));
            _ctx.Track(new OnDispose(World.ProgenitorWorld.ForgetRows));
            var like = new PluginContextLike(_ctx);
            Labels.OwnerKeyOf = c => Stores.SettingsKey(like, c);
            _ctx.Track(new OnDispose(() => Labels.OwnerKeyOf = null));
            _ctx.Requests.Handle<AdminReq, Net.TextReply>((u, q, i) => AdminActions.Run(_ctx, this, u, q));
            Ticker = new Ticker(_ctx, this);
            Ticker.Attach();
            // The catalog first: a stash, a hunt return or a report right after load used an empty one
            // (no homes for anything, ids for names) until the first tick filled it.
            try { Catalog.Refresh(_ctx); }
            catch (System.Exception e) { _ctx.Log.LogWarning("Satisvampory: item catalog not ready at load: " + e.Message); }
            new PanelService(_ctx, this).Attach();
            new HubService(_ctx, this).Attach();
            new ServantService(_ctx, this).Attach();
            new MaterialService(_ctx, this).Attach();
            // The Log lives with the world save, one key per castle: it survives reloads and restarts.
            _ctx.Track(_ctx.Save.WhenReady(store =>
            {
                foreach (var key in store.Keys)
                    if (key.StartsWith(LogKey, System.StringComparison.Ordinal) && ulong.TryParse(key.Substring(LogKey.Length), out var plot))
                        MoveLog.ImportCastle(new Engine.Model.PlotId(plot), store.Get<List<MoveLog.Entry>>(key));
            }));
            Progenitor.Dev.MainThread.PostDelayed(60, SaveLogLoop, _ctx.Owner);
            _ctx.Track(new OnDispose(SaveLog));
            // Hand moves into the Log (Progenitor #108); an older Core logs none. Watched only while the log is on.
            var hands = new HandMoves(_ctx, this);
            void LogSwitch()
            {
                var on = _server.Snapshot().AllowMoveLog;
                MoveLog.Enabled = on;
                try { hands.Watch(on); }
                catch (System.Exception e) { _ctx.Log.LogInfo("Satisvampory: hand moves not logged (Progenitor too old?): " + e.Message); }
            }
            LogSwitch();
            _server.Changed += LogSwitch;
            _ctx.Track(new OnDispose(() => _server.Changed -= LogSwitch));
            Progenitor.Dev.MainThread.PostDelayed(20, () => AdminActions.AutoImportOnFirstBoot(_ctx, this), _ctx.Owner);
            _ctx.Log.LogInfo("Satisvampory 2.0: server part loaded.");
        }

        /// <summary>A player's settings: what they last sent, else what the save holds, else defaults.</summary>
        internal PlayerSettings PlayerFor(ulong platformId)
        {
            if (_players.TryGetValue(platformId, out var s)) return s;
            s = Stores.Player(_ctx.Save, platformId) ?? new PlayerSettings();
            _players[platformId] = s;
            return s;
        }

        /// <summary>Drop a cached player so the next read comes from the save (after an import wrote it).</summary>
        internal void ForgetPlayer(ulong platformId) => _players.Remove(platformId);

        unsafe void OnPrefs(int eventId, Entity userEntity, User user, byte* payload, int length)
        {
            if (length < sizeof(PlayerPrefsPacket)) return;
            var p = *(PlayerPrefsPacket*)payload;
            if (p.Magic != EventIds.Magic || p.Version != 1) return;
            if (_lastBits.TryGetValue(user.PlatformId, out var last) && last == p.Bits) return;
            _lastBits[user.PlatformId] = p.Bits;
            var settings = PlayerConfig.FromBits(p.Bits);
            _players[user.PlatformId] = settings;
            Stores.SetPlayer(_ctx.Save, user.PlatformId, settings);
            // Craft from storage, shares and never-pull-last change this player's answers.
            MaterialPolicy.Invalidate();
        }

        unsafe void OnAction(int eventId, Entity userEntity, User user, byte* payload, int length)
        {
            if (length < sizeof(ActionPacket)) return;
            var a = *(ActionPacket*)payload;
            if (a.Magic != EventIds.Magic) return;
            // One action per second per player (spec 07); rebuilding an island is not free.
            var now = _clock.Elapsed.TotalSeconds;
            if (_lastAction.TryGetValue(user.PlatformId, out var at) && now - at < 1) return;
            _lastAction[user.PlatformId] = now;
            if ((ActionKind)a.Kind is ActionKind.ImportCheck or ActionKind.ImportApply)
            {
                string text;
                if (!user.IsAdmin) text = "Only server admins can run this.";
                else if ((ActionKind)a.Kind == ActionKind.ImportCheck) text = AdminActions.ImportCheck();
                else
                {
                    try { text = AdminActions.ImportApply(_ctx, this); _ctx.Log.LogInfo("Satisvampory: import by an admin. " + text); }
                    catch (System.Exception e) { _ctx.Log.LogError("Satisvampory: import failed: " + e); text = "Import failed: " + e.Message; }
                }
                _ctx.Net.SendToUser(userEntity, EventIds.Text, TextPacket.Of(ActionKind.ImportCheck, text));
                return;
            }
            NoticePacket reply;
            try
            {
                reply = ServerActions.Run(_ctx, this, userEntity, user, (ActionKind)a.Kind);
            }
            catch (System.Exception e)
            {
                _ctx.Log.LogError("Satisvampory: action failed: " + e);
                reply = new NoticePacket { Magic = EventIds.Magic, Code = (int)NoticeCode.Refused };
            }
            var sent = _ctx.Net.SendToUser(userEntity, EventIds.Notice, reply);
            _ctx.Log.LogInfo($"Satisvampory: action {(ActionKind)a.Kind} from {user.CharacterName} -> {(NoticeCode)reply.Code} ({reply.A}, {reply.B}); reply sent={sent}");
        }
    }
}
