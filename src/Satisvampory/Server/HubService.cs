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
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Satisvampory.Engine.Player;
using Satisvampory.Engine.Ui;
using Satisvampory.Net;
using Satisvampory.World;
using IslandScope = Satisvampory.Engine.World.Scope;
using Unity.Entities;

namespace Satisvampory.Server
{
    /// <summary>
    /// Server side of the hub screens: Castle, Mass Deposit, Belts and categories, Inventory,
    /// Find and pull. Every request works on the requester's island, resolved from where they
    /// stand; edits go through the engine and are saved at once.
    /// </summary>
    internal sealed class HubService
    {
        readonly SatisContext _ctx;
        readonly ServerHalf _half;

        internal HubService(SatisContext ctx, ServerHalf half)
        {
            _ctx = ctx;
            _half = half;
        }

        internal void Attach()
        {
            var r = _ctx.Requests;
            r.Handle<GetCastleReq, CastleMsg>((u, q, i) => With(u, s => Castle(s)));
            r.Handle<SetCastleReq, CastleMsg>((u, q, i) => With(u, s => SetCastle(s, q.Castle)));
            r.Handle<TidyReq, CastleMsg>((u, q, i) => With(u, s => Tidy(s)));
            r.Handle<GetMassDepositReq, MassDepositMsg>((u, q, i) => With(u, s => MassDeposit(s)));
            r.Handle<SetMassDepositReq, MassDepositMsg>((u, q, i) => With(u, s => SetMassDeposit(s, q)));
            r.Handle<IslandTryReq, TryItReply>((u, q, i) => With(u, s => IslandTry(s, q)));
            r.Handle<GetBeltsReq, BeltsMsg>((u, q, i) => With(u, s => Belts(s)));
            r.Handle<BeltEditReq, BeltsMsg>((u, q, i) => With(u, s => EditBelts(s, q)));
            r.Handle<GetInventoryReq, InventoryMsg>((u, q, i) => With(u, s => Inventory(s, q)));
            r.Handle<SetRuleReq, InventoryMsg>((u, q, i) => With(u, s => SetRule(s, q)));
            r.Handle<FindReq, FindReply>((u, q, i) => With(u, s => Find(s, q)));
            r.Handle<PullReq, TextReply>((u, q, i) => With(u, s => Pull(s, q)));
        }

        sealed class Session
        {
            public Entity UserEntity;
            public User User;
            public ServerActions.Island Island = null!;
            public ProgenitorWorld World = null!;
            public ActorRole Role;
            public ItemCatalog Catalog = null!;
            public EditService Edits = null!;
            public DepositContext Deposit = null!;
            public PlotId Standing;
        }

        T With<T>(Entity userEntity, Func<Session, T> body)
        {
            var user = _ctx.World!.EntityManager.GetComponentData<User>(userEntity);
            var island = ServerActions.Resolve(_ctx, _half, userEntity, user)
                ?? throw new RequestRefusedException("Stand on one of your or your clan's castles.");
            var relation = _ctx.Castles.RelationTo(userEntity, island.Standing);
            var catalog = _half.Catalog.Current.WithEdits(island.State.Categories);
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, island.Castles,
                id => island.State.Deposit.TryGetValue(id, out var d) ? d : null)
            {
                TableRecipesOn = id => island.State.TableRecipesOn.TryGetValue(id, out var on) ? on : null,
            };
            var standing = ProgenitorWorld.PlotOf(island.Standing);
            return body(new Session
            {
                UserEntity = userEntity,
                User = user,
                Island = island,
                World = world,
                Role = relation switch
                {
                    CastleRelation.Owner => ActorRole.Owner,
                    CastleRelation.ClanMember => ActorRole.ClanMember,
                    _ => ActorRole.Guest,
                },
                Catalog = catalog,
                Edits = new EditService(world, catalog, island.State, () => island.Scope),
                Standing = standing,
                Deposit = new DepositContext(catalog, world, island.Scope, standing)
                {
                    Settings = island.State.MassDeposit,
                    Belts = island.State.Graph.BeltMembers(),
                    PlotLabel = p => Label(island, p),
                },
            });
        }

        void Save(Session s)
        {
            Stores.SetIsland(_ctx.Save, s.Island.OwnerKey, SaveCodec.Capture(s.Island.State, s.Island.State.Graph.Sequence));
            _half.MaterialPolicy.Invalidate();
        }

        static void MustEdit(Session s, string what)
        {
            if (s.Role == ActorRole.Guest) throw new RequestRefusedException($"Clan members can change {what}.");
        }

        static EditResult Must(EditResult r) => r.Ok ? r : throw new RequestRefusedException(r.Reason);

        // ---------------- Castle ----------------

        CastleMsg Castle(Session s, string tidy = "")
        {
            var c = s.Island.Standing;
            var settings = Stores.Castle(_ctx.Save, c.Territory.Index);
            var rows = InventoryReport.Build(s.World, s.Catalog, s.Island.Scope, s.Standing, s.Island.State.Rules);
            var need = InventoryReport.Sort(rows.Where(r => r.Status == InventoryStatus.Short), new[] { new SortKey(InventoryColumn.Demand, true) })
                .Take(10).Select(r => $"{r.Name} · stations want {r.Demand:N0} · spare {r.Spare:N0}").ToList();
            return new CastleMsg
            {
                PlotLabel = string.IsNullOrWhiteSpace(settings.PlotLabel) ? Label(s.Island, s.Standing) : settings.PlotLabel,
                Level = c.Level,
                Owner = c.Owner.Name,
                Sharing = s.Island.Scope.Count <= 1 ? "no sharing" : s.Island.OwnerKey.StartsWith("clan/") ? "clan share" : "castle share",
                IsOwner = s.Role == ActorRole.Owner,
                CanEdit = s.Role != ActorRole.Guest,
                LogisticsOn = s.Island.State.Rules.LogisticsOn,
                DefaultReserve = s.Island.State.Rules.DefaultReserve,
                FeedSalvage = settings.FeedSalvage,
                HeartAutoFeed = settings.HeartAutoFeed,
                RepeatHunts = settings.RepeatHunts,
                Exclude = settings.ExcludeFromSharing,
                Need = need,
                TidyResult = tidy,
            };
        }

        CastleMsg SetCastle(Session s, CastleMsg m)
        {
            MustEdit(s, "castle settings");
            var c = s.Island.Standing;
            var settings = Stores.Castle(_ctx.Save, c.Territory.Index);
            settings.PlotLabel = (m.PlotLabel ?? string.Empty).Trim();
            settings.FeedSalvage = m.FeedSalvage;
            settings.HeartAutoFeed = m.HeartAutoFeed;
            settings.RepeatHunts = m.RepeatHunts;
            if (m.Exclude != settings.ExcludeFromSharing)
            {
                if (s.Role != ActorRole.Owner) throw new RequestRefusedException("Only the castle's owner can exclude it from sharing.");
                settings.ExcludeFromSharing = m.Exclude;
            }
            Stores.SetCastle(_ctx.Save, c.Territory.Index, settings);
            s.Island.State.Rules.LogisticsOn = m.LogisticsOn;
            Must(s.Edits.SetDefaultReserve(s.Role, Math.Max(0, m.DefaultReserve)));
            Save(s);
            return Castle(s);
        }

        CastleMsg Tidy(Session s)
        {
            MustEdit(s, "storage");
            var r = PlayerActions.Tidy(s.World, s.Deposit, s.Island.State.Graph);
            var text = r.Items == 0 ? "Nothing to tidy." : $"Tidied {r.Items:N0} items ({r.Kinds} kinds) across {r.Containers(byDestination: true)} chests.";
            return Castle(s, text);
        }

        // ---------------- Mass Deposit ----------------

        MassDepositMsg MassDeposit(Session s, List<ScriptMessage>? messages = null)
        {
            var md = s.Island.State.MassDeposit;
            var msg = new MassDepositMsg { Script = md.AdvancedScript, CanEdit = s.Role != ActorRole.Guest, NeedsReview = s.Edits.ScriptNeedsReview ?? "" };
            foreach (var (sel, t) in md.ItemRules)
                msg.Overrides.Add(new OverrideMsg
                {
                    Items = sel.Kind switch { ItemSelectorKind.Any => "any", ItemSelectorKind.Category => "category " + sel.Category, _ => s.Catalog.NameOf(sel.Item) },
                    TargetKind = t.Kind.ToString().ToLowerInvariant(),
                    TargetName = t.Kind == TargetKind.Special ? t.Special.ToString() : t.Name,
                    TargetPlot = t.Plot ?? "",
                });
            if (messages != null) msg.Messages.AddRange(messages.Select(m => new LineErrorMsg { Line = m.Line, Text = m.Text, IsError = m.IsError }));

            // Overview: items on the island and where the next stack of each would go.
            var held = IslandScope.Chests(s.World, s.Island.Scope).SelectMany(c => s.World.Contents(c.Id)).Select(x => x.Item).Distinct().Take(60);
            foreach (var item in held.OrderBy(s.Catalog.NameOf))
            {
                var homes = DepositRanker.Rank(s.Deposit, item).InOrder.Take(3).ToList();
                if (homes.Count == 0) continue;
                msg.Overview.Add($"{s.Catalog.NameOf(item)} → " + string.Join(", ", homes.Select(h => $"{h.Chest.DisplayName} · {Label(s.Island, h.Chest.Plot)} ({h.Why})")));
            }
            return msg;
        }

        MassDepositMsg SetMassDeposit(Session s, SetMassDepositReq q)
        {
            MustEdit(s, "Mass Deposit");
            List<ScriptMessage>? messages = null;
            if (q.SaveOverrides)
            {
                var parsed = q.Overrides.Select(o => ParseOverride(s, o)).ToList();
                Must(s.Edits.SetRows(s.Role, md =>
                {
                    md.ItemRules.Clear();
                    md.ItemRules.AddRange(parsed);
                }));
            }
            if (q.SaveScript)
            {
                var compiled = AdvancedScript.Compile(q.Script ?? "", s.Catalog, new ScriptNames
                {
                    ChestNames = new HashSet<string>(IslandScope.Chests(s.World, s.Island.Scope).Select(c => NameFold.Fold(c.DisplayName))),
                    Belts = new HashSet<string>(s.Island.State.Graph.Belts.Values.Select(b => NameFold.Fold(b.Name))),
                    PlotLabels = new HashSet<string>(s.Island.Castles.Select(c => NameFold.Fold(Label(s.Island, ProgenitorWorld.PlotOf(c))))),
                });
                messages = compiled.Messages;
                if (compiled.Ok) Must(s.Edits.SaveScript(s.Role, q.Script ?? ""));
            }
            Save(s);
            return MassDeposit(s, messages);
        }

        (ItemSelector, Target) ParseOverride(Session s, OverrideMsg o)
        {
            var items = (o.Items ?? "").Trim();
            ItemSelector sel;
            if (items.Length == 0 || items.Equals("any", StringComparison.OrdinalIgnoreCase)) sel = ItemSelector.Any;
            else if (items.StartsWith("category ", StringComparison.OrdinalIgnoreCase) && s.Catalog.TryResolveCategory(items.Substring(9), out var cat))
                sel = new ItemSelector(ItemSelectorKind.Category, Category: cat);
            else if (s.Catalog.TryResolveCategory(items, out var cat2)) sel = new ItemSelector(ItemSelectorKind.Category, Category: cat2);
            else if (TryItem(s.Catalog, items, out var item)) sel = new ItemSelector(ItemSelectorKind.Item, item);
            else throw new RequestRefusedException($"No item or category called \"{items}\".");

            var kind = (o.TargetKind ?? "").ToLowerInvariant() switch
            {
                "belt" => TargetKind.Belt,
                "chest" => TargetKind.Chest,
                "plot" => TargetKind.Plot,
                "overflow" => TargetKind.Overflow,
                "general" => TargetKind.General,
                "special" => TargetKind.Special,
                _ => throw new RequestRefusedException("Choose a belt, chest, plot, overflow, general or special target."),
            };
            var special = SpecialKind.None;
            if (kind == TargetKind.Special && !Enum.TryParse(o.TargetName, true, out special))
                throw new RequestRefusedException($"No special kind called \"{o.TargetName}\".");
            return (sel, new Target(kind, o.TargetName ?? "", string.IsNullOrWhiteSpace(o.TargetPlot) ? null : o.TargetPlot, special));
        }

        TryItReply IslandTry(Session s, IslandTryReq q)
        {
            if (!TryItem(s.Catalog, q.Item, out var item)) throw new RequestRefusedException($"No item called \"{q.Item}\".");
            var n = Math.Clamp(q.Amount, 1, 1_000_000);
            var e = q.Direction == 1
                ? Evaluator.Pull(s.Deposit, item, n, new Evaluator.PullOptions { Reserve = s.Island.State.Rules.Reserve(item, s.Catalog) })
                : Evaluator.Push(s.Deposit, item, n);
            var reply = new TryItReply { Item = s.Catalog.NameOf(item), Short = e.Short };
            reply.Steps.AddRange(e.Steps.Select(x => new EvalRowMsg { Name = x.Name, Where = x.Plot, Amount = x.Amount, Why = x.Why }));
            reply.NotUsed.AddRange(e.NotUsed.Select(x => new EvalRowMsg { Name = x.Name, Where = x.Plot, Why = x.Reason }));
            return reply;
        }

        // ---------------- Belts and categories ----------------

        BeltsMsg Belts(Session s)
        {
            var graph = s.Island.State.Graph;
            var refs = new BeltReferences { MassDeposit = s.Island.State.MassDeposit };
            var msg = new BeltsMsg { CanEdit = s.Role != ActorRole.Guest };
            foreach (var b in graph.Belts.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
            {
                var bm = new BeltMsg { Id = b.Id.Value.ToString("N"), Name = b.Name };
                foreach (var m in b.Members)
                {
                    var c = s.World.Find(m);
                    bm.Members.Add(new RowMsg { ContainerId = m.Value.ToString("N"), Name = c?.DisplayName ?? "(gone)", Where = c == null ? "" : Label(s.Island, c.Plot) });
                }
                foreach (var u in BeltOps.Usages(graph, b.Id, refs))
                    bm.UsedBy.Add(u.Kind switch
                    {
                        BeltUseKind.InputEntry => $"Input of {s.World.Find(u.Container!.Value)?.DisplayName ?? "a chest"}",
                        BeltUseKind.OutputEntry => $"Output of {s.World.Find(u.Container!.Value)?.DisplayName ?? "a chest"}",
                        BeltUseKind.MassDepositItemRule => $"Mass Deposit override {u.Index + 1}",
                        _ => $"Advanced rule, line {u.Index}",
                    });
                msg.Belts.Add(bm);
            }
            var edits = s.Island.State.Categories;
            foreach (var name in s.Catalog.CategoryNames.OrderBy(n => n))
                msg.Categories.Add(new CategoryMsg
                {
                    Name = name,
                    Kind = edits.Custom.ContainsKey(name) ? "Custom" : edits.Edited.ContainsKey(name) ? "Edited" : "Built-in",
                    Members = s.Catalog.Members(name).Select(s.Catalog.NameOf).OrderBy(x => x).Take(80).ToList(),
                });
            return msg;
        }

        BeltsMsg EditBelts(Session s, BeltEditReq q)
        {
            MustEdit(s, "belts and categories");
            var graph = s.Island.State.Graph;
            var refs = new BeltReferences { MassDeposit = s.Island.State.MassDeposit };
            BeltId Id() => Guid.TryParse(q.BeltId, out var g) && graph.Belts.ContainsKey(new BeltId(g))
                ? new BeltId(g) : throw new RequestRefusedException("That belt no longer exists.");

            switch (q.Op)
            {
                case 0: Must(s.Edits.CreateBelt(s.Role, q.Name, out _)); break;
                case 1: Must(s.Edits.RenameBelt(s.Role, Id(), q.Name)); break;
                case 2: Must(s.Edits.DeleteBelt(s.Role, Id(), out _)); break;
                case 3:
                {
                    var matches = BeltOps.MatchByName(s.World, s.Island.Scope, q.Text).Select(c => c.Id).ToList();
                    if (matches.Count == 0) throw new RequestRefusedException($"No chest on the island is named with \"{q.Text}\".");
                    Must(s.Edits.AddBeltMembers(s.Role, Id(), matches));
                    break;
                }
                case 4:
                {
                    var belt = graph.Belts[Id()];
                    if (q.Index < 0 || q.Index >= belt.Members.Count) throw new RequestRefusedException("That member is gone; the belt changed.");
                    belt.Members.RemoveAt(q.Index);
                    break;
                }
                case 5:
                    if (!BeltOps.Move(graph, Id(), q.Index, q.To)) throw new RequestRefusedException("That member is gone; the belt changed.");
                    break;
                case 6:
                {
                    var r = BeltOps.Duplicate(graph, Id(), q.Name, out _);
                    if (!r.Ok) throw new RequestRefusedException(r.Error);
                    break;
                }
                case 10:
                case 11:
                case 12:
                case 13:
                    EditCategory(s, q);
                    break;
                default:
                    throw new RequestRefusedException("Unknown edit.");
            }
            Save(s);
            // Rebuild the catalog view so category edits show at once.
            s.Catalog = _half.Catalog.Current.WithEdits(s.Island.State.Categories);
            return Belts(s);
        }

        void EditCategory(Session s, BeltEditReq q)
        {
            var edits = s.Island.State.Categories;
            var name = NameFold.Fold(q.Name);
            if (name.Length == 0) throw new RequestRefusedException("A category needs a name.");
            var builtIn = _half.Catalog.Current.IsBuiltIn(name);
            List<ItemId> Members()
            {
                if (edits.Custom.TryGetValue(name, out var c)) return c;
                if (edits.Edited.TryGetValue(name, out var e)) return e;
                var copy = s.Catalog.Members(name).ToList();
                if (builtIn) edits.Edited[name] = copy;
                else edits.Custom[name] = copy;
                return copy;
            }
            switch (q.Op)
            {
                case 10:
                    if (builtIn) throw new RequestRefusedException("That name belongs to a built-in category.");
                    edits.Custom[name] = edits.Custom.TryGetValue(name, out var existing) ? existing : new List<ItemId>();
                    break;
                case 11:
                    if (!TryItem(s.Catalog, q.Text, out var add)) throw new RequestRefusedException($"No item called \"{q.Text}\".");
                    var m = Members();
                    if (!m.Contains(add)) m.Add(add);
                    break;
                case 12:
                    if (!TryItem(s.Catalog, q.Text, out var rem)) throw new RequestRefusedException($"No item called \"{q.Text}\".");
                    Members().Remove(rem);
                    break;
                case 13:
                    if (builtIn) edits.Edited.Remove(name);
                    else edits.Custom.Remove(name);
                    break;
            }
        }

        // ---------------- Inventory ----------------

        InventoryMsg Inventory(Session s, GetInventoryReq q)
        {
            var rows = InventoryReport.Build(s.World, s.Catalog, s.Island.Scope, s.Standing, s.Island.State.Rules);
            IEnumerable<InventoryRow> filtered = rows;
            if (!string.IsNullOrWhiteSpace(q.Search))
            {
                var t = NameFold.Fold(q.Search);
                filtered = filtered.Where(r => NameFold.Fold(r.Name).Contains(t) || r.Categories.Any(c => c.Contains(t)));
            }
            if (q.OnlyShort) filtered = filtered.Where(r => r.Status == InventoryStatus.Short);
            var col = Enum.IsDefined(typeof(InventoryColumn), q.SortColumn) ? (InventoryColumn)q.SortColumn : InventoryColumn.Item;
            var sorted = InventoryReport.Sort(filtered, new[] { new SortKey(col, q.Descending) });
            var msg = new InventoryMsg { CanEdit = s.Role != ActorRole.Guest, Island = string.Join(" + ", s.Island.Castles.Select(c => Label(s.Island, ProgenitorWorld.PlotOf(c)))) };
            foreach (var r in sorted.Take(500))
                msg.Rows.Add(new InventoryRowMsg
                {
                    Item = r.Item.Guid,
                    Name = r.Name,
                    Categories = string.Join(", ", r.Categories),
                    Total = r.Total,
                    Here = r.Here,
                    Chests = r.Chests,
                    InStations = r.InStations,
                    Reserve = r.Reserve,
                    ReserveFrom = (int)r.ReserveFrom,
                    Spare = r.Spare,
                    Cap = r.Cap ?? -1,
                    CapFrom = (int)r.CapFrom,
                    Demand = r.Demand,
                    Status = r.Status switch
                    {
                        InventoryStatus.Short => "Short",
                        InventoryStatus.BelowReserve => "Below reserve",
                        InventoryStatus.AtCap => "At cap",
                        InventoryStatus.NoStock => "No stock",
                        _ => "",
                    },
                });
            return msg;
        }

        InventoryMsg SetRule(Session s, SetRuleReq q)
        {
            int? value = q.Value < 0 ? null : q.Value;
            var target = (q.Target ?? "").Trim();
            if (target.StartsWith("category ", StringComparison.OrdinalIgnoreCase))
            {
                var cat = target.Substring(9);
                Must(q.Rule == 0 ? s.Edits.SetCategoryReserve(s.Role, cat, value) : s.Edits.SetCategoryCap(s.Role, cat, value));
            }
            else
            {
                if (!TryItem(s.Catalog, target, out var item)) throw new RequestRefusedException($"No item called \"{target}\".");
                Must(q.Rule == 0 ? s.Edits.SetItemReserve(s.Role, item, value) : s.Edits.SetItemCap(s.Role, item, value));
            }
            Save(s);
            return Inventory(s, new GetInventoryReq());
        }

        // ---------------- Find and pull ----------------

        FindReply Find(Session s, FindReq q)
        {
            var r = Engine.Ui.Find.Run(s.World, s.Catalog, s.Island.Scope, s.Standing, p => Label(s.Island, p), q.Text ?? "");
            var reply = new FindReply { Ambiguous = r.Ambiguous };
            reply.Categories.AddRange(r.Categories);
            foreach (var it in r.Items.Take(25))
                reply.Items.Add(new FoundMsg
                {
                    Name = it.Name,
                    Total = it.Total,
                    Chests = it.Chests.Take(20).Select(c => new EvalRowMsg { Name = c.Name, Where = c.PlotLabel, Amount = c.Count }).ToList(),
                });
            reply.ChestsByName.AddRange(r.ChestsByName.Take(20).Select(c => new EvalRowMsg { Name = c.Name, Where = c.PlotLabel, Amount = c.Count }));
            return reply;
        }

        TextReply Pull(Session s, PullReq q)
        {
            var server = _half.Config.Snapshot();
            if (!server.AllowPull) throw new RequestRefusedException("Pulling is turned off on this server.");
            var prefs = _half.PlayerFor(s.User.PlatformId);
            var character = s.User.LocalCharacter._Entity;
            var bag = s.World.AddBag(_ctx.Inventory.Of(character));
            var text = (q.Text ?? "").Trim();
            ActionResult result;
            if (s.Catalog.TryResolveCategory(text.StartsWith("category ", StringComparison.OrdinalIgnoreCase) ? text.Substring(9) : text, out var cat) && !s.Catalog.TryResolveItem(text, out _))
                result = PlayerActions.PullGroup(s.World, s.Deposit, bag, cat, q.Amount > 0 ? q.Amount : null, prefs.NeverPullLastStack);
            else if (TryItem(s.Catalog, text, out var item))
                result = PlayerActions.Pull(s.World, s.Deposit, bag, item, Math.Max(1, q.Amount), prefs.NeverPullLastStack);
            else
                throw new RequestRefusedException($"No item or category called \"{text}\".");

            if (result.Items == 0) return new TextReply { Text = "Nothing to take: none on the island, or only last stacks left." };
            if (prefs.SilentPull) return new TextReply { Text = $"Took {result.Items:N0}." };
            return new TextReply { Text = $"Took {result.Items:N0} from {result.Containers(byDestination: false)} chests." + (result.Left.Count > 0 ? $" Short by {result.Left.Values.Sum():N0}." : "") };
        }

        // ---------------- helpers ----------------

        static bool TryItem(ItemCatalog catalog, string text, out ItemId item)
        {
            if (int.TryParse(text, out var guid) && guid != 0) { item = new ItemId(guid); return true; }
            return catalog.TryResolveItem(text ?? string.Empty, out item);
        }

        string Label(ServerActions.Island island, PlotId plot) =>
            Labels.Plot(_ctx.Save, island.Castles, ProgenitorWorld.PlotOf(island.Standing), plot);
    }
}
