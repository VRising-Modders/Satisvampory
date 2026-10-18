// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace Satisvampory.Server
{
    /// <summary>
    /// Testing only, off unless a server sets it: castles whose owner's platform id falls in the
    /// [Testing] ExcludeOwnerIds range ("first-last") are left alone by the conveyor and get no
    /// automatic links. A stress test's synthetic offline owners use it so generated castles stay
    /// untouched while they are qualified. Empty (the default) excludes nothing.
    /// </summary>
    internal static class StressOwners
    {
        static ulong _first, _last;
        static bool _on;

        internal static void Bind(SatisContext ctx)
        {
            var entry = ctx.Bind("Testing", "ExcludeOwnerIds", "",
                "Testing only. Platform id range \"first-last\" whose castles Satisvampory leaves alone (no conveyor, no automatic links). Empty: none.");
            Parse(entry.Value);
            if (_on) ctx.Log.LogWarning($"Satisvampory: testing exclusion on, castles of owners {_first}-{_last} are left alone");
        }

        static void Parse(string text)
        {
            _on = false;
            var parts = (text ?? "").Split('-');
            if (parts.Length == 2 && ulong.TryParse(parts[0].Trim(), out var a) && ulong.TryParse(parts[1].Trim(), out var b) && a <= b)
            {
                _first = a;
                _last = b;
                _on = true;
            }
        }

        internal static bool Is(ulong platformId) => _on && platformId >= _first && platformId <= _last;
    }
}
