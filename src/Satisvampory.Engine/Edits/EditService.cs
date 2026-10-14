// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Edits
{
    public enum ActorRole
    {
        Guest,
        ClanMember,
        Owner,
        Admin,
    }

    public enum ListSide
    {
        Input,
        Output,
    }

    /// <summary>Result of one edit request; Reason is a player-facing sentence when refused.</summary>
    public sealed record EditResult(bool Ok, string Reason = "", bool ClosesLoop = false)
    {
        public static EditResult Done { get; } = new(true);
        public static EditResult No(string reason) => new(false, reason);
    }

    /// <summary>
    /// Every change a client can ask for, validated on the server (spec 00, "Who may do what").
    /// The plugin's request handlers call these; nothing here trusts the client. Each accepted
    /// edit bumps <see cref="Revision"/>, which drives subscription deltas and material-view
    /// invalidation.
    /// </summary>
    public sealed class EditService
    {
        readonly IWorldView _world;
        readonly ItemCatalog _catalog;
        readonly IslandState _island;
        readonly Func<IReadOnlyList<PlotId>> _scope;

        public EditService(IWorldView world, ItemCatalog catalog, IslandState island, Func<IReadOnlyList<PlotId>> scope)
        {
            _world = world;
            _catalog = catalog;
            _island = island;
            _scope = scope;
        }

        public long Revision { get; private set; }

        /// <summary>The Advanced script the engine actually runs: the last one that compiled.</summary>
        public CompiledScript ActiveScript { get; private set; } = CompiledScript.Empty;

        public string? ScriptNeedsReview { get; private set; }

        LogisticsGraph Graph => _island.Graph;

        static bool CanEdit(ActorRole r) => r != ActorRole.Guest;

        EditResult Accept(EditResult r)
        {
            if (r.Ok) Revision++;
            return r;
        }

        bool InIsland(ContainerId id)
        {
            var c = _world.Find(id);
            return c != null && _scope().Contains(c.Plot);
        }

        // ---------------- lists ----------------

        public EditResult AddEntry(ActorRole role, ContainerId owner, ListSide side, ListEntry entry, int? at = null)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change logistics");
            if (!InIsland(owner)) return EditResult.No("That container is outside your sharing island");
            var self = _world.Find(owner)!;

            if (entry.Kind == EntryKind.Container)
            {
                if (entry.Container == owner) return EditResult.No("A container cannot list itself");
                var other = _world.Find(entry.Container);
                if (other == null) return EditResult.No("That container no longer exists");
                if (!InIsland(entry.Container)) return EditResult.No("Outside your sharing island");
                if (other.Deposit.Special == SpecialKind.Skip) return EditResult.No("Skip chests are never used");
                if (side == ListSide.Input && other.Kind == NodeKind.Heart) return EditResult.No("Hearts only give items through their own Output");
                if (side == ListSide.Output && self.Kind == NodeKind.Heart && other.Kind == NodeKind.Heart) return EditResult.No("A heart cannot feed another heart");
                if (side == ListSide.Output && self.Role == StationRole.UnitSpawner) return EditResult.No("Spawners make units, not items, so they have no Output");
            }
            else if (entry.Kind == EntryKind.Belt)
            {
                if (!Graph.Belts.TryGetValue(entry.Belt, out var belt)) return EditResult.No("That belt no longer exists");
                if (belt.Members.Contains(owner)) return EditResult.No("This container is on that belt");
            }
            else if (entry.Kind == EntryKind.MassDeposit && side == ListSide.Input && self.Kind == NodeKind.Chest && entry.Filter.IsAnything)
            {
                return EditResult.No("Pulling everything from Mass Deposit needs a filter: choose items or categories");
            }

            var list = side == ListSide.Input ? Graph.For(owner).Input : Graph.For(owner).Output;
            if (list.Any(e => Same(e, entry))) return EditResult.No("Already in this list");

            var seq = Graph.NextSequence();
            var stored = new ListEntry { Kind = entry.Kind, Container = entry.Container, Belt = entry.Belt, Filter = entry.Filter, Sequence = seq };
            list.Insert(Math.Clamp(at ?? list.Count, 0, list.Count), stored);

            var closes = LoopFinder.Find(Graph.Edges()).Any(l => l.Closing.Sequence == seq);
            return Accept(new EditResult(true, ClosesLoop: closes));
        }

        public EditResult RemoveEntry(ActorRole role, ContainerId owner, ListSide side, int index)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change logistics");
            var list = side == ListSide.Input ? Graph.For(owner).Input : Graph.For(owner).Output;
            if (index < 0 || index >= list.Count) return EditResult.No("That row is gone; the list changed");
            list.RemoveAt(index);
            return Accept(EditResult.Done);
        }

        public EditResult MoveEntry(ActorRole role, ContainerId owner, ListSide side, int from, int to)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change logistics");
            var list = side == ListSide.Input ? Graph.For(owner).Input : Graph.For(owner).Output;
            if (from < 0 || from >= list.Count || to < 0 || to >= list.Count) return EditResult.No("That row is gone; the list changed");
            var e = list[from];
            list.RemoveAt(from);
            list.Insert(to, e);
            return Accept(EditResult.Done);
        }

        public EditResult SetOrders(ActorRole role, ContainerId owner, PullOrder? pull, SendOrder? send)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change logistics");
            var l = Graph.For(owner);
            if (pull is { } p) l.PullOrder = p;
            if (send is { } s) l.SendOrder = s;
            return Accept(EditResult.Done);
        }

        // ---------------- deposit options ----------------

        public EditResult SetDepositOptions(ActorRole role, ContainerId id, Action<DepositOptions> change)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change deposit options");
            var c = _world.Find(id);
            if (c == null) return EditResult.No("That container no longer exists");
            if (c.Kind == NodeKind.Heart) return EditResult.No("Hearts are never a Mass Deposit home");
            if (!_island.Deposit.TryGetValue(id, out var d)) _island.Deposit[id] = d = new DepositOptions();
            change(d);
            // Apply to the live ContainerInfo too so rankings see it at once.
            CopyInto(d, c.Deposit);
            return Accept(EditResult.Done);
        }

        static void CopyInto(DepositOptions from, DepositOptions to)
        {
            to.BasePriority = from.BasePriority;
            to.Special = from.Special;
            to.ItemPriority.Clear();
            foreach (var kv in from.ItemPriority) to.ItemPriority[kv.Key] = kv.Value;
            to.CategoryPriority.Clear();
            foreach (var kv in from.CategoryPriority) to.CategoryPriority[kv.Key] = kv.Value;
            to.BannedItems.Clear();
            to.BannedItems.UnionWith(from.BannedItems);
            to.BannedCategories.Clear();
            to.BannedCategories.UnionWith(from.BannedCategories);
        }

        // ---------------- reserves and caps ----------------

        /// <summary>Set or clear (null) an item reserve. Negative values are refused.</summary>
        public EditResult SetItemReserve(ActorRole role, ItemId item, int? value) =>
            SetRule(role, value, () => _island.Rules.ItemReserve.Remove(item), v => _island.Rules.ItemReserve[item] = v);

        public EditResult SetCategoryReserve(ActorRole role, string category, int? value) =>
            !_catalog.TryResolveCategory(category, out var key) ? EditResult.No($"No category named '{category}'")
            : SetRule(role, value, () => _island.Rules.CategoryReserve.Remove(key), v => _island.Rules.CategoryReserve[key] = v);

        public EditResult SetItemCap(ActorRole role, ItemId item, int? value) =>
            SetRule(role, value, () => _island.Rules.ItemCap.Remove(item), v => _island.Rules.ItemCap[item] = v);

        public EditResult SetCategoryCap(ActorRole role, string category, int? value) =>
            !_catalog.TryResolveCategory(category, out var key) ? EditResult.No($"No category named '{category}'")
            : SetRule(role, value, () => _island.Rules.CategoryCap.Remove(key), v => _island.Rules.CategoryCap[key] = v);

        public EditResult SetDefaultReserve(ActorRole role, int value) =>
            SetRule(role, value, () => { }, v => _island.Rules.DefaultReserve = v);

        EditResult SetRule(ActorRole role, int? value, Action clear, Action<int> set)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change reserves and caps");
            if (value is < 0) return EditResult.No("Use 0 or more");
            if (value is { } v) set(v);
            else clear();
            return Accept(EditResult.Done);
        }

        // ---------------- loops ----------------

        public EditResult SetLoopMode(ActorRole role, string loopKey, LoopMode mode)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change loops");
            _island.LoopModes[loopKey] = mode;
            return Accept(EditResult.Done);
        }

        // ---------------- Mass Deposit ----------------

        public EditResult SetRows(ActorRole role, Action<MassDepositSettings> change)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change Mass Deposit");
            change(_island.MassDeposit);
            return Accept(EditResult.Done);
        }

        /// <summary>
        /// Save an Advanced script. A script with errors is refused and the last good one keeps
        /// running; the reply carries the first error with its line.
        /// </summary>
        public EditResult SaveScript(ActorRole role, string source, ISet<string>? chestNames = null, ISet<string>? plotLabels = null)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change Mass Deposit");
            var names = new ScriptNames
            {
                ChestNames = chestNames,
                Belts = new HashSet<string>(Graph.Belts.Values.Select(b => NameFold.Fold(b.Name))),
                PlotLabels = plotLabels,
            };
            var compiled = AdvancedScript.Compile(source, _catalog, names);
            if (!compiled.Ok)
            {
                var e = compiled.Messages.First(m => m.IsError);
                return EditResult.No($"Line {e.Line}: {e.Text}");
            }
            _island.MassDeposit.AdvancedScript = source;
            ActiveScript = compiled;
            ScriptNeedsReview = null;
            return Accept(EditResult.Done);
        }

        // ---------------- belts ----------------

        public EditResult CreateBelt(ActorRole role, string name, out Belt? belt)
        {
            belt = null;
            if (!CanEdit(role)) return EditResult.No("Clan members can change belts");
            var r = BeltOps.Create(Graph, name, out belt);
            return Accept(r.Ok ? EditResult.Done : EditResult.No(r.Error));
        }

        public EditResult RenameBelt(ActorRole role, BeltId id, string name)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change belts");
            var r = BeltOps.Rename(Graph, id, name, new BeltReferences { MassDeposit = _island.MassDeposit });
            if (r.Ok) RecompileAfterBeltChange();
            return Accept(r.Ok ? EditResult.Done : EditResult.No(r.Error));
        }

        public EditResult DeleteBelt(ActorRole role, BeltId id, out List<BeltUsage> removed)
        {
            removed = new List<BeltUsage>();
            if (!CanEdit(role)) return EditResult.No("Clan members can change belts");
            if (!Graph.Belts.ContainsKey(id)) return EditResult.No("That belt no longer exists");
            removed = BeltOps.Delete(Graph, id, new BeltReferences { MassDeposit = _island.MassDeposit });
            RecompileAfterBeltChange();
            return Accept(EditResult.Done);
        }

        public EditResult AddBeltMembers(ActorRole role, BeltId id, IEnumerable<ContainerId> members)
        {
            if (!CanEdit(role)) return EditResult.No("Clan members can change belts");
            var ok = members.Where(m => InIsland(m) && _world.Find(m)?.Kind != NodeKind.Heart).ToList();
            if (ok.Count == 0) return EditResult.No("Nothing to add from your sharing island");
            BeltOps.AddMembers(Graph, id, ok);
            return Accept(EditResult.Done);
        }

        /// <summary>A belt rename or delete may break or fix the stored script; the last good version keeps running.</summary>
        void RecompileAfterBeltChange()
        {
            var source = _island.MassDeposit.AdvancedScript;
            if (string.IsNullOrWhiteSpace(source)) return;
            var compiled = AdvancedScript.Compile(source, _catalog, new ScriptNames
            {
                Belts = new HashSet<string>(Graph.Belts.Values.Select(b => NameFold.Fold(b.Name))),
            });
            if (compiled.Ok)
            {
                ActiveScript = compiled;
                ScriptNeedsReview = null;
            }
            else
            {
                var e = compiled.Messages.First(m => m.IsError);
                ScriptNeedsReview = $"Line {e.Line}: {e.Text}";
            }
        }

        static bool Same(ListEntry a, ListEntry b) =>
            a.Kind == b.Kind && a.Kind switch
            {
                EntryKind.Container => a.Container == b.Container,
                EntryKind.Belt => a.Belt == b.Belt,
                _ => true,
            };
    }
}
