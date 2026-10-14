// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using ProjectM.Network;
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

        internal unsafe void Attach()
        {
            _active = this;
            _ctx.Track(new OnDispose(() => { if (ReferenceEquals(_active, this)) _active = null; }));
            _ctx.Net.OnReceiveFromClient(EventIds.PlayerPrefs, OnPrefs);
            _ctx.Net.OnReceiveFromClient(EventIds.Action, OnAction);
            Ticker = new Ticker(_ctx, this);
            Ticker.Attach();
            new PanelService(_ctx, this).Attach();
            new HubService(_ctx, this).Attach();
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

        unsafe void OnPrefs(int eventId, Entity userEntity, User user, byte* payload, int length)
        {
            if (length < sizeof(PlayerPrefsPacket)) return;
            var p = *(PlayerPrefsPacket*)payload;
            if (p.Magic != EventIds.Magic || p.Version != 1) return;
            var settings = PlayerConfig.FromBits(p.Bits);
            _players[user.PlatformId] = settings;
            Stores.SetPlayer(_ctx.Save, user.PlatformId, settings);
        }

        unsafe void OnAction(int eventId, Entity userEntity, User user, byte* payload, int length)
        {
            if (length < sizeof(ActionPacket)) return;
            var a = *(ActionPacket*)payload;
            if (a.Magic != EventIds.Magic) return;
            if ((ActionKind)a.Kind == ActionKind.ImportCheck)
            {
                var text = user.IsAdmin ? AdminActions.ImportCheck() : "Only server admins can run this.";
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
            _ctx.Net.SendToUser(userEntity, EventIds.Notice, reply);
        }
    }
}
