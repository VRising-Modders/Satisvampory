// SPDX-License-Identifier: AGPL-3.0-only
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
            SendPrefs();
        }

        void SendPrefs() =>
            _ctx.Net.SendToServer(EventIds.PlayerPrefs, new PlayerPrefsPacket { Magic = EventIds.Magic, Version = 1, Bits = _player.Bits() });

        void OnKey(string id)
        {
            var kind = id switch
            {
                Keys.Stash => ActionKind.Stash,
                Keys.Tidy => ActionKind.Tidy,
                _ => ActionKind.None,
            };
            if (kind == ActionKind.None)
            {
                _ctx.Log.LogInfo($"Satisvampory: the {id} screen arrives with Progenitor's panel sites.");
                return;
            }
            SendAction(kind);
        }

        void SendAction(ActionKind kind)
        {
            if (kind == ActionKind.ImportCheck) _admin.LastResult = "Asking the server…";
            _ctx.Net.SendToServer(EventIds.Action, new ActionPacket { Magic = EventIds.Magic, Kind = (int)kind });
        }

        unsafe void OnText(int eventId, byte* payload, int length)
        {
            if (length < sizeof(TextPacket)) return;
            var t = *(TextPacket*)payload;
            if (t.Magic != EventIds.Magic) return;
            if ((ActionKind)t.Kind == ActionKind.ImportCheck) _admin.LastResult = t.Text();
        }

        unsafe void OnNotice(int eventId, byte* payload, int length)
        {
            if (length < sizeof(NoticePacket)) return;
            var n = *(NoticePacket*)payload;
            if (n.Magic != EventIds.Magic) return;
            var text = (NoticeCode)n.Code switch
            {
                NoticeCode.NotAvailableYet => "Satisvampory: that needs Progenitor's inventory API, which this build does not have yet.",
                NoticeCode.Refused => "Satisvampory: refused.",
                NoticeCode.Stashed => $"Stashed {n.A} stacks into {n.B} chests.",
                NoticeCode.Tidied => $"Tidied {n.A} items across {n.B} chests.",
                _ => null,
            };
            if (text != null) _ctx.Log.LogInfo(text);
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
