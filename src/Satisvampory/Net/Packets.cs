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
}
