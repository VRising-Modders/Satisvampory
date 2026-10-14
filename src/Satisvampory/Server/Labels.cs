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

        public static string Name(IModStore save, ICastleHandle c)
        {
            var custom = Stores.Castle(save, c.Territory.Index).PlotLabel;
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
            return string.IsNullOrWhiteSpace(c.Name) ? $"Plot {c.Territory.Index}" : c.Name;
        }
    }
}
