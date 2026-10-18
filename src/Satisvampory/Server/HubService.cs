// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Plugins;
using Progenitor.Dev;
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
        readonly Dictionary<Entity, (string Token, IDisposable Lease)> _previews = new();

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
            r.Handle<SetHomePriorityReq, MassDepositMsg>((u, q, i) => With(u, s => SetHomePriority(s, q)));
            r.Handle<IslandTryReq, TryItReply>((u, q, i) => With(u, s => IslandTry(s, q)));
            r.Handle<GetBeltsReq, BeltsMsg>((u, q, i) => With(u, s => Belts(s)));
            r.Handle<BeltEditReq, BeltsMsg>((u, q, i) => With(u, s => EditBelts(s, q)));
            r.Handle<GetInventoryReq, InventoryMsg>((u, q, i) => With(u, s => Inventory(s, q)));
            r.Handle<SetViewReq, ViewSaved>((u, q, i) => SetView(u, q));
            AttachLiveInventory();
            r.Handle<SetRuleReq, InventoryMsg>((u, q, i) => With(u, s => SetRule(s, q)));
            r.Handle<FindReq, FindReply>((u, q, i) => With(u, s => Find(s, q)));
            r.Handle<PreviewReq, PreviewReply>((u, q, i) => With(u, s => BeginPreview(s, q)));
            r.Handle<RecipeInfoReq, RecipeInfoReply>((u, q, i) => With(u, s => RecipeInfo(s, q)));
            r.Handle<EndPreviewReq, ViewSaved>((u, q, i) =>
            {
                if (_previews.TryGetValue(u, out var current) && current.Token == q.Token)
                {
                    // Keep open: the lease runs until Return/Esc ends it, its own ten-minute cap, or the player leaves.
                    if (q.Hold && current.Lease is Progenitor.Cameras.PluginCamera.CameraObservation held) return new ViewSaved { Ok = held.Hold() };
                    current.Lease.Dispose(); _previews.Remove(u);
                }
                return new ViewSaved { Ok = true };
            });
            r.Handle<PullReq, TextReply>((u, q, i) => With(u, s => Pull(s, q)));
            r.Handle<StorageCountReq, StorageCountReply>((u, q, i) => With(u, s => StorageCount(s, q)),
                new Progenitor.Networking.RequestOptions { Burst = 20, PerSecond = 10 });
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
            _half.Catalog.Refresh(_ctx);
            var island = ServerActions.Resolve(_ctx, _half, userEntity, user)
                ?? throw new RequestRefusedException("Stand on one of your or your clan's castles.");
            var relation = _ctx.Castles.RelationTo(userEntity, island.Standing);
            var catalog = _half.Catalog.Current.WithEdits(island.State.Categories);
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, island.Castles,
                id => island.State.Deposit.TryGetValue(id, out var d) ? d : null, logistics: island.State)
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
            Stores.Save(_ctx.Save, s.Island.OwnerKey, s.Island.State);
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
                PlotLabel = Labels.Name(_ctx.Save, c),
                Level = c.Level,
                Owner = c.Owner.Name,
                Sharing = s.Island.Scope.Count <= 1 ? "no sharing" : s.Island.OwnerKey.StartsWith("clan/") ? "clan share" : "castle share",
                IsOwner = s.Role == ActorRole.Owner,
                CanEdit = s.Role != ActorRole.Guest,
                LogisticsOn = settings.LogisticsOn,
                DefaultReserve = s.Island.State.Rules.DefaultReserve,
                FeedSalvage = settings.FeedSalvage,
                HeartAutoFeed = settings.HeartAutoFeed,
                RepeatHunts = settings.RepeatHunts,
                Exclude = settings.ExcludeFromSharing,
                Need = need,
                TidyResult = tidy,
                Aliases = string.Join("; ", s.Island.State.Categories.Aliases.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {s.Catalog.NameOf(kv.Value)}")),
                Clan = ClanLines(s),
            };
        }

        /// <summary>Clan page (spec 10): every castle of the standing castle's clan (or owner).</summary>
        List<string> ClanLines(Session s)
        {
            var lines = new List<string>();
            foreach (var c in ClanCastles(s))
            {
                var cs = Stores.Castle(_ctx.Save, c.Territory.Index);
                lines.Add($"{Labels.Name(_ctx.Save, c)} · level {c.Level} · {c.Owner.Name}"
                    + (cs.ExcludeFromSharing ? " · excluded from sharing" : "")
                    + $" · repeat hunts {(cs.RepeatHunts ? "on" : "off")}"
                    + (c.Raided ? " · under attack" : ""));
            }
            return lines;
        }

        List<ICastleHandle> ClanCastles(Session s)
        {
            var owner = s.Island.Standing.Owner.User.ToEntity();
            var clan = _ctx.Clans.Of(owner);
            return (clan.IsNone ? _ctx.Castles.AllForUser(owner) : _ctx.Clans.Castles(clan)).Where(c => c.Enabled).ToList();
        }

        CastleMsg SetCastle(Session s, CastleMsg m)
        {
            MustEdit(s, "castle settings");
            if (m.RepeatAllOn)
            {
                foreach (var cc in ClanCastles(s))
                {
                    var cs = Stores.Castle(_ctx.Save, cc.Territory.Index);
                    if (cs.RepeatHunts) continue;
                    cs.RepeatHunts = true;
                    Stores.SetCastle(_ctx.Save, cc.Territory.Index, cs);
                }
                return Castle(s);
            }
            var c = s.Island.Standing;
            var settings = Stores.Castle(_ctx.Save, c.Territory.Index);
            var label = (m.PlotLabel ?? string.Empty).Trim();
            if (label != settings.PlotLabel && label != Labels.Name(_ctx.Save, c))
            {
                if (s.Role != ActorRole.Owner) throw new RequestRefusedException("Only the castle's owner can rename its plot label.");
                settings.PlotLabel = label;
            }
            var aliases = s.Island.State.Categories.Aliases;
            var wanted = new Dictionary<string, ItemId>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (m.Aliases ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = raw.IndexOf('=');
                if (eq <= 0) throw new RequestRefusedException($"\"{raw.Trim()}\": write alias = item.");
                var alias = raw.Substring(0, eq).Trim();
                var name = raw.Substring(eq + 1).Trim();
                if (!TryItem(s.Catalog, name, out var item)) throw new RequestRefusedException($"No item called \"{name}\".");
                if (_half.Catalog.Current.TryResolveItem(alias, out _)) throw new RequestRefusedException($"\"{alias}\" is already a server alias or item name.");
                wanted[alias] = item;
            }
            aliases.Clear();
            foreach (var kv in wanted) aliases[kv.Key] = kv.Value;
            settings.FeedSalvage = m.FeedSalvage;
            settings.RepeatHunts = m.RepeatHunts;
            settings.LogisticsOn = m.LogisticsOn;
            if (m.Exclude != settings.ExcludeFromSharing)
            {
                if (s.Role != ActorRole.Owner) throw new RequestRefusedException("Only the castle's owner can exclude it from sharing.");
                settings.ExcludeFromSharing = m.Exclude;
            }
            Stores.SetCastle(_ctx.Save, c.Territory.Index, settings);
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
            Homes(s, msg);
            return msg;
        }

        /// <summary>
        /// Spec 03 Overview: categories with the chests that list them, by priority; and every item
        /// stored or listed, with its first homes in Mass Deposit order. Priorities edit in place.
        /// </summary>
        void Homes(Session s, MassDepositMsg msg)
        {
            var chests = IslandScope.Chests(s.World, s.Island.Scope).ToList();
            HomeRowMsg Row(string key, string label, ContainerInfo c, int priority, string why)
            {
                var row = new HomeRowMsg { Key = key, Label = label, ContainerId = c.Id.Value.ToString("N"), Chest = c.DisplayName, Where = Label(s.Island, c.Plot), Priority = priority, Why = why };
                if (TargetResolver.Words(_ctx, c.Id, out var ni, out var ng, out var nt)) { row.NetIndex = ni; row.NetGeneration = ng; row.NetType = nt; }
                return row;
            }
            foreach (var cat in chests.SelectMany(c => c.Deposit.CategoryPriority.Keys).Distinct().OrderBy(x => x))
                foreach (var c in chests.Where(c => c.Deposit.CategoryPriority.ContainsKey(cat)).OrderByDescending(c => c.Deposit.CategoryPriority[cat] + c.Deposit.BasePriority))
                    msg.Homes.Add(Row("c:" + cat, "category " + cat, c, c.Deposit.CategoryPriority[cat], c.Deposit.BasePriority != 0 ? $"base {c.Deposit.BasePriority:+0;-0}" : ""));

            var items = chests.SelectMany(c => c.Deposit.ItemPriority.Keys)
                .Concat(chests.SelectMany(c => s.World.Contents(c.Id)).Select(x => x.Item))
                .Distinct().OrderBy(s.Catalog.NameOf).Take(150);
            foreach (var item in items)
            {
                var label = s.Catalog.NameOf(item);
                foreach (var h in DepositRanker.Rank(s.Deposit, item).InOrder.Take(5))
                {
                    var d = h.Chest.Deposit;
                    var p = d.ItemPriority.TryGetValue(item, out var ip) ? ip
                        : d.CategoryPriority.Where(kv => s.Catalog.InCategory(item, kv.Key)).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
                    msg.Homes.Add(Row("i:" + item.Guid, label, h.Chest, p, h.Why));
                }
            }
        }

        MassDepositMsg SetHomePriority(Session s, SetHomePriorityReq q)
        {
            MustEdit(s, "deposit settings");
            if (!Guid.TryParse(q.ContainerId, out var g)) throw new RequestRefusedException("That chest is gone.");
            var id = new Engine.Model.ContainerId(g);
            if (s.World.Find(id) == null) throw new RequestRefusedException("That chest is not on this island.");
            var p = Math.Clamp(q.Priority, DepositOptions.MinPriority, DepositOptions.MaxPriority);
            var key = q.Key ?? "";
            Must(s.Edits.SetDepositOptions(s.Role, id, d =>
            {
                if (key.StartsWith("c:")) d.CategoryPriority[key.Substring(2)] = p;
                else if (key.StartsWith("i:") && int.TryParse(key.Substring(2), out var item)) d.ItemPriority[new ItemId(item)] = p;
                else throw new RequestRefusedException("Unknown home.");
                if (d.Takes == Takes.Nothing) d.Takes = Takes.OnlyListed;
            }));
            Save(s);
            return MassDeposit(s);
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
                var bm = new BeltMsg { Id = b.Id.Value.ToString("N"), Name = b.Name, BuiltIn = b.Id == DefaultBelt.Id };
                foreach (var output in new[] { true, false })
                foreach (var member in output ? graph.Suppliers(b.Id) : graph.Receivers(b.Id))
                {
                    var c = s.World.Find(member);
                    var row = new RowMsg
                    {
                        ContainerId = member.Value.ToString("N"), Name = c?.DisplayName ?? "(gone)",
                        Where = c == null ? "" : Label(s.Island, c.Plot),
                        Status = c == null ? "Gone" : !s.Island.Scope.Contains(c.Plot) ? "Out of scope" :
                            string.Join(", ", s.World.Contents(member).OrderByDescending(x => x.Amount).Take(2).Select(x => $"{s.Catalog.NameOf(x.Item)} {x.Amount:N0}")),
                    };
                    if (TargetResolver.Words(_ctx, member, out var ni, out var ng, out var nt)) { row.PickIndex = ni; row.PickGeneration = ng; row.PickType = nt; }
                    (output ? bm.Suppliers : bm.Receivers).Add(row);
                    if (!bm.Members.Any(m => m.ContainerId == row.ContainerId)) bm.Members.Add(row);
                    if (row.Where.Length > 0 && !bm.Plots.Contains(row.Where)) bm.Plots.Add(row.Where);
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
            var stock = new Dictionary<ItemId, long>();
            foreach (var plot in s.Island.Scope)
                foreach (var c in s.World.ContainersOn(plot).Where(c => c.Kind == NodeKind.Chest))
                    foreach (var st in s.World.Contents(c.Id))
                        stock[st.Item] = (stock.TryGetValue(st.Item, out var have) ? have : 0) + st.Amount;
            string NameOfContainer(Engine.Model.ContainerId id) => s.World.Find(id) is { } x ? $"{x.DisplayName} · {Label(s.Island, x.Plot)}" : "a chest";
            foreach (var name in s.Catalog.CategoryNames.OrderBy(n => n))
                msg.Categories.Add(new CategoryMsg
                {
                    Name = name,
                    Kind = edits.Custom.ContainsKey(name) ? "Custom" : edits.Edited.ContainsKey(name) ? "Edited" : "Built-in",
                }.Fill(s.Catalog.Members(name).Select(i => (s.Catalog.NameOf(i), i.Guid)).OrderBy(x => x.Item1).Take(80), stock,
                    Engine.Edits.CategoryOps.Usages(s.Island.State, name, NameOfContainer)));
            return msg;
        }

        BeltsMsg EditBelts(Session s, BeltEditReq q)
        {
            MustEdit(s, "belts and categories");
            var graph = s.Island.State.Graph;
            var refs = new BeltReferences { MassDeposit = s.Island.State.MassDeposit };
            BeltId Id() => Guid.TryParse(q.BeltId, out var g) && graph.Belts.ContainsKey(new BeltId(g))
                ? new BeltId(g) : throw new RequestRefusedException("That belt no longer exists.");

            if (q.Direction is not (0 or 1)) throw new RequestRefusedException("Unknown direction.");
            var output = q.Direction == 0;
            switch (q.Op)
            {
                case 0: Must(s.Edits.CreateBelt(s.Role, q.Name, out _)); break;
                case 1: Must(s.Edits.RenameBelt(s.Role, Id(), q.Name)); break;
                case 2: Must(s.Edits.DeleteBelt(s.Role, Id(), out _)); break;
                case 3:
                {
                    var matches = BeltOps.MatchByName(s.World, s.Island.Scope, q.Text).Select(c => c.Id).ToList();
                    if (matches.Count == 0) throw new RequestRefusedException($"No chest on the island is named with \"{q.Text}\".");
                    Must(s.Edits.AddBeltMembers(s.Role, Id(), matches, output));
                    break;
                }
                case 4:
                {
                    var id = Id();
                    var members = (output ? graph.Suppliers(id) : graph.Receivers(id)).ToList();
                    if (q.Index < 0 || q.Index >= members.Count || (q.MemberId.Length > 0 && q.MemberId != members[q.Index].Value.ToString("N")))
                        throw new RequestRefusedException("That member is gone; the belt changed.");
                    BeltOps.Remove(graph, id, members[q.Index], output);
                    s.Island.State.Touched.Add(members[q.Index]);
                    break;
                }
                case 5:
                {
                    var id = Id();
                    var members = (output ? graph.Suppliers(id) : graph.Receivers(id)).ToList();
                    if (q.MemberId.Length > 0 && (q.Index < 0 || q.Index >= members.Count || q.MemberId != members[q.Index].Value.ToString("N")))
                        throw new RequestRefusedException("The belt changed; refresh and try again.");
                    if (!BeltOps.Move(graph, id, q.Index, q.To, output)) throw new RequestRefusedException("That member is gone; the belt changed.");
                    foreach (var member in members) s.Island.State.Touched.Add(member);
                    break;
                }
                case 7:
                {
                    var picked = TargetResolver.Resolve(_ctx, q.PickIndex, q.PickGeneration, q.PickType, out var why)
                        ?? throw new RequestRefusedException(why);
                    if (s.World.Find(picked) == null) throw new RequestRefusedException("That is not on this island.");
                    Must(s.Edits.AddBeltMembers(s.Role, Id(), new[] { picked }, output));
                    break;
                }
                case 6:
                {
                    var r = BeltOps.Duplicate(graph, Id(), q.Name, out var copy);
                    if (copy != null) foreach (var member in graph.Members(copy.Id)) s.Island.State.Touched.Add(member);
                    if (!r.Ok) throw new RequestRefusedException(r.Error);
                    break;
                }
                case 10:
                case 11:
                case 12:
                case 13:
                    EditCategory(s, q);
                    break;
                case 14:
                    Must(Engine.Edits.CategoryOps.Rename(s.Island.State, _half.Catalog.Current, q.Name, q.Text));
                    break;
                case 15:
                    Must(Engine.Edits.CategoryOps.Duplicate(s.Island.State, s.Catalog, q.Name, q.Text));
                    break;
                case 8:
                {
                    var matches = BeltOps.MatchByName(s.World, s.Island.Scope, q.Text).ToList();
                    var preview = Belts(s);
                    preview.PreviewFor = q.BeltId;
                    preview.PreviewDirection = q.Direction;
                    preview.PreviewText = q.Text;
                    preview.Preview.AddRange(matches.Take(30).Select(c => $"{c.DisplayName} · {Label(s.Island, c.Plot)}"));
                    if (matches.Count > 30) preview.Preview.Add($"… and {matches.Count - 30} more");
                    return preview;
                }
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
            var view = Stores.View(_ctx.Save, "inv", s.User.PlatformId);
            msg.Sort = view.Sort;
            msg.Hidden = view.Hidden;
            // Per item and plot: count, chests, and the chest holding the most (spec 15, row expand).
            var byPlot = new Dictionary<ItemId, Dictionary<PlotId, (long Count, int Chests, string Top, long TopCount)>>();
            foreach (var plot in s.Island.Scope)
                foreach (var c in s.World.ContainersOn(plot).Where(c => c.Kind == NodeKind.Chest))
                    foreach (var st in s.World.Contents(c.Id))
                    {
                        if (!byPlot.TryGetValue(st.Item, out var pp)) byPlot[st.Item] = pp = new();
                        pp.TryGetValue(plot, out var v);
                        pp[plot] = (v.Count + st.Amount, v.Chests + 1, st.Amount > v.TopCount ? c.DisplayName : v.Top, Math.Max(v.TopCount, st.Amount));
                    }
            foreach (var r in sorted.Take(InventoryRowCap))
            {
                var row = new InventoryRowMsg
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
                };
                if (byPlot.TryGetValue(r.Item, out var plots))
                    foreach (var (plot, v) in plots.OrderByDescending(x => x.Value.Count))
                    {
                        var label = Label(s.Island, plot);
                        row.PlotLabels.Add(label);
                        row.Plots.Add($"{label} · {v.Count:N0} in {v.Chests} chest{(v.Chests == 1 ? "" : "s")} · most in {v.Top} ({v.TopCount:N0})");
                    }
                msg.Rows.Add(row);
            }
            return msg;
        }

        const int InventoryRowCap = 2000;

        ViewSaved SetView(Entity userEntity, SetViewReq q)
        {
            var view = (q.View ?? "").Trim();
            if (view != "inv") throw new RequestRefusedException("Unknown view.");
            if ((q.Sort?.Length ?? 0) > 400 || (q.Hidden?.Length ?? 0) > 400) throw new RequestRefusedException("Too long.");
            var user = _ctx.World!.EntityManager.GetComponentData<User>(userEntity);
            Stores.SetView(_ctx.Save, view, user.PlatformId, new TableView { Sort = q.Sort ?? "", Hidden = q.Hidden ?? "" });
            return new ViewSaved { Ok = true };
        }

        // ---------------- live inventory (spec 15) ----------------

        const double LiveSeconds = 2;
        SubscriptionTopic<InventoryMsg, InventoryDeltaMsg>? _live;
        /// <summary>Per subscription key: who opened it and the rows last sent, by item.</summary>
        readonly Dictionary<string, (Entity User, string Island, Dictionary<int, string> Rows)> _liveState = new();

        void AttachLiveInventory()
        {
            _live = _ctx.Subscriptions.Topic<InventoryMsg, InventoryDeltaMsg>("sv.inv.live", LiveSnapshot);
            _ctx.Track(_live);
            _live.Closed += (_, key) => _liveState.Remove(key);
            _ctx.Track(new OnDispose(() => _liveStopped = true));
            MainThread.PostDelayed(LiveSeconds, LiveTick, _ctx.Owner);
        }

        bool _liveStopped;

        static string Fingerprint(InventoryRowMsg r) =>
            $"{r.Total}|{r.Here}|{r.Chests}|{r.InStations}|{r.Reserve}|{r.ReserveFrom}|{r.Spare}|{r.Cap}|{r.CapFrom}|{r.Demand}|{r.Status}|{r.Categories}";

        /// <summary>Snapshot for one key. A key belongs to the user who opened it; the island is re-resolved every time.</summary>
        InventoryMsg LiveSnapshot(Entity userEntity, string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 64) throw new RequestRefusedException("Bad key.");
            if (_liveState.TryGetValue(key, out var st) && st.User != userEntity) throw new RequestRefusedException("That key is in use.");
            InventoryMsg msg;
            try { msg = With(userEntity, s => Inventory(s, new GetInventoryReq())); }
            catch (Exception e) { _ctx.Log.LogInfo("Satisvampory: live inventory snapshot refused/failed: " + e.Message); throw; }
            _liveState[key] = (userEntity, msg.Island, msg.Rows.ToDictionary(r => r.Item, Fingerprint));
            return msg;
        }

        void LiveTick()
        {
            try
            {
                if (_live == null || _liveStopped) return;
                foreach (var key in _live.Keys)
                {
                    if (!_liveState.TryGetValue(key, out var st)) { _live.Resync(key); continue; }
                    InventoryMsg now;
                    try { now = With(st.User, s => Inventory(s, new GetInventoryReq())); }
                    catch (RequestRefusedException) { _live.Resync(key); continue; } // the snapshot refuses and ends it
                    if (now.Island != st.Island) { _live.Resync(key); continue; }

                    var delta = new InventoryDeltaMsg();
                    var rows = new Dictionary<int, string>();
                    foreach (var r in now.Rows)
                    {
                        var f = Fingerprint(r);
                        rows[r.Item] = f;
                        if (!st.Rows.TryGetValue(r.Item, out var old) || old != f) delta.Changed.Add(r);
                    }
                    foreach (var item in st.Rows.Keys)
                        if (!rows.ContainsKey(item)) delta.Removed.Add(item);
                    _liveState[key] = (st.User, st.Island, rows);
                    if (delta.Changed.Count > 0 || delta.Removed.Count > 0) _live.Publish(key, delta);
                }
            }
            catch (Exception e)
            {
                _ctx.Log.LogError("Satisvampory: live inventory failed: " + e.Message);
            }
            finally
            {
                if (!_liveStopped) MainThread.PostDelayed(LiveSeconds, LiveTick, _ctx.Owner);
            }
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
            var castles = q.IncludeClanPlots
                ? _ctx.Castles.All().Where(c => c.Enabled && !c.Raided && _ctx.Castles.RelationTo(s.UserEntity, c) is CastleRelation.Owner or CastleRelation.ClanMember).ToList()
                : new List<ICastleHandle> { s.Island.Standing };
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, castles, _ => null);
            var scope = castles.Select(ProgenitorWorld.PlotOf).ToList();
            string PlotLabel(PlotId plot) => castles.Where(c => ProgenitorWorld.PlotOf(c) == plot).Select(c => Labels.Name(_ctx.Save, c)).FirstOrDefault() ?? plot.ToString();
            var r = Engine.Ui.Find.Run(world, s.Catalog, scope, s.Standing, PlotLabel, q.Text ?? "", q.PartialMatch, q.Kind, _half.Catalog.Findable,
                q.ClientItems?.Select(g => new ItemId(g)).ToList());
            var reply = new FindReply { Ambiguous = r.Ambiguous };
            reply.Categories.AddRange(r.Categories);
            EvalRowMsg Row(FoundChest c)
            {
                var row = WithWords(new EvalRowMsg
            {
                ContainerId = c.Id.Value.ToString("N"), Name = c.Name, Where = c.PlotLabel + (c.Here ? " (here)" : ""), Amount = c.Count,
                Why = c.Produces ? c.Count > 0 ? "Stores and makes" : "Can make" : "Stores",
                Level = castles.FirstOrDefault(x => ProgenitorWorld.PlotOf(x) == c.Plot)?.Level ?? 0,
                Holds = string.Join(", ", world.Contents(c.Id).OrderByDescending(x => x.Amount).Take(3).Select(x => $"{s.Catalog.NameOf(x.Item)} {x.Amount:N0}")),
                BuildingPrefab = world.Find(c.Id)?.BuildingPrefab ?? 0,
                Type = TypeOf(world.Find(c.Id)),
            }, c.Id);
                if (_ctx.Castles.TryGetBuildingPosition(world.EntityOf(c.Id), out var position))
                {
                    row.Yaw = world.FacingOf(c.Id) + 180f;
                    row.HasPosition = true; row.X = position.X; row.Y = position.Y; row.Z = position.Z;
                }
                return row;
            }
            foreach (var it in r.Items.Take(25))
                reply.Items.Add(new FoundMsg { Item = it.Item.Guid, Name = it.Name, Total = it.Total,
                    MatchRank = it.MatchRank, Chests = it.Chests.Take(40).Select(Row).ToList() });
            reply.ChestsByName.AddRange(r.ChestsByName.Take(40).Select(Row));
            return reply;
        }

        /// <summary>What a place is, in words the player knows from the game.</summary>
        /// <summary>
        /// A hovered recipe's numbers: per item, the island total in storage, what the station's Inputs reach and
        /// already hold, the reserve and the cap.
        /// </summary>
        RecipeInfoReply RecipeInfo(Session s, RecipeInfoReq q)
        {
            var reply = new RecipeInfoReply
            {
                Scope = s.Island.Castles.Count <= 1 ? "this castle" : s.Island.OwnerKey.StartsWith("clan/") ? "the clan's castles" : "your castles",
            };
            var graph = s.Island.State.Graph;
            ContainerInfo? station = null;
            if (q.Target.NetIndex != 0 || q.Target.NetGeneration != 0 || q.Target.ContainerId.Length > 0)
                station = TargetResolver.Resolve(_ctx, q.Target, out _) is { } sid ? s.World.Find(sid) : null;
            var chests = s.Island.Scope.SelectMany(p => s.World.ContainersOn(p)).Where(c => c.Kind == NodeKind.Chest).ToList();
            HashSet<ContainerId>? reach = null;
            if (station != null)
            {
                var lists = graph.For(station.Id);
                reach = lists.Input.Any(e => e.Kind == EntryKind.MassDeposit)
                    ? chests.Select(c => c.Id).ToHashSet()
                    : lists.Input.SelectMany(e => graph.Expand(e, false, station.Id)).ToHashSet();
            }
            foreach (var guid in q.Items)
            {
                var item = new ItemId(guid);
                reply.Island.Add(chests.Sum(c => (long)s.World.Count(c.Id, item)));
                reply.Reachable.Add(reach == null ? -1 : chests.Where(c => reach.Contains(c.Id)).Sum(c => (long)s.World.Count(c.Id, item)));
                reply.AtStation.Add(station == null ? -1 : s.World.Count(station.Id, item));
                reply.Reserve.Add(s.Island.State.Rules.Reserve(item, s.Catalog));
                reply.Cap.Add(s.Island.State.Rules.Cap(item, s.Catalog) ?? -1);
            }
            return reply;
        }

        internal static string TypeOf(ContainerInfo? c) => c == null ? "" : c.Kind switch
        {
            NodeKind.Heart => "Castle heart",
            NodeKind.Chest => "Storage",
            _ => c.Role switch
            {
                StationRole.Workstation => "Crafting",
                StationRole.Refinement => "Refining",
                StationRole.UnitSpawner => "Unit station",
                StationRole.Brazier => "Brazier",
                StationRole.Stable => "Stable",
                StationRole.Devourer => "Devourer",
                StationRole.Coffin => "Coffin",
                _ => "Station",
            },
        };

        PreviewReply BeginPreview(Session s, PreviewReq q)
        {
            if (!Guid.TryParse(q.ContainerId, out var guid)) throw new RequestRefusedException("Choose a chest or station first.");
            var castles = q.IncludeClanPlots
                ? _ctx.Castles.All().Where(c => c.Enabled && !c.Raided && _ctx.Castles.RelationTo(s.UserEntity, c) is CastleRelation.Owner or CastleRelation.ClanMember).ToList()
                : new List<ICastleHandle> { s.Island.Standing };
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, castles, _ => null);
            var id = new ContainerId(guid);
            if (world.Find(id) == null) throw new RequestRefusedException("That location is no longer accessible.");
            var entity = world.EntityOf(id);
            if (!_ctx.Castles.TryGetBuildingPosition(entity, out var point)) throw new RequestRefusedException("That location is unavailable.");
            if (_previews.Remove(s.UserEntity, out var previous)) previous.Lease.Dispose();
            var token = Guid.NewGuid().ToString("N");
            var lease = _ctx.Camera.Observe(s.UserEntity, entity, 9);
            _previews[s.UserEntity] = (token, lease);
            MainThread.PostDelayed(9.1, () =>
            {
                if (_previews.TryGetValue(s.UserEntity, out var active) && active.Token == token
                    && !(active.Lease is Progenitor.Cameras.PluginCamera.CameraObservation { IsHeld: true }))
                { active.Lease.Dispose(); _previews.Remove(s.UserEntity); }
            }, _ctx.Owner);
            var reply = new PreviewReply { Token = token, X = point.X, Y = point.Y, Z = point.Z,
                Yaw = world.FacingOf(id) + 180, FloorHeight = _ctx.Castles.BuildingFloorHeight(entity), Name = world.Find(id)!.DisplayName,
                Chest = world.Find(id)!.Kind == NodeKind.Chest };
            var held = world.Contents(id).AsEnumerable();
            if (world.Find(id)!.Kind == NodeKind.Station) held = held.Concat(world.Contents(id, InvPart.Output));
            foreach (var g in held.GroupBy(x => x.Item).OrderByDescending(g => g.Sum(x => x.Amount)))
            {
                reply.Items.Add(g.Key.Guid);
                reply.Amounts.Add(g.Sum(x => x.Amount));
            }
            return reply;
        }

        TextReply Pull(Session s, PullReq q)
        {
            var server = _half.Config.Snapshot();
            if (!server.AllowPull) throw new RequestRefusedException("Pulling is turned off on this server.");
            var gate = ServerActions.StateGate(_ctx, s.UserEntity);
            if (gate != StateRefusal.None) throw new RequestRefusedException(ServerActions.Words(gate));
            var prefs = _half.PlayerFor(s.User.PlatformId);
            var character = s.User.LocalCharacter._Entity;
            var bag = s.World.AddBag(_ctx.Inventory.Of(character), player: true);
            var text = (q.Text ?? "").Trim();
            ActionResult result;
            if (s.Catalog.TryResolveCategory(text.StartsWith("category ", StringComparison.OrdinalIgnoreCase) ? text.Substring(9) : text, out var cat) && !s.Catalog.TryResolveItem(text, out _))
                result = PlayerActions.PullGroup(s.World, s.Deposit, bag, cat, q.Amount > 0 ? q.Amount : null, prefs.NeverPullLastStack);
            else if (TryItem(s.Catalog, text, out var item))
                result = PlayerActions.Pull(s.World, s.Deposit, bag, item, q.Amount > 0 ? q.Amount : s.Catalog.MaxStack(item), prefs.NeverPullLastStack);
            else
                throw new RequestRefusedException($"No item or category called \"{text}\".");

            if (result.Items == 0) return new TextReply { Text = "Nothing to take: none on the island, or only last stacks left." };
            if (prefs.SilentPull) return new TextReply { Text = $"Took {result.Items:N0}." };
            return new TextReply { Text = $"Took {result.Items:N0} from {result.Containers(byDestination: false)} chests." + (result.Left.Count > 0 ? $" Short by {result.Left.Values.Sum():N0}." : "") };
        }

        EvalRowMsg WithWords(EvalRowMsg row, Engine.Model.ContainerId id)
        {
            if (TargetResolver.Words(_ctx, id, out var i, out var g, out var t)) { row.NetIndex = i; row.NetGeneration = g; row.NetType = t; }
            return row;
        }

        StorageCountReply StorageCount(Session s, StorageCountReq q)
        {
            var item = new ItemId(q.Item);
            var reply = new StorageCountReply();
            foreach (var c in IslandScope.Chests(s.World, s.Island.Scope))
            {
                var n = s.World.Count(c.Id, item);
                if (n <= 0) continue;
                reply.Total += n;
                reply.Chests++;
            }
            return reply;
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

    internal static class CategoryMsgFill
    {
        internal static CategoryMsg Fill(this CategoryMsg m, IEnumerable<(string Name, int Guid)> items, IReadOnlyDictionary<ItemId, long> stock, List<string> usedBy)
        {
            foreach (var (name, guid) in items)
            {
                m.Members.Add(name);
                m.Items.Add(guid);
                m.Stock.Add(stock.TryGetValue(new ItemId(guid), out var n) ? n : 0);
            }
            m.UsedBy.AddRange(usedBy);
            return m;
        }
    }
}
