// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Networking;
using ProjectM.Network;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Edits;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Satisvampory.Engine.Stations;
using Satisvampory.Engine.Ui;
using Satisvampory.Net;
using Satisvampory.World;
using Unity.Entities;
using EngineId = Satisvampory.Engine.Model.ContainerId;

namespace Satisvampory.Server
{
    /// <summary>
    /// Server side of the container panels (spec 02, 01, 03): every request is resolved to a
    /// container on the requester's island, checked for edit rights, applied through the engine's
    /// EditService, saved, and answered with the panel as the server now sees it.
    /// </summary>
    internal sealed class PanelService
    {
        readonly SatisContext _ctx;
        readonly ServerHalf _half;

        internal PanelService(SatisContext ctx, ServerHalf half)
        {
            _ctx = ctx;
            _half = half;
        }

        internal void Attach()
        {
            _ctx.Requests.Handle<GetPanelReq, PanelMsg>((u, r, i) => With(u, r.Target, s => Panel(s)));
            _ctx.Requests.Handle<EditListReq, PanelMsg>((u, r, i) => With(u, r.Target, s => EditList(s, r)));
            _ctx.Requests.Handle<SetDepositReq, PanelMsg>((u, r, i) => With(u, r.Target, s => SetDeposit(s, r)));
            _ctx.Requests.Handle<PickerReq, PickerReply>((u, r, i) => With(u, r.Target, s => PickerRows(s, r)));
            _ctx.Requests.Handle<TryItReq, TryItReply>((u, r, i) => With(u, r.Target, s => TryIt(s, r)));
            _ctx.Requests.Handle<SetRecipesReq, PanelMsg>((u, r, i) => With(u, r.Target, s => SetRecipes(s, r)));
            _ctx.Requests.Handle<SetLoopReq, PanelMsg>((u, r, i) => With(u, r.Target, s => SetLoop(s, r)));
            _ctx.Requests.Handle<EmptyTrashReq, PanelMsg>((u, r, i) => With(u, r.Target, s => EmptyTrash(s)));
        }

        // ---------------- session per request ----------------

        sealed class Session
        {
            public User User;
            public ServerActions.Island Island = null!;
            public ProgenitorWorld World = null!;
            public ContainerInfo Container = null!;
            public ActorRole Role;
            public EditService Edits = null!;
            public DepositContext Deposit = null!;
            public ItemCatalog Catalog = null!;
        }

        T With<T>(Entity userEntity, TargetMsg target, Func<Session, T> body)
        {
            var user = _ctx.World!.EntityManager.GetComponentData<User>(userEntity);
            _half.Catalog.Refresh(_ctx);
            var island = ServerActions.Resolve(_ctx, _half, userEntity, user)
                ?? throw new RequestRefusedException("Stand on one of your or your clan's castles to change logistics.");
            var catalog = _half.Catalog.Current.WithEdits(island.State.Categories);
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, island.Castles,
                id => island.State.Deposit.TryGetValue(id, out var d) ? d : null)
            {
                TableRecipesOn = id => island.State.TableRecipesOn.TryGetValue(id, out var on) ? on : null,
            };

            var id = TargetResolver.Resolve(_ctx, target, out var why)
                ?? throw new RequestRefusedException(why);
            var container = world.Find(id) ?? throw new RequestRefusedException("That container is outside your sharing island.");

            var castle = island.Castles.FirstOrDefault(c => ProgenitorWorld.PlotOf(c) == container.Plot);
            var relation = castle == null ? CastleRelation.None : _ctx.Castles.RelationTo(userEntity, castle);
            var role = relation switch
            {
                CastleRelation.Owner => ActorRole.Owner,
                CastleRelation.ClanMember => ActorRole.ClanMember,
                _ => ActorRole.Guest,
            };

            var deposit = new DepositContext(catalog, world, island.Scope, ProgenitorWorld.PlotOf(island.Standing))
            {
                Settings = island.State.MassDeposit,
                Belts = island.State.Graph.BeltMembers(),
                PlotLabel = p => PlotLabel(island, p),
            };
            var session = new Session
            {
                User = user,
                Island = island,
                World = world,
                Container = container,
                Role = role,
                Catalog = catalog,
                Deposit = deposit,
                Edits = new EditService(world, catalog, island.State, () => island.Scope),
            };
            return body(session);
        }

        void Save(Session s)
        {
            Stores.Save(_ctx.Save, s.Island.OwnerKey, s.Island.State);
            _half.MaterialPolicy.Invalidate();
        }

        static EditResult Must(EditResult r) => r.Ok ? r : throw new RequestRefusedException(r.Reason);

        // ---------------- handlers ----------------

        PanelMsg EditList(Session s, EditListReq r)
        {
            var side = r.Side == 0 ? ListSide.Input : ListSide.Output;
            switch (r.Op)
            {
                case 0:
                {
                    var entry = r.Entry;
                    if (entry.Kind == 0 && string.IsNullOrEmpty(entry.ContainerId) && entry.PickType != 0)
                    {
                        var picked = TargetResolver.Resolve(_ctx, entry.PickIndex, entry.PickGeneration, entry.PickType, out var why)
                            ?? throw new RequestRefusedException(why);
                        entry.ContainerId = picked.Value.ToString("N");
                    }
                    Must(s.Edits.AddEntry(s.Role, s.Container.Id, side, ToEntry(entry)));
                    break;
                }
                case 1:
                    Must(s.Edits.RemoveEntry(s.Role, s.Container.Id, side, r.Index));
                    break;
                case 2:
                    Must(s.Edits.MoveEntry(s.Role, s.Container.Id, side, r.Index, r.To));
                    break;
                case 3:
                    Must(s.Edits.SetOrders(s.Role, s.Container.Id, (PullOrder)r.PullOrder, (SendOrder)r.SendOrder));
                    break;
                case 4:
                {
                    var list = side == ListSide.Input ? s.Island.State.Graph.For(s.Container.Id).Input : s.Island.State.Graph.For(s.Container.Id).Output;
                    if (r.Index < 0 || r.Index >= list.Count) throw new RequestRefusedException("That row is gone; the list changed.");
                    if (s.Role == ActorRole.Guest) throw new RequestRefusedException("Clan members can change logistics.");
                    var old = list[r.Index];
                    list[r.Index] = new ListEntry { Kind = old.Kind, Container = old.Container, Belt = old.Belt, Sequence = old.Sequence, Filter = ToFilter(r.Entry.FilterSpec) };
                    break;
                }
                default:
                    throw new RequestRefusedException("Unknown edit.");
            }
            Save(s);
            return Panel(s);
        }

        PanelMsg SetDeposit(Session s, SetDepositReq r)
        {
            var m = r.Deposit;
            Must(s.Edits.SetDepositOptions(s.Role, s.Container.Id, d =>
            {
                d.Takes = (Takes)Math.Clamp(m.Takes, 0, 2);
                d.BasePriority = m.BasePriority;
                d.Special = Enum.IsDefined(typeof(SpecialKind), m.Special) ? (SpecialKind)m.Special : SpecialKind.None;
                d.ItemPriority.Clear();
                d.CategoryPriority.Clear();
                foreach (var p in m.Priorities)
                    if (p.Item != 0) d.ItemPriority[new ItemId(p.Item)] = Math.Clamp(p.Adjust, -10, 10);
                    else if (s.Catalog.TryResolveCategory(p.Category, out var c)) d.CategoryPriority[c] = Math.Clamp(p.Adjust, -10, 10);
                    else if (TryItem(s.Catalog, p.Category, out var named)) d.ItemPriority[named] = Math.Clamp(p.Adjust, -10, 10);
                    else throw new RequestRefusedException($"No item or category called \"{p.Category}\".");
                d.BannedItems.Clear();
                d.BannedCategories.Clear();
                foreach (var b in m.Bans)
                    if (b.Item != 0) d.BannedItems.Add(new ItemId(b.Item));
                    else if (s.Catalog.TryResolveCategory(b.Category, out var c)) d.BannedCategories.Add(c);
                    else if (TryItem(s.Catalog, b.Category, out var named)) d.BannedItems.Add(named);
                    else throw new RequestRefusedException($"No item or category called \"{b.Category}\".");
            }));
            Save(s);
            return Panel(s);
        }

        PickerReply PickerRows(Session s, PickerReq r)
        {
            var rows = Picker.Rows(s.World, s.Catalog, s.Island.State.Graph, s.Island.Scope, p => PlotLabel(s.Island, p), new PickerQuery
            {
                Self = s.Container.Id,
                Side = r.Side == 0 ? PickerSide.Input : PickerSide.Output,
                Standing = ProgenitorWorld.PlotOf(s.Island.Standing),
                Search = r.Search ?? string.Empty,
            });
            var reply = new PickerReply();
            foreach (var row in rows)
                reply.Rows.Add(new PickerRowMsg
                {
                    Key = row.Kind switch
                    {
                        PickerRowKind.Container => "c:" + row.Container!.Value.Value.ToString("N"),
                        PickerRowKind.Belt => "b:" + row.Belt!.Value.Value.ToString("N"),
                        _ => "md",
                    },
                    Label = row.Name,
                    Section = row.Section switch
                    {
                        PickerSection.Here => "Here",
                        PickerSection.OtherPlots => "Other plots",
                        PickerSection.Belts => "Belts",
                        PickerSection.SpecialChests => "Special chests",
                        _ => "Mass Deposit",
                    },
                    Note = string.Join(" · ", new[] { row.PlotLabel, row.Distance is { } d ? $"{d:0} m" : "", row.Hint }.Where(x => !string.IsNullOrEmpty(x))),
                    Refused = row.Refused ?? string.Empty,
                    ClosesLoop = row.ClosesLoop,
                });
            return reply;
        }

        PanelMsg SetRecipes(Session s, SetRecipesReq r)
        {
            if (s.Role == ActorRole.Guest) throw new RequestRefusedException("Clan members can change recipes.");
            if (s.Container.Kind != NodeKind.Station) throw new RequestRefusedException("Only stations have recipes.");
            var wanted = new HashSet<int>(r.On);
            var table = new HashSet<int>();
            foreach (var row in s.World.RecipeRows(s.Container.Id))
            {
                var on = wanted.Contains(row.Guid);
                if (row.GameToggle)
                {
                    if (row.On != on && !s.World.SetGameRecipe(s.Container.Id, row.Guid, on))
                        throw new RequestRefusedException($"The game refused to switch {row.Name}.");
                }
                else if (on) table.Add(row.Guid);
            }
            s.Island.State.TableRecipesOn[s.Container.Id] = table;
            Save(s);
            return Panel(s);
        }

        PanelMsg EmptyTrash(Session s)
        {
            if (s.Role == ActorRole.Guest) throw new RequestRefusedException("Clan members can empty trash.");
            if (s.Container.Deposit.Special != SpecialKind.Trash) throw new RequestRefusedException("Only trash chests can be emptied.");
            var server = _half.Config.Snapshot();
            if (!server.AllowTrash) throw new RequestRefusedException("Emptying trash is turned off on this server.");
            var r = SpecialJobs.EmptyTrash(s.World, new[] { s.Container }, new SpecialSwitches { AllowTrash = true });
            _ctx.Log.LogInfo($"Satisvampory: emptied trash ({r.Destroyed.Values.Sum()} items).");
            return Panel(s);
        }

        PanelMsg SetLoop(Session s, SetLoopReq r)
        {
            var loops = LoopFinder.Find(s.Island.State.Graph.Edges());
            if (!loops.Any(l => l.Key == r.LoopKey && l.Members.Contains(s.Container.Id)))
                throw new RequestRefusedException("That loop no longer exists.");
            Must(s.Edits.SetLoopMode(s.Role, r.LoopKey, (LoopMode)Math.Clamp(r.Mode, 0, 2)));
            Save(s);
            return Panel(s);
        }

        TryItReply TryIt(Session s, TryItReq r)
        {
            if (!TryItem(s.Catalog, r.Item, out var item)) throw new RequestRefusedException($"No item called \"{r.Item}\".");
            var amount = Math.Clamp(r.Amount, 1, 1_000_000);
            Evaluation e = r.Mode switch
            {
                0 => Evaluator.Pull(s.Deposit, item, amount, new Evaluator.PullOptions
                {
                    Reserve = s.Island.State.Rules.Reserve(item, s.Catalog),
                    Sources = InputSources(s),
                }),
                1 => Evaluator.OutputList(s.Deposit, s.Island.State.Graph, s.Container.Id, item, amount),
                _ => Evaluator.Push(s.Deposit, item, amount),
            };
            var reply = new TryItReply { Item = s.Catalog.NameOf(item), Short = e.Short };
            reply.Steps.AddRange(e.Steps.Select(x => new EvalRowMsg { Name = x.Name, Where = x.Plot, Amount = x.Amount, Why = x.Why }));
            reply.NotUsed.AddRange(e.NotUsed.Select(x => new EvalRowMsg { Name = x.Name, Where = x.Plot, Amount = 0, Why = x.Reason }));
            return reply;
        }

        /// <summary>An Input list's own sources; null when it pulls from reverse Mass Deposit (the whole island).</summary>
        IReadOnlyList<EngineId>? InputSources(Session s)
        {
            var lists = s.Island.State.Graph.For(s.Container.Id);
            if (lists.Input.Any(e => e.Kind == EntryKind.MassDeposit)) return null;
            return lists.Input.SelectMany(s.Island.State.Graph.Expand).Distinct().ToList();
        }

        // ---------------- panel view ----------------

        PanelMsg Panel(Session s)
        {
            var c = s.Container;
            var graph = s.Island.State.Graph;
            var lists = graph.For(c.Id);
            var msg = new PanelMsg
            {
                ContainerId = c.Id.Value.ToString("N"),
                Name = c.DisplayName,
                Where = PlotLabel(s.Island, c.Plot),
                NodeKind = c.Kind switch { NodeKind.Station => 1, NodeKind.Heart => 2, _ => 0 },
                CanEdit = s.Role != ActorRole.Guest,
                ReadOnlyReason = s.Role == ActorRole.Guest ? "Clan members can change logistics" : "",
                PullOrder = (int)lists.PullOrder,
                SendOrder = (int)lists.SendOrder,
                HasDemand = EditService.HasDemand(c),
                HasOutput = c.Role != StationRole.UnitSpawner,
            };
            var report = _half.Ticker?.LastReport(s.Island.OwnerKey);
            foreach (var e in lists.Input) msg.Input.Add(Row(s, e, ListSide.Input, report));
            foreach (var e in lists.Output) msg.Output.Add(Row(s, e, ListSide.Output, report));

            var d = c.Deposit;
            msg.Deposit.Takes = (int)d.Takes;
            msg.Deposit.BasePriority = d.BasePriority;
            msg.Deposit.Special = (int)d.Special;
            foreach (var (item, adj) in d.ItemPriority) msg.Deposit.Priorities.Add(new PriorityMsg { Item = item.Guid, Label = s.Catalog.NameOf(item), Adjust = adj });
            foreach (var (cat, adj) in d.CategoryPriority) msg.Deposit.Priorities.Add(new PriorityMsg { Category = cat, Label = "Category " + cat, Adjust = adj });
            foreach (var item in d.BannedItems) msg.Deposit.Bans.Add(new PriorityMsg { Item = item.Guid, Label = s.Catalog.NameOf(item) });
            foreach (var cat in d.BannedCategories) msg.Deposit.Bans.Add(new PriorityMsg { Category = cat, Label = "Category " + cat });
            msg.Deposit.Belts.AddRange(graph.Belts.Values.Where(b => b.Members.Contains(c.Id)).Select(b => b.Name));

            var loop = report?.Loops.FirstOrDefault(l => l.Members.Contains(c.Id));
            if (loop != null)
            {
                var mode = s.Island.State.LoopModes.TryGetValue(loop.Key, out var m) ? m : LoopMode.CycleOnce;
                var waiting = report!.Diagnostics.TryGetValue(c.Id, out var dg) && dg.Contains("loop-waiting");
                msg.LoopBanner = $"Loop · {loop.Members.Count} chests · {Mode(mode)}{(waiting ? " · waiting for a change" : "")}";
                msg.LoopKey = loop.Key;
                msg.LoopMode = (int)mode;
            }
            if (c.Kind == NodeKind.Station)
                foreach (var row in s.World.RecipeRows(c.Id))
                    msg.Recipes.Add(new RecipeMsg
                    {
                        Guid = row.Guid,
                        Name = Pretty(row.Name),
                        Inputs = string.Join(" + ", row.Inputs.Select(x => $"{x.Amount} {s.Catalog.NameOf(x.Item)}")),
                        On = row.On,
                    });
            return msg;
        }

        RowMsg Row(Session s, ListEntry e, ListSide side, Engine.Lists.TickReport? report)
        {
            var graph = s.Island.State.Graph;
            var row = new RowMsg { Kind = e.Kind switch { EntryKind.Belt => 1, EntryKind.MassDeposit => 2, _ => 0 }, FilterSpec = FromFilter(e.Filter) };
            row.Filter = e.Filter.IsAnything ? (side == ListSide.Input ? "anything" : "anything it accepts") : "filter: " + Describe(s.Catalog, e.Filter);

            IEnumerable<EngineId> ends;
            switch (e.Kind)
            {
                case EntryKind.Container:
                {
                    var t = s.World.Find(e.Container);
                    row.ContainerId = e.Container.Value.ToString("N");
                    row.Name = t?.DisplayName ?? "(gone)";
                    row.Where = t == null ? "" : PlotLabel(s.Island, t.Plot);
                    ends = new[] { e.Container };
                    break;
                }
                case EntryKind.Belt:
                {
                    graph.Belts.TryGetValue(e.Belt, out var b);
                    row.BeltId = e.Belt.Value.ToString("N");
                    row.Name = b?.Name ?? "(deleted belt)";
                    var plots = b?.Members.Select(m => s.World.Find(m)?.Plot).Where(p => p != null).Select(p => PlotLabel(s.Island, p!.Value)).Distinct().ToList() ?? new List<string>();
                    row.Where = $"{b?.Members.Count ?? 0} chests" + (plots.Count > 0 ? " · " + string.Join(", ", plots) : "");
                    ends = b?.Members ?? (IEnumerable<EngineId>)Array.Empty<EngineId>();
                    break;
                }
                default:
                    row.Name = side == ListSide.Output ? "Push: Mass Deposit" : "Pull: reverse Mass Deposit";
                    row.Where = side == ListSide.Output ? "each item to the chest that rates it highest" : "whole island · worst-stored first";
                    ends = Array.Empty<EngineId>();
                    break;
            }

            row.Status = "idle";
            if (report != null)
            {
                var self = s.Container.Id;
                var endSet = new HashSet<EngineId>(ends);
                var moved = report.Moves.Where(m => side == ListSide.Output
                    ? m.From == self && (e.Kind == EntryKind.MassDeposit ? m.Why == "mass deposit" : endSet.Contains(m.To))
                    : m.To == self && (e.Kind == EntryKind.MassDeposit || endSet.Contains(m.From))).Sum(m => m.Amount);
                if (moved > 0) { row.Status = $"moving · {moved:N0} last tick"; row.Tone = 1; }
                else if (report.Diagnostics.TryGetValue(self, out var codes) && codes.Count > 0) { row.Status = Explain(codes[0]); row.Tone = 2; }
            }
            return row;
        }

        // ---------------- helpers ----------------

        /// <summary>"Recipe_Ingredient_IronIngot" → "Ingredient Iron Ingot"; the client shows its own names once the catalog lands.</summary>
        static string Pretty(string recipe) =>
            Engine.Model.CatalogBuilder.DisplayNameOf(new GameItem(1, (recipe ?? "").Replace("Recipe_", "Item_"), null, 1, 0, 0));

        static string Mode(LoopMode m) => m switch { LoopMode.EvenDistribution => "even distribution", LoopMode.Off => "off", _ => "cycle once" };

        static string Explain(string code) => code switch
        {
            "raided" => "castle is under attack",
            "disabled" => "logistics are off here",
            "out-of-scope" => "on a plot outside your island",
            "no-source" => "nothing in the input holds what is needed",
            "not-enough" => "not enough for one full craft",
            "reserve" => "only the reserve is left",
            "cap" => "the island holds the production cap",
            "sink-full" => "receiver full",
            "not-wanted" => "no recipe switched on uses it",
            "loop-waiting" => "loop waiting for a change",
            _ => code,
        };

        string PlotLabel(ServerActions.Island island, PlotId plot) =>
            Labels.Plot(_ctx.Save, island.Castles, ProgenitorWorld.PlotOf(island.Standing), plot);

        static string Describe(ItemCatalog catalog, ItemFilter f)
        {
            var parts = f.Items.Select(catalog.NameOf).Concat(f.Categories.Select(c => "Category " + c)).ToList();
            var not = f.ExcludeItems.Select(catalog.NameOf).Concat(f.ExcludeCategories.Select(c => "Category " + c)).ToList();
            var text = parts.Count > 0 ? string.Join(", ", parts) : "anything";
            return not.Count > 0 ? text + " except " + string.Join(", ", not) : text;
        }

        static bool TryItem(ItemCatalog catalog, string text, out ItemId item)
        {
            if (int.TryParse(text, out var guid) && guid != 0) { item = new ItemId(guid); return true; }
            return catalog.TryResolveItem(text ?? string.Empty, out item);
        }

        static ListEntry ToEntry(RowMsg r)
        {
            var filter = ToFilter(r.FilterSpec);
            return r.Kind switch
            {
                1 when Guid.TryParse(r.BeltId, out var b) => new ListEntry { Kind = EntryKind.Belt, Belt = new BeltId(b), Filter = filter },
                2 => new ListEntry { Kind = EntryKind.MassDeposit, Filter = filter },
                0 when Guid.TryParse(r.ContainerId, out var c) => new ListEntry { Kind = EntryKind.Container, Container = new EngineId(c), Filter = filter },
                _ => throw new RequestRefusedException("That row does not name a chest, belt or Mass Deposit."),
            };
        }

        static ItemFilter ToFilter(FilterMsg? m)
        {
            var f = new ItemFilter();
            if (m == null) return f;
            foreach (var i in m.Items) f.Items.Add(new ItemId(i));
            foreach (var c in m.Categories) f.Categories.Add(NameFold.Fold(c));
            foreach (var i in m.ExcludeItems) f.ExcludeItems.Add(new ItemId(i));
            foreach (var c in m.ExcludeCategories) f.ExcludeCategories.Add(NameFold.Fold(c));
            return f;
        }

        static FilterMsg FromFilter(ItemFilter f) => new()
        {
            Items = f.Items.Select(i => i.Guid).ToList(),
            Categories = f.Categories.ToList(),
            ExcludeItems = f.ExcludeItems.Select(i => i.Guid).ToList(),
            ExcludeCategories = f.ExcludeCategories.ToList(),
        };
    }
}
