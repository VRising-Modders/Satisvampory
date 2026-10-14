// SPDX-License-Identifier: AGPL-3.0-only
namespace Satisvampory.Net
{
    /// <summary>
    /// Fixed-size messages over Progenitor custom events. Ids sit in Satisvampory's own range
    /// ('S','V' high bytes), clear of Progenitor's reserved 0x5072____. Larger, typed request and
    /// reply messages move to Progenitor's ctx.Requests once it lands.
    /// </summary>
    public static class EventIds
    {
        public const int PlayerPrefs = 0x53560001;
        public const int Action = 0x53560002;
        public const int Notice = 0x53560003;
        public const int Text = 0x53560004;

        public const int Magic = 0x53563230; // "SV20"
    }

    /// <summary>Client → server: the player's Me settings as bits (see PlayerConfig).</summary>
    public struct PlayerPrefsPacket
    {
        public int Magic;
        public int Version;
        public uint Bits;
    }

    public enum ActionKind
    {
        None = 0,
        Stash = 1,
        Tidy = 2,

        /// <summary>Admin: dry run of the 1.x settings import.</summary>
        ImportCheck = 10,

        /// <summary>Admin: apply the older mod's settings and chest names once.</summary>
        ImportApply = 11,

        /// <summary>Server → client text notice (servant returns).</summary>
        Notice = 20,
    }

    /// <summary>Client → server: a player action triggered by a key or button.</summary>
    public struct ActionPacket
    {
        public int Magic;
        public int Kind;
    }

    /// <summary>Server → client: a short result code for a notice the client words itself.</summary>
    public struct NoticePacket
    {
        public int Magic;
        public int Code;
        public int A;
        public int B;
    }

    public enum NoticeCode
    {
        None = 0,
        NotAvailableYet = 1,
        Refused = 2,
        Stashed = 3,
        Tidied = 4,
    }

    /// <summary>Server → client: a short UTF-8 text reply (admin results), length first.</summary>
    public unsafe struct TextPacket
    {
        public const int Bytes = 480;

        public int Magic;
        public int Kind;
        public int Length;
        public fixed byte Utf8[Bytes];

        public static TextPacket Of(ActionKind kind, string text)
        {
            var p = new TextPacket { Magic = EventIds.Magic, Kind = (int)kind };
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var n = System.Math.Min(bytes.Length, Bytes);
            while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80) n--; // never split a character
            for (var i = 0; i < n; i++) p.Utf8[i] = bytes[i];
            p.Length = n;
            return p;
        }

        public string Text()
        {
            if (Length < 0 || Length > Bytes) return string.Empty;
            fixed (byte* b = Utf8) return System.Text.Encoding.UTF8.GetString(b, Length);
        }
    }
}
