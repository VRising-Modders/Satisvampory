// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Satisvampory.Config;
using Satisvampory.Net;
using UiApi = Progenitor.Ui.Ui;

namespace Satisvampory.Client
{
    /// <summary>
    /// The Satisvampory hub: one panel with tabs for Castle, Mass Deposit, Belts and categories,
    /// Inventory and Find (specs 03, 10, 14, 15, 07). Opened from the keybinds and from the
    /// buttons beside the inventory's sort button. Until Progenitor's data table and master-detail
    /// widgets land, lists are drawn as text rows and ordered lists.
    /// </summary>
    internal sealed class Hub
    {
        public const int CastleTab = 0, MassDepositTab = 1, BeltsTab = 2, InventoryTab = 3, FindTab = 4;

        readonly SatisContext _ctx;
        readonly PlayerConfig _player;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _tidyArmedAt = -10;
        IUiPanel? _panel;
        IUiTabs _tabs = null!;

        // Castle
        IUiLabel _castleHeader = null!;
        IUiInput _plotLabel = null!;
        IUiToggle _logistics = null!, _salvage = null!, _heartFeed = null!, _repeat = null!, _exclude = null!;
        IUiNumber _defaultReserve = null!;
        IUiLabel _need = null!, _tidyResult = null!;

        // Mass Deposit
        IUiLabel _overview = null!;
        IUiOrderedList _overrides = null!;
        IUiInput _ovItems = null!, _ovTarget = null!;
        IUiDropdown _ovKind = null!;
        IUiCodeArea _script = null!;
        IUiLabel _scriptNote = null!;
        IUiInput _mdItem = null!, _mdAmount = null!;
        IUiDropdown _mdDirection = null!;
        IUiLabel _mdResult = null!;
        List<OverrideMsg> _overrideDraft = new();

        // Belts and categories
        IUiOrderedList _belts = null!;
        IUiInput _beltName = null!, _beltText = null!;
        IUiLabel _beltDetail = null!;
        IUiOrderedList _categories = null!;
        IUiInput _catName = null!, _catItem = null!;
        BeltsMsg? _beltsState;

        // Inventory
        IUiInput _invSearch = null!;
        IUiDropdown _invSort = null!;
        IUiToggle _invShort = null!;
        IUiLabel _invTable = null!;
        IUiInput _ruleTarget = null!;
        IUiDropdown _ruleKind = null!;
        IUiNumber _ruleValue = null!;

        // Find
        IUiInput _findText = null!, _pullAmount = null!;
        IUiLabel _findResult = null!;
        FindReply? _lastFind;

        internal Hub(SatisContext ctx, PlayerConfig player)
        {
            _ctx = ctx;
            _player = player;
        }

        internal void Attach(Keys keys)
        {
            keys.Pressed += OnKey;
            _ctx.Track(new OnDispose(() => keys.Pressed -= OnKey));

            var inv = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, _ctx.Owner, _ctx.Guid);
            _ctx.Track(inv);
            var stash = inv.Button("Stash");
            _ctx.Track(stash.OnClick(() => _ctx.Net.SendToServer(EventIds.Action, new ActionPacket { Magic = EventIds.Magic, Kind = (int)ActionKind.Stash })));
            _ctx.Track(stash.Tooltip("Put everything but your hotbar where it belongs (Mass Deposit).", "Stash"));
            AttachTooltip();
            var open = inv.Button("Satisvampory");
            _ctx.Track(open.OnClick(() => Open(CastleTab)));
            _ctx.Track(open.Tooltip("Castle, Mass Deposit, belts and categories, inventory, find.", "Satisvampory"));
        }

        readonly Dictionary<int, (double At, string Line)> _storageCache = new();

        /// <summary>"In storage: 1,240 (3 chests)" on item tooltips while on a plot; cached ten seconds per item.</summary>
        void AttachTooltip()
        {
            var tip = UiApi.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipBody, _ctx.Owner, _ctx.Guid);
            _ctx.Track(tip);
            _ctx.Track(tip.OnPanel(e =>
            {
                if (e.Change == VanillaPanelChange.Closed || e.Target.ItemGuid == 0)
                {
                    tip.SetRows(Array.Empty<string>());
                    return;
                }
                var item = e.Target.ItemGuid;
                var now = _clock.Elapsed.TotalSeconds;
                if (_storageCache.TryGetValue(item, out var hit) && now - hit.At < 10)
                {
                    tip.SetRows(hit.Line.Length == 0 ? Array.Empty<string>() : new[] { hit.Line });
                    return;
                }
                _ctx.Requests.Send<StorageCountReq, StorageCountReply>(new StorageCountReq { Item = item }, r =>
                {
                    var line = r.Ok && r.Reply != null && r.Reply.Total > 0 ? $"In storage: {r.Reply.Total:N0} ({r.Reply.Chests} chest{(r.Reply.Chests == 1 ? "" : "s")})" : "";
                    _storageCache[item] = (_clock.Elapsed.TotalSeconds, line);
                    if (tip.IsOpen && tip.Target.ItemGuid == item) tip.SetRows(line.Length == 0 ? Array.Empty<string>() : new[] { line });
                });
            }));
        }

        void OnKey(string id)
        {
            switch (id)
            {
                case Keys.MassDeposit: Open(MassDepositTab); break;
                case Keys.BeltsAndCategories: Open(BeltsTab); break;
                case Keys.Inventory: Open(InventoryTab); break;
                case Keys.Find: Open(FindTab); break;
                case Keys.Tidy: Tidy(); break;
                case Keys.HuntLog:
                    _ctx.Hud.Notify("The hunt log arrives with Progenitor's servant mission API.", kind: UiNoticeKind.Info, seconds: 5);
                    break;
            }
        }

        void Tidy()
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (_player.Rows.First(r => r.Entry.Definition.Key == "TidyConfirm").Entry.Value && now - _tidyArmedAt > 3)
            {
                _tidyArmedAt = now;
                _ctx.Hud.Notify("Tidy this island? Press again to confirm.", kind: UiNoticeKind.Info, seconds: 3);
                return;
            }
            _tidyArmedAt = -10;
            Send<TidyReq, CastleMsg>(new TidyReq(), c =>
            {
                _ctx.Hud.Notify(c.TidyResult, kind: UiNoticeKind.Success, seconds: 5);
                DrawCastle(c);
            });
        }

        void Open(int tab)
        {
            var panel = Panel;
            _tabs.Selected = tab;
            Refresh(tab);
            UiApi.Show(panel);
        }

        void Refresh(int tab)
        {
            switch (tab)
            {
                case CastleTab: Send<GetCastleReq, CastleMsg>(new GetCastleReq(), DrawCastle); break;
                case MassDepositTab: Send<GetMassDepositReq, MassDepositMsg>(new GetMassDepositReq(), DrawMassDeposit); break;
                case BeltsTab: Send<GetBeltsReq, BeltsMsg>(new GetBeltsReq(), DrawBelts); break;
                case InventoryTab: LoadInventory(); break;
            }
        }

        void Send<TReq, TReply>(TReq req, Action<TReply> ok) where TReq : class, new() where TReply : class, new() =>
            _ctx.Requests.Send<TReq, TReply>(req, r =>
            {
                if (r.Ok && r.Reply != null) ok(r.Reply);
                else _ctx.Hud.Notify(r.Status == RequestStatus.Refused && !string.IsNullOrEmpty(r.Reason) ? r.Reason : "Satisvampory: " + r.Status, kind: UiNoticeKind.Warning, seconds: 6);
            });

        IUiPanel Panel
        {
            get
            {
                if (_panel != null) return _panel;
                var p = _panel = UiApi.Panel("Satisvampory", _ctx.Owner, _ctx.Guid);
                p.CaptureMode = UiCaptureMode.WhileShown;
                _ctx.Track(p);
                _tabs = p.Tabs("tabs", new[] { "Castle", "Mass Deposit", "Belts and categories", "Inventory", "Find" });
                _ctx.Track(_tabs.OnChanged(Refresh));
                BuildCastle(p);
                BuildMassDeposit(p);
                BuildBelts(p);
                BuildInventory(p);
                BuildFind(p);
                return p;
            }
        }

        T In<T>(int tab, T handle) where T : IUiHandle => _tabs.Add(tab, handle);

        void Track(IDisposable d) => _ctx.Track(d);

        // ---------------- Castle ----------------

        void BuildCastle(IUiPanel p)
        {
            _castleHeader = In(CastleTab, p.Label(""));
            _plotLabel = In(CastleTab, p.Input("Plot label", ""));
            _logistics = In(CastleTab, p.Toggle("Logistics (lists run)", true));
            _defaultReserve = In(CastleTab, p.Number("Default reserve", 10, 0, 10000));
            _salvage = In(CastleTab, p.Toggle("Feed salvage chests to the devourer", false));
            _heartFeed = In(CastleTab, p.Toggle("Heart fuel auto-feed", true));
            _repeat = In(CastleTab, p.Toggle("Repeat hunts on this castle", true));
            _exclude = In(CastleTab, p.Toggle("Exclude this castle from sharing (owner only)", false));
            Track(In(CastleTab, p.Button("Save castle settings")).OnClick(() =>
                Send<SetCastleReq, CastleMsg>(new SetCastleReq
                {
                    Castle = new CastleMsg
                    {
                        PlotLabel = _plotLabel.Value,
                        LogisticsOn = _logistics.Value,
                        DefaultReserve = (int)Math.Round(_defaultReserve.Value),
                        FeedSalvage = _salvage.Value,
                        HeartAutoFeed = _heartFeed.Value,
                        RepeatHunts = _repeat.Value,
                        Exclude = _exclude.Value,
                    },
                }, c => { DrawCastle(c); _ctx.Hud.Notify("Castle settings saved.", kind: UiNoticeKind.Success, seconds: 3); })));
            Track(In(CastleTab, p.Button("Tidy now")).OnClick(() => Send<TidyReq, CastleMsg>(new TidyReq(), DrawCastle)));
            _tidyResult = In(CastleTab, p.Label(""));
            _need = In(CastleTab, p.Label(""));
        }

        void DrawCastle(CastleMsg c)
        {
            _ = Panel;
            _castleHeader.Text = $"{c.PlotLabel} · level {c.Level} · {c.Owner} · {c.Sharing}" + (c.CanEdit ? "" : " · clan members can change these");
            _plotLabel.Value = c.PlotLabel;
            _logistics.Value = c.LogisticsOn;
            _defaultReserve.Value = c.DefaultReserve;
            _salvage.Value = c.FeedSalvage;
            _heartFeed.Value = c.HeartAutoFeed;
            _repeat.Value = c.RepeatHunts;
            _exclude.Value = c.Exclude;
            _tidyResult.Text = c.TidyResult;
            _need.Text = c.Need.Count == 0 ? "Need: nothing is short." : "Need (short on the island):\n  " + string.Join("\n  ", c.Need);
        }

        // ---------------- Mass Deposit ----------------

        static readonly string[] KindValues = { "belt", "chest", "plot", "overflow", "general", "special" };

        void BuildMassDeposit(IUiPanel p)
        {
            In(MassDepositTab, p.Label("Each chest's deposit settings decide where items go. Overrides and rules below change that island-wide."));
            _overview = In(MassDepositTab, p.Label(""));
            In(MassDepositTab, p.Divider());
            _overrides = In(MassDepositTab, p.OrderedList("md.overrides", Array.Empty<UiListItem>(), new UiOrderedListOptions { EmptyText = "No overrides. Chests' own settings decide." }));
            Track(_overrides.OnReorder(m =>
            {
                var o = _overrideDraft[m.From];
                _overrideDraft.RemoveAt(m.From);
                _overrideDraft.Insert(m.To, o);
                SaveOverrides();
            }));
            Track(_overrides.OnRemove(item =>
            {
                var i = int.Parse(item.Id);
                if (i < _overrideDraft.Count) _overrideDraft.RemoveAt(i);
                SaveOverrides();
            }));
            _ovItems = In(MassDepositTab, p.Input("For items (any / item / category name)", "any"));
            _ovKind = In(MassDepositTab, p.Dropdown("Use first", KindValues, "belt"));
            _ovTarget = In(MassDepositTab, p.Input("Target name (and plot after @)", ""));
            Track(In(MassDepositTab, p.Button("+ Add override")).OnClick(() =>
            {
                var parts = _ovTarget.Value.Split('@');
                _overrideDraft.Add(new OverrideMsg { Items = ClientItems.Resolve(_ctx, _ovItems.Value), TargetKind = _ovKind.Value, TargetName = parts[0].Trim(), TargetPlot = parts.Length > 1 ? parts[1].Trim() : "" });
                SaveOverrides();
            }));
            In(MassDepositTab, p.Divider());
            _script = In(MassDepositTab, p.CodeArea("Advanced rules", "", 10));
            _script.SetHighlighter(Highlight);
            _scriptNote = In(MassDepositTab, p.Label(""));
            Track(In(MassDepositTab, p.Button("Save rules")).OnClick(() =>
                Send<SetMassDepositReq, MassDepositMsg>(new SetMassDepositReq { Script = _script.Value, SaveScript = true }, DrawMassDeposit)));
            In(MassDepositTab, p.Divider());
            _mdItem = In(MassDepositTab, p.Input("Try it: item", ""));
            _mdAmount = In(MassDepositTab, p.Input("Amount", "100"));
            _mdDirection = In(MassDepositTab, p.Dropdown("Direction", new[] { "0", "1" }, "0", new[] { "Push: where would it go", "Pull: where would it come from" }));
            Track(In(MassDepositTab, p.Button("Try it")).OnClick(() =>
            {
                if (!int.TryParse(_mdAmount.Value, out var n)) n = 100;
                Send<IslandTryReq, TryItReply>(new IslandTryReq { Item = ClientItems.Resolve(_ctx, _mdItem.Value), Amount = n, Direction = int.Parse(_mdDirection.Value) },
                    r => _mdResult.Text = TryText(n, r));
            }));
            _mdResult = In(MassDepositTab, p.Label(""));
        }

        void SaveOverrides() =>
            Send<SetMassDepositReq, MassDepositMsg>(new SetMassDepositReq { Overrides = _overrideDraft, SaveOverrides = true }, DrawMassDeposit);

        void DrawMassDeposit(MassDepositMsg m)
        {
            _ = Panel;
            _overrideDraft = m.Overrides.ToList();
            _overview.Text = m.Overview.Count == 0 ? "Nothing stored on the island yet." : string.Join("\n", m.Overview.Take(25)) + (m.Overview.Count > 25 ? $"\n… {m.Overview.Count - 25} more" : "");
            _overrides.SetItems(m.Overrides.Select((o, i) => new UiListItem(i.ToString(), $"{o.Items} → {o.TargetKind} {o.TargetName}" + (o.TargetPlot.Length > 0 ? " @ " + o.TargetPlot : "")) { Removable = m.CanEdit }));
            if (_script.Value != m.Script && m.Messages.Count == 0) _script.Value = m.Script;
            _script.SetErrors(m.Messages.Where(x => x.IsError).Select(x => new UiLineError(x.Line, x.Text)).ToList());
            var warnings = m.Messages.Where(x => !x.IsError).Select(x => $"Line {x.Line}: {x.Text}");
            _scriptNote.Text = string.Join("\n", new[] { m.NeedsReview.Length > 0 ? "Needs review (the last good rules keep running): " + m.NeedsReview : "" }.Concat(warnings).Where(x => x.Length > 0));
        }

        static readonly string[] Words = { "for", "first", "then", "only", "never", "prefer", "here", "any", "category", "chest", "belt", "plot", "general", "overflow", "special" };

        static IReadOnlyList<UiTextSpan> Highlight(string text)
        {
            var spans = new List<UiTextSpan>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '#')
                {
                    var end = text.IndexOf('\n', i);
                    if (end < 0) end = text.Length;
                    spans.Add(new UiTextSpan(i, end - i, "#8F8573"));
                    i = end;
                }
                else if (c == '"')
                {
                    var end = text.IndexOf('"', i + 1);
                    end = end < 0 ? text.Length : end + 1;
                    spans.Add(new UiTextSpan(i, end - i, "#9FD0A0"));
                    i = end;
                }
                else if (char.IsLetter(c))
                {
                    var start = i;
                    while (i < text.Length && char.IsLetter(text[i])) i++;
                    if (Words.Contains(text.Substring(start, i - start).ToLowerInvariant())) spans.Add(new UiTextSpan(start, i - start, "#D8B56A"));
                }
                else i++;
            }
            return spans;
        }

        // ---------------- Belts and categories ----------------

        void BuildBelts(IUiPanel p)
        {
            In(BeltsTab, p.Label("A belt is a named list of chests and stations you use together. A category is a named set of items."));
            _belts = In(BeltsTab, p.OrderedList("belts", Array.Empty<UiListItem>(), new UiOrderedListOptions { Reorder = false, EmptyText = "No belts yet. Make one, then add it to any Input or Output." }));
            Track(_belts.OnExpand((item, open) => { if (open) ShowBelt(item.Id); }));
            Track(_belts.OnRemove(item => EditBelt(new BeltEditReq { Op = 2, BeltId = item.Id })));
            _beltDetail = In(BeltsTab, p.Label(""));
            _beltName = In(BeltsTab, p.Input("Belt name", ""));
            Track(In(BeltsTab, p.Button("+ New belt")).OnClick(() => EditBelt(new BeltEditReq { Op = 0, Name = _beltName.Value })));
            Track(In(BeltsTab, p.Button("Rename selected")).OnClick(() => WithSelectedBelt(id => EditBelt(new BeltEditReq { Op = 1, BeltId = id, Name = _beltName.Value }))));
            _beltText = In(BeltsTab, p.Input("Add chests whose name contains", ""));
            Track(In(BeltsTab, p.Button("+ Add by name")).OnClick(() => WithSelectedBelt(id => EditBelt(new BeltEditReq { Op = 3, BeltId = id, Text = _beltText.Value }))));
            In(BeltsTab, p.Divider());
            _categories = In(BeltsTab, p.OrderedList("categories", Array.Empty<UiListItem>(), new UiOrderedListOptions { Reorder = false, Remove = false }));
            _catName = In(BeltsTab, p.Input("Category", ""));
            _catItem = In(BeltsTab, p.Input("Item", ""));
            Track(In(BeltsTab, p.Button("+ New category")).OnClick(() => EditBelt(new BeltEditReq { Op = 10, Name = _catName.Value })));
            Track(In(BeltsTab, p.Button("+ Add item to category")).OnClick(() => EditBelt(new BeltEditReq { Op = 11, Name = _catName.Value, Text = ClientItems.Resolve(_ctx, _catItem.Value) })));
            Track(In(BeltsTab, p.Button("− Remove item from category")).OnClick(() => EditBelt(new BeltEditReq { Op = 12, Name = _catName.Value, Text = ClientItems.Resolve(_ctx, _catItem.Value) })));
            Track(In(BeltsTab, p.Button("Restore built-in / delete custom")).OnClick(() => EditBelt(new BeltEditReq { Op = 13, Name = _catName.Value })));
        }

        string? _selectedBelt;

        void WithSelectedBelt(Action<string> act)
        {
            if (_selectedBelt == null) { _ctx.Hud.Notify("Open a belt in the list first.", kind: UiNoticeKind.Info, seconds: 3); return; }
            act(_selectedBelt);
        }

        void ShowBelt(string id)
        {
            _selectedBelt = id;
            var b = _beltsState?.Belts.FirstOrDefault(x => x.Id == id);
            if (b == null) return;
            _beltName.Value = b.Name;
            _beltDetail.Text = $"{b.Name}:\n  " + (b.Members.Count == 0 ? "no chests yet" : string.Join("\n  ", b.Members.Select(m => $"{m.Name} · {m.Where}")))
                + (b.UsedBy.Count == 0 ? "\nNot used anywhere yet." : "\nUsed by: " + string.Join("; ", b.UsedBy));
        }

        void EditBelt(BeltEditReq req) => Send<BeltEditReq, BeltsMsg>(req, DrawBelts);

        void DrawBelts(BeltsMsg m)
        {
            _ = Panel;
            _beltsState = m;
            _belts.SetItems(m.Belts.Select(b => new UiListItem(b.Id, b.Name)
            {
                Note = $"{b.Members.Count} chests" + (b.UsedBy.Count > 0 ? $" · used by {b.UsedBy.Count}" : ""),
                Detail = string.Join(", ", b.Members.Select(x => $"{x.Name} ({x.Where})")),
                Removable = m.CanEdit,
            }));
            _categories.SetItems(m.Categories.Select(c => new UiListItem(c.Name, c.Name)
            {
                Badge = c.Kind,
                Note = $"{c.Members.Count} items",
                Detail = string.Join(", ", c.Members.Take(30)),
                Removable = false,
            }));
            if (_selectedBelt != null) ShowBelt(_selectedBelt);
        }

        // ---------------- Inventory ----------------

        static readonly string[] SortValues = { "0", "2", "3", "4", "6", "7", "8", "9", "10" };
        static readonly string[] SortLabels = { "Item", "Total", "Here", "Chests", "Reserve", "Spare", "Cap", "Demand", "Status" };

        void BuildInventory(IUiPanel p)
        {
            _invSearch = In(InventoryTab, p.Input("Search", ""));
            _invSort = In(InventoryTab, p.Dropdown("Sort by", SortValues, "0", SortLabels));
            _invShort = In(InventoryTab, p.Toggle("Only short", false));
            Track(In(InventoryTab, p.Button("Refresh")).OnClick(LoadInventory));
            Track(_invSort.OnChanged(_ => LoadInventory()));
            Track(_invShort.OnChanged(_ => LoadInventory()));
            _invTable = In(InventoryTab, p.Label(""));
            In(InventoryTab, p.Divider());
            _ruleTarget = In(InventoryTab, p.Input("Item, or \"category <name>\"", ""));
            _ruleKind = In(InventoryTab, p.Dropdown("Set", new[] { "0", "1" }, "0", new[] { "Reserve", "Cap" }));
            _ruleValue = In(InventoryTab, p.Number("Value (−1 clears)", -1, -1, 1_000_000));
            Track(In(InventoryTab, p.Button("Apply")).OnClick(() =>
                Send<SetRuleReq, InventoryMsg>(new SetRuleReq { Target = ClientItems.Resolve(_ctx, _ruleTarget.Value), Rule = int.Parse(_ruleKind.Value), Value = (int)Math.Round(_ruleValue.Value) }, _ => LoadInventory())));
        }

        void LoadInventory()
        {
            _ = Panel;
            var col = int.TryParse(_invSort.Value, out var c) ? c : 0;
            Send<GetInventoryReq, InventoryMsg>(new GetInventoryReq { SortColumn = col, Descending = col != 0, Search = _invSearch.Value, OnlyShort = _invShort.Value }, DrawInventory);
        }

        void DrawInventory(InventoryMsg m)
        {
            static string From(int f) => f switch { 2 => "", 1 => " (category)", _ => " (default)" };
            var lines = new List<string> { m.Island, "Item · total · here · chests · in stations · reserve · spare · cap · demand · status" };
            lines.AddRange(m.Rows.Take(60).Select(r =>
                $"{ClientItems.NameOf(_ctx, r.Item, r.Name)} · {r.Total:N0} · {r.Here:N0} · {r.Chests} · {r.InStations:N0} · {r.Reserve:N0}{From(r.ReserveFrom)} · {r.Spare:N0} · {(r.Cap < 0 ? "—" : r.Cap.ToString("N0") + From(r.CapFrom))} · {r.Demand:N0} · {r.Status}"));
            if (m.Rows.Count > 60) lines.Add($"… {m.Rows.Count - 60} more; narrow with Search.");
            _invTable.Text = string.Join("\n", lines);
        }

        // ---------------- Find ----------------

        void BuildFind(IUiPanel p)
        {
            _findText = In(FindTab, p.Input("Item, category or chest name", ""));
            Track(In(FindTab, p.Button("Find")).OnClick(() =>
                Send<FindReq, FindReply>(new FindReq { Text = _findText.Value }, DrawFind)));
            _pullAmount = In(FindTab, p.Input("Take amount (blank: one stack of each)", ""));
            Track(In(FindTab, p.Button("Take into bags")).OnClick(() =>
            {
                int.TryParse(_pullAmount.Value, out var n);
                Send<PullReq, TextReply>(new PullReq { Text = ClientItems.Resolve(_ctx, _findText.Value), Amount = n }, r => _ctx.Hud.Notify(r.Text, kind: UiNoticeKind.Success, seconds: 5));
            }));
            Track(In(FindTab, p.Button("Locate")).OnClick(Locate));
            _findResult = In(FindTab, p.Label(""));
        }

        /// <summary>Ping the chests from the last search (spec 07): the first item's chests, else chests by name.</summary>
        void Locate()
        {
            var rows = _lastFind == null ? new List<EvalRowMsg>()
                : (_lastFind.Items.FirstOrDefault()?.Chests ?? new List<EvalRowMsg>()).Concat(_lastFind.Items.Count == 0 ? _lastFind.ChestsByName : new List<EvalRowMsg>()).ToList();
            int pinged = 0, far = 0;
            foreach (var r in rows.Where(r => r.NetType != 0).Take(10))
            {
                if (_ctx.Hud.TryFindByNetworkId(r.NetIndex, r.NetGeneration, r.NetType, out var entity, out _))
                {
                    _ctx.Hud.Locate(entity, r.Name, 10);
                    pinged++;
                }
                else far++;
            }
            _ctx.Hud.Notify(pinged == 0 ? "Nothing nearby to ping; those chests are too far away to show." : $"Pinged {pinged} chest{(pinged == 1 ? "" : "s")}." + (far > 0 ? $" {far} too far away." : ""),
                kind: UiNoticeKind.Info, seconds: 4);
        }

        void DrawFind(FindReply r)
        {
            _lastFind = r;
            var lines = new List<string>();
            if (r.Ambiguous) lines.Add("Several items match; pick one by its full name:");
            if (r.Categories.Count > 0) lines.Add("Category: " + string.Join(", ", r.Categories) + " (Take gives one stack of each)");
            foreach (var it in r.Items)
            {
                lines.Add($"{it.Name} — {it.Total:N0}");
                lines.AddRange(it.Chests.Select(c => $"   {c.Name} · {c.Where} · {c.Amount:N0}"));
            }
            if (r.ChestsByName.Count > 0)
            {
                lines.Add("Chests named like that:");
                lines.AddRange(r.ChestsByName.Select(c => $"   {c.Name} · {c.Where} · {c.Amount:N0} items"));
            }
            _findResult.Text = lines.Count == 0 ? "Nothing found on your island." : string.Join("\n", lines);
        }

        internal static string TryText(int n, TryItReply r)
        {
            var lines = new List<string> { $"{n:N0} {r.Item}:" };
            lines.AddRange(r.Steps.Select(x => $"  {x.Name} · {x.Where} — {x.Amount:N0}  ({x.Why})"));
            if (r.Short > 0) lines.Add($"  short by {r.Short:N0}");
            if (r.NotUsed.Count > 0)
            {
                lines.Add("Not used:");
                lines.AddRange(r.NotUsed.Take(10).Select(x => $"  {x.Name} · {x.Where} — {x.Why}"));
                if (r.NotUsed.Count > 10) lines.Add($"  … and {r.NotUsed.Count - 10} more");
            }
            return string.Join("\n", lines);
        }
    }
}
