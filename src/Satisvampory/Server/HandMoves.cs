// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Progenitor.Castles;
using Progenitor.Inventory;
using ProjectM.Network;
using Satisvampory.Engine.Model;
using Satisvampory.World;

namespace Satisvampory.Server
{
    /// <summary>
    /// Items players move by hand into or out of castle chests and stations (drag, take all, deposit all, drop, equip),
    /// into the Log, with the amounts the game really moved (Progenitor's ctx.Inventory.OnPlayerMove). A move between
    /// two castle containers is logged once, from the side that lost the items.
    /// </summary>
    internal sealed class HandMoves
    {
        readonly SatisContext _ctx;
        readonly ServerHalf _half;
        Dictionary<Guid, (PlotId Plot, string Name, string PlotLabel)> _index = new();
        double _indexAt = double.NegativeInfinity;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        internal HandMoves(SatisContext ctx, ServerHalf half)
        {
            _ctx = ctx;
            _half = half;
        }

        IDisposable? _watch;
        bool _unavailable;

        /// <summary>Watch hand moves while the log is on; stop (no cost at all) while it is off.</summary>
        internal void Watch(bool on)
        {
            if (_unavailable) return;
            if (!on) { _watch?.Dispose(); _watch = null; return; }
            if (_watch != null) return;
            try { _watch = Subscribe(); _ctx.Track(_watch); }
            catch (Exception) { _unavailable = true; throw; }
        }

        // Its own method: on a Core without OnPlayerMove (Progenitor #108) only this call fails (and is caught).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        IDisposable Subscribe() => _ctx.Inventory.OnPlayerMove(OnMove);

        void OnMove(PlayerInventoryMove m)
        {
            try
            {
                if (!m.IsCastleContainer || m.Direction == PlayerInventoryMoveDirection.Within) return;
                // Chest to chest raises a move on each side: keep the side that lost the items.
                if (m.Direction == PlayerInventoryMoveDirection.In && !m.CounterpartContainer.IsNone && Find(m.CounterpartContainer.Value) != null) return;
                if (Find(m.Container.Value) is not { } here) return;

                var actor = CharacterName(m.User);
                if (m.Ambiguous || m.Incomplete) actor += " (uncertain)";
                (PlotId? Plot, Guid Id, string Name, string Label) other = m.Counterpart switch
                {
                    PlayerInventoryCounterpart.OwnInventory => (null, Guid.Empty, CharacterName(m.User) + "'s bag", ""),
                    PlayerInventoryCounterpart.Equipment => (null, Guid.Empty, CharacterName(m.User) + "'s gear", ""),
                    PlayerInventoryCounterpart.Ground => (null, Guid.Empty, "the ground", ""),
                    PlayerInventoryCounterpart.OtherInventory when !m.CounterpartContainer.IsNone && Find(m.CounterpartContainer.Value) is { } c
                        => (c.Plot, m.CounterpartContainer.Value, c.Name, c.PlotLabel),
                    _ => (null, Guid.Empty, "another inventory", ""),
                };
                var why = m.Kind switch
                {
                    PlayerInventoryMoveKind.MoveAll => m.Direction == PlayerInventoryMoveDirection.In ? "By hand: deposit all" : "By hand: take all",
                    PlayerInventoryMoveKind.SmartMerge => "By hand: merge",
                    PlayerInventoryMoveKind.Drop => "Dropped",
                    PlayerInventoryMoveKind.Equip => "Equipped",
                    _ => "By hand",
                };
                if (m.Direction == PlayerInventoryMoveDirection.In)
                    _half.MoveLog.Add(other.Plot, here.Plot, m.Item, m.Amount, other.Id, other.Name, other.Label,
                        m.Container.Value, here.Name, here.PlotLabel, why, 0, actor);
                else
                    _half.MoveLog.Add(here.Plot, other.Plot, m.Item, m.Amount, m.Container.Value, here.Name, here.PlotLabel,
                        other.Id, other.Name, other.Label, why, 0, actor);
            }
            catch (Exception e)
            {
                _ctx.Log.LogInfo("Satisvampory: a hand move was not logged: " + e.Message);
            }
        }

        string CharacterName(Unity.Entities.Entity user)
        {
            var em = _ctx.World!.EntityManager;
            return em.Exists(user) && em.HasComponent<User>(user) ? em.GetComponentData<User>(user).CharacterName.ToString() : "A player";
        }

        /// <summary>Every castle chest and station by id, with its castle and names; rebuilt at most every 5 s on a miss, else every minute.</summary>
        (PlotId Plot, string Name, string PlotLabel)? Find(Guid id)
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (_index.TryGetValue(id, out var hit) && now - _indexAt < 60) return hit;
            if (now - _indexAt < 5) return _index.TryGetValue(id, out hit) ? hit : null;
            var index = new Dictionary<Guid, (PlotId, string, string)>();
            var castles = new List<ICastleHandle>();
            foreach (var c in _ctx.Castles.All()) if (c.Enabled) castles.Add(c);
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, castles, _ => null, cachedRows: true);
            foreach (var c in castles)
            {
                var plot = ProgenitorWorld.PlotOf(c);
                var label = Labels.Name(_ctx.Save, c);
                foreach (var info in world.ContainersOn(plot)) index[info.Id.Value] = (plot, info.DisplayName, label);
            }
            _index = index;
            _indexAt = now;
            return _index.TryGetValue(id, out hit) ? hit : null;
        }
    }
}
