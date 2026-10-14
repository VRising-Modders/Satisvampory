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
            _ctx.Track(new OnDispose(() => _admin.Request = null));
            new Client.Panels(_ctx).Attach();
            new Client.Hub(_ctx, _player).Attach(_keys);
            _ctx.Track(new OnDispose(() => _stopped = true));
            KeepPrefsSent();
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
            _ctx.Net.SendToServer(EventIds.Action, new ActionPacket { Magic = EventIds.Magic, Kind = (int)kind });
        }

        unsafe void OnText(int eventId, byte* payload, int length)
        {
            if (length < sizeof(TextPacket)) return;
            var t = *(TextPacket*)payload;
            if (t.Magic != EventIds.Magic) return;
            if ((ActionKind)t.Kind == ActionKind.ImportCheck) _admin.LastResult = t.Text();
            else if ((ActionKind)t.Kind == ActionKind.Notice) _ctx.Hud.Notify(t.Text(), kind: Progenitor.Ui.UiNoticeKind.Info, seconds: 8);
        }

        unsafe void OnNotice(int eventId, byte* payload, int length)
        {
            if (length < sizeof(NoticePacket)) return;
            var n = *(NoticePacket*)payload;
            if (n.Magic != EventIds.Magic) return;
            var text = (NoticeCode)n.Code switch
            {
                NoticeCode.NotAvailableYet => "Satisvampory: that needs Progenitor's inventory API, which this build does not have yet.",
                NoticeCode.Refused => "Satisvampory: not here. Stand on one of your or your clan's castles.",
                NoticeCode.Stashed => $"Stashed {n.A} stacks into {n.B} chests.",
                NoticeCode.Tidied => $"Tidied {n.A} items across {n.B} chests.",
                NoticeCode.NotNow => "Satisvampory: " + Server.ServerActions.Words((StateRefusal)n.A),
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
