// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Persistence;
using Satisvampory.Engine.Model;
using Satisvampory.World;

namespace Satisvampory.Server
{
    /// <summary>
    /// Plot labels (spec 01): "here" for the standing plot, else the label set on the Castle page,
    /// else the castle's own name, else "Plot n".
    /// </summary>
    internal static class Labels
    {
        public static string Plot(IModStore save, IReadOnlyList<ICastleHandle> castles, PlotId standing, PlotId plot)
        {
            if (plot == standing) return "here";
            var c = castles.FirstOrDefault(x => ProgenitorWorld.PlotOf(x) == plot);
            return c == null ? "elsewhere" : Name(save, c);
        }

        /// <summary>Whose numbering a castle belongs to (its clan, else its owner); set by the server half.</summary>
        public static System.Func<ICastleHandle, string>? OwnerKeyOf { get; set; }

        public static string Name(IModStore save, ICastleHandle c)
        {
            var custom = Stores.Castle(save, c.Territory.Index).PlotLabel;
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
            return string.IsNullOrWhiteSpace(c.Name) ? $"Plot {Number(save, c)}" : c.Name;
        }

        /// <summary>A stable number per clan (or clanless owner), given in the order hearts were first seen.</summary>
        static int Number(IModStore save, ICastleHandle c)
        {
            var owner = OwnerKeyOf?.Invoke(c);
            if (owner == null) return c.Territory.Index;
            var key = "plot-numbers/" + owner;
            var map = save.Get<Dictionary<int, int>>(key) ?? new Dictionary<int, int>();
            if (map.TryGetValue(c.Territory.Index, out var n)) return n;
            n = map.Count == 0 ? 1 : map.Values.Max() + 1;
            map[c.Territory.Index] = n;
            save.Set(key, map);
            return n;
        }
    }
}
