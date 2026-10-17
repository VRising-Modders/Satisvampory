// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Satisvampory.Config;
using Satisvampory.Net;

namespace Satisvampory
{
    /// <summary>
    /// The client part. Sends this player's Me settings to the server (on load and on every
    /// change) and turns key presses into requests. Panels arrive with Progenitor's panel sites.
    /// </summary>
    internal sealed class ClientHalf
    {
        readonly SatisContext _ctx;
        readonly PlayerConfig _player;
        readonly Keys _keys;
        readonly AdminPanel _admin;

        internal ClientHalf(SatisContext ctx, PlayerConfig player, Keys keys, AdminPanel admin)
        {
            _ctx = ctx;
            _player = player;
            _keys = keys;
            _admin = admin;
        }

        internal unsafe void Attach()
        {
            _player.Changed += SendPrefs;
            _ctx.Track(new OnDispose(() => _player.Changed -= SendPrefs));
            _keys.Pressed += OnKey;
            _ctx.Track(new OnDispose(() => _keys.Pressed -= OnKey));
            _ctx.Net.OnReceive(EventIds.Notice, OnNotice);
            _ctx.Net.OnReceive(EventIds.Text, OnText);
            _admin.Request = SendAction;
            _admin.Run = RunAdmin;
            _ctx.Track(new OnDispose(() => _admin.Run = null));
            _ctx.Track(new OnDispose(() => _admin.Request = null));
            Client.Help.Attach(_ctx, _player);
            var panels = new Client.Panels(_ctx, _player);
            panels.Attach();
            var hub = new Client.Hub(_ctx, _player);
            hub.Attach(_keys);
            _admin.OpenHub = hub.OpenTab;
            panels.OpenHub = hub.OpenTab;
            _ctx.Track(new OnDispose(() => _admin.OpenHub = null));
            _ctx.Track(new OnDispose(() => _stopped = true));
            KeepPrefsSent();
        }

        double _lastDiag = -10;

        void RunAdmin(int op, string item, int amount)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (op == 3) { if (now - _lastDiag < 3) return; _lastDiag = now; }
            else _admin.LastAdminResult = "Asking the server…";
            _ctx.Requests.Send<AdminReq, TextReply>(new AdminReq { Op = op, Item = item ?? "", Amount = amount }, r =>
            {
                var text = r.Ok && r.Reply != null ? r.Reply.Text : (r.Reason.Length > 0 ? r.Reason : r.Status.ToString());
                if (op == 3) _admin.Diagnostics = text;
                else _admin.LastAdminResult = text;
            });
        }

        bool _stopped;
        bool _sent;

        void SendPrefs() =>
            _sent = _ctx.Net.SendToServer(EventIds.PlayerPrefs, new PlayerPrefsPacket { Magic = EventIds.Magic, Version = 1, Bits = _player.Bits() });

        /// <summary>
        /// Player settings must reach the server after every join: at load there is often no
        /// connection yet, so retry every 2 s until a send goes through, then refresh once a minute
        /// (the server ignores unchanged settings).
        /// </summary>
        void KeepPrefsSent()
        {
            if (_stopped) return;
            SendPrefs();
            Progenitor.Dev.MainThread.PostDelayed(_sent ? 60 : 2, KeepPrefsSent, _ctx.Owner);
        }

        void OnKey(string id)
        {
            var kind = id switch
            {
                Keys.Stash => ActionKind.Stash,
                _ => ActionKind.None,
            };
            if (kind == ActionKind.None) return; // the hub handles the other keys
            SendAction(kind);
        }

        void SendAction(ActionKind kind)
        {
            if (kind is ActionKind.ImportCheck or ActionKind.ImportApply) _admin.LastResult = "Asking the server…";
            if (!_ctx.Net.SendToServer(EventIds.Action, new ActionPacket { Magic = EventIds.Magic, Kind = (int)kind }))
                _ctx.Log.LogWarning($"Satisvampory: could not send {kind} to the server");
        }

        unsafe void OnText(int eventId, byte* payload, int length)
        {
            if (length < sizeof(TextPacket)) return;
            var t = *(TextPacket*)payload;
            if (t.Magic != EventIds.Magic) return;
            if ((ActionKind)t.Kind == ActionKind.ImportCheck) _admin.LastResult = t.Text();
            else if ((ActionKind)t.Kind == ActionKind.Notice) _ctx.Hud.Notify(Client.ClientItems.Missions(_ctx, t.Text()), kind: Progenitor.Ui.UiNoticeKind.Info, seconds: 8);
        }

        unsafe void OnNotice(int eventId, byte* payload, int length)
        {
            if (length < sizeof(NoticePacket)) return;
            var n = *(NoticePacket*)payload;
            if (n.Magic != EventIds.Magic) return;
            if ((NoticeCode)n.Code == NoticeCode.SharedIsland)
            {
                Client.Help.Tip(_ctx, Client.Help.SharedIslandTip, null);
                return;
            }
            var text = (NoticeCode)n.Code switch
            {
                NoticeCode.NotAvailableYet => "That needs Progenitor's inventory API, which this build does not have yet.",
                NoticeCode.Refused => "Not here. Stand on one of your or your clan's castles.",
                NoticeCode.Stashed => $"Stashed {n.A} stacks into {n.B} chests.",
                NoticeCode.Tidied => $"Tidied {n.A} items across {n.B} chests.",
                NoticeCode.NotNow => Server.ServerActions.Words((StateRefusal)n.A),
                _ => null,
            };
            if (text == null) return;
            var kind = (NoticeCode)n.Code is NoticeCode.Stashed or NoticeCode.Tidied ? Progenitor.Ui.UiNoticeKind.Success : Progenitor.Ui.UiNoticeKind.Warning;
            if ((NoticeCode)n.Code == NoticeCode.Stashed && _player.Rows.First(r => r.Entry.Definition.Key == "SilentStash").Entry.Value) return;
            _ctx.Hud.Notify(text, kind: kind, seconds: 5);
        }
    }

    /// <summary>A disposable that runs one action once.</summary>
    internal sealed class OnDispose : System.IDisposable
    {
        System.Action? _action;

        internal OnDispose(System.Action action) => _action = action;

        public void Dispose()
        {
            var a = _action;
            _action = null;
            a?.Invoke();
        }
    }
}
