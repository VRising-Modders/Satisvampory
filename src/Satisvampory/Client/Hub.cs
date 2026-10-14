// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Progenitor.Inventory;
using Progenitor.Networking;
using Progenitor.Plugins;
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
        public const int CastleTab = 0, MassDepositTab = 1, BeltsTab = 2, InventoryTab = 3, FindTab = 4, HuntLogTab = 5;

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
        const int BeltsPage = 0, CategoriesPage = 1;
        IUiMasterDetail _beltsMd = null!;
        IUiInput _beltName = null!, _beltText = null!;
        IUiInput _catName = null!, _catItem = null!;
        BeltsMsg? _beltsState;

        // Inventory
        IUiInput _invSearch = null!;
        IUiToggle _invShort = null!;
        IUiLabel _invIsland = null!;
        IUiDataTable _invTable = null!;
        IUiChips _invCats = null!;
        bool _invCanEdit;
        // Live inventory (spec 15): a snapshot, then deltas while the tab is open.
        Subscription<InventoryMsg, InventoryDeltaMsg>? _invSub;
        readonly string _invKey = "c" + Guid.NewGuid().ToString("N");
        readonly Dictionary<int, InventoryRowMsg> _invRows = new();
        readonly HashSet<int> _invFlash = new();
        string _invIslandText = "";
        string _invChipSet = "";
        bool _invViewApplied, _invApplyingView;
        IUiInput _ruleTarget = null!;
        IUiDropdown _ruleKind = null!;
        IUiNumber _ruleValue = null!;

        // Find
        IUiInput _findText = null!, _pullAmount = null!;
        IUiLabel _findResult = null!;
        IUiLabel _huntLog = null!;
        FindReply? _lastFind;

        internal Hub(SatisContext ctx, PlayerConfig player)
        {
            _ctx = ctx;
            _player = player;
        }

        internal void OpenTab(int tab) => Open(Math.Clamp(tab, CastleTab, HuntLogTab));

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
            _ctx.Events.On<InventorySortUsed>(OnSort);
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

        /// <summary>Sorting your own inventory twice within a second stashes it (spec 07), when the player wants that.</summary>
        void OnSort(InventorySortUsed e)
        {
            if (e.Target != InventorySortTarget.Own) return;
            if (e.SincePreviousMs < 0 || e.SincePreviousMs > 1000) return;
            if (!_player.Rows.First(r => r.Entry.Definition.Key == "StashOnDoubleSort").Entry.Value) return;
            _ctx.Net.SendToServer(EventIds.Action, new ActionPacket { Magic = EventIds.Magic, Kind = (int)ActionKind.Stash });
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
                case Keys.HuntLog: Open(HuntLogTab); break;
            }
        }

        string _lastPlotLabel = "";

        void Tidy()
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (_player.Rows.First(r => r.Entry.Definition.Key == "TidyConfirm").Entry.Value && now - _tidyArmedAt > 3)
            {
                _tidyArmedAt = now;
                _ctx.Hud.Notify($"Tidy {(_lastPlotLabel.Length > 0 ? _lastPlotLabel : "this castle")}? Press again to confirm.", kind: UiNoticeKind.Info, seconds: 3);
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
            Help.Tip(_ctx, Help.HubTip, panel);
            TabTip(tab);
        }

        void TabTip(int tab)
        {
            var id = tab switch
            {
                MassDepositTab => Help.MassDepositTip,
                BeltsTab => Help.BeltsTip,
                InventoryTab => Help.InventoryTip,
                _ => null,
            };
            if (id != null && _panel != null) Help.Tip(_ctx, id, _panel);
        }

        void Refresh(int tab)
        {
            switch (tab)
            {
                case CastleTab: Send<GetCastleReq, CastleMsg>(new GetCastleReq(), DrawCastle); break;
                case MassDepositTab: Send<GetMassDepositReq, MassDepositMsg>(new GetMassDepositReq(), DrawMassDeposit); break;
                case BeltsTab: Send<GetBeltsReq, BeltsMsg>(new GetBeltsReq(), DrawBelts); break;
                case InventoryTab: LoadInventory(); break;
                case HuntLogTab:
                    Send<GetHuntLogReq, HuntLogReply>(new GetHuntLogReq(), r =>
                        _huntLog.Text = (r.Lines.Count == 0 ? "No hunts in the last 72 hours." : string.Join("\n", r.Lines.Take(40)))
                            + (r.Servants.Count == 0 ? "" : "\n\nServants now:\n  " + string.Join("\n  ", r.Servants)));
                    break;
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
                _tabs = p.Tabs("tabs", new[] { "Castle", "Mass Deposit", "Belts and categories", "Inventory", "Find", "Hunt log" });
                _ctx.Track(_tabs.OnChanged(t => { Refresh(t); TabTip(t); }));
                BuildCastle(p);
                BuildMassDeposit(p);
                BuildBelts(p);
                BuildInventory(p);
                BuildFind(p);
                _huntLog = In(HuntLogTab, p.Label(""));
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
            Track(_defaultReserve.Term("Reserve"));
            _salvage = In(CastleTab, p.Toggle("Feed salvage chests to the devourer", false));
            _heartFeed = In(CastleTab, p.Toggle("Heart fuel auto-feed", true));
            _repeat = In(CastleTab, p.Toggle("Repeat hunts on this castle", true));
            _exclude = In(CastleTab, p.Toggle("Exclude this castle from sharing (owner only)", false));
            _aliases = In(CastleTab, p.Input("Item aliases (this island): rb = Ruby; ior = Iron Ore", ""));
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
                        Aliases = _aliases.Value,
                    },
                }, c => { DrawCastle(c); _ctx.Hud.Notify("Castle settings saved.", kind: UiNoticeKind.Success, seconds: 3); })));
            Track(In(CastleTab, p.Button("Tidy now")).OnClick(() => Send<TidyReq, CastleMsg>(new TidyReq(), DrawCastle)));
            _tidyResult = In(CastleTab, p.Label(""));
            Track(In(CastleTab, p.Button("Inventory: reserves and caps")).OnClick(() => OpenTab(InventoryTab)));
            Track(In(CastleTab, p.Button("Belts and categories")).OnClick(() => OpenTab(BeltsTab)));
            Track(In(CastleTab, p.Button("Mass Deposit")).OnClick(() => OpenTab(MassDepositTab)));
            _need = In(CastleTab, p.Label(""));
            Track(In(CastleTab, p.Button("Show all short items")).OnClick(() => { _ = Panel; _invShort.Value = true; OpenTab(InventoryTab); }));
            In(CastleTab, p.Divider());
            _clan = In(CastleTab, p.Label(""));
            Track(In(CastleTab, p.Button("Repeat hunts: all on")).OnClick(() =>
                Send<SetCastleReq, CastleMsg>(new SetCastleReq { Castle = new CastleMsg { RepeatAllOn = true } }, DrawCastle)));
        }

        IUiInput _aliases = null!;
        IUiLabel _clan = null!;

        void DrawCastle(CastleMsg c)
        {
            _ = Panel;
            _castleHeader.Text = $"{c.PlotLabel} · level {c.Level} · {c.Owner} · {c.Sharing}" + (c.CanEdit ? "" : " · clan members can change these");
            _plotLabel.Value = c.PlotLabel;
            _lastPlotLabel = c.PlotLabel;
            _logistics.Value = c.LogisticsOn;
            _defaultReserve.Value = c.DefaultReserve;
            _salvage.Value = c.FeedSalvage;
            _heartFeed.Value = c.HeartAutoFeed;
            _repeat.Value = c.RepeatHunts;
            _exclude.Value = c.Exclude;
            _tidyResult.Text = c.TidyResult;
            _need.Text = c.Need.Count == 0 ? "Need: nothing is short." : "Need (short on the island):\n  " + string.Join("\n  ", c.Need);
            _aliases.Value = c.Aliases;
            _clan.Text = c.Clan.Count == 0 ? "" : "Clan castles:\n  " + string.Join("\n  ", c.Clan);
        }

        // ---------------- Mass Deposit ----------------

        static readonly string[] KindValues = { "belt", "chest", "plot", "overflow", "general", "special" };

        void BuildMassDeposit(IUiPanel p)
        {
            Track(In(MassDepositTab, p.Label("Each chest's deposit settings decide where items go. Overrides and rules below change that island-wide.")).Term("Mass Deposit"));
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
            _beltsMd = In(BeltsTab, p.MasterDetail("belts", new UiMasterDetailOptions
            {
                Tabs = new[] { "Belts", "Categories" },
                Rows = 12,
                SearchPlaceholder = "Search belts or categories",
                NewCaption = "+ New belt",
                EmptyText = "A belt is a named list of chests and stations you use together, like 'Ore in' for the chests beside your furnaces. Make one, then add it to any Input or Output.",
                DetailEmptyText = "Select a belt to see its chests and where it is used.",
            }));
            _beltsMd.SetTexts(CategoriesPage, newCaption: "+ New category", emptyText: "No categories.", detailEmptyText: "Select a category to see its items.");
            Track(_beltsMd.OnSelected(_ => ShowSelected()));
            Track(_beltsMd.OnAction(OnBeltAction));
            Track(_beltsMd.OnReorder(m => WithBelt(id => EditBelt(new BeltEditReq { Op = 5, BeltId = id, Index = m.From, To = m.To }))));
            Track(_beltsMd.OnRemove(item =>
            {
                if (_beltsMd.Tab == BeltsPage) WithBelt(id => EditBelt(new BeltEditReq { Op = 4, BeltId = id, Index = int.Parse(item.Id) }));
                else if (_beltsMd.SelectedId is { } cat) EditBelt(new BeltEditReq { Op = 12, Name = cat, Text = item.Id });
            }));
            _beltName = In(BeltsTab, p.Input("Belt name (new or rename)", ""));
            _beltText = In(BeltsTab, p.Input("Add chests whose name contains", ""));
            _catName = In(BeltsTab, p.Input("Category name (new)", ""));
            _catItem = In(BeltsTab, p.Input("Item to add to the selected category", ""));
        }

        void WithBelt(Action<string> act)
        {
            if (_beltsMd.Tab != BeltsPage || _beltsMd.SelectedId == null) { _ctx.Hud.Notify("Select a belt first.", kind: UiNoticeKind.Info, seconds: 3); return; }
            act(_beltsMd.SelectedId);
        }

        void OnBeltAction(UiDetailAction a)
        {
            if (a.Tab == BeltsPage)
            {
                switch (a.ActionId)
                {
                    case UiDetailAction.New: EditBelt(new BeltEditReq { Op = 0, Name = _beltName.Value }); break;
                    case "rename": EditBelt(new BeltEditReq { Op = 1, BeltId = a.EntryId, Name = _beltName.Value }); break;
                    case "duplicate": EditBelt(new BeltEditReq { Op = 6, BeltId = a.EntryId, Name = _beltName.Value }); break;
                    case "delete": EditBelt(new BeltEditReq { Op = 2, BeltId = a.EntryId }); break;
                    case "byname": EditBelt(new BeltEditReq { Op = 3, BeltId = a.EntryId, Text = _beltText.Value }); break;
                    case "pick": PickBeltMembers(a.EntryId); break;
                }
                return;
            }
            switch (a.ActionId)
            {
                case UiDetailAction.New: EditBelt(new BeltEditReq { Op = 10, Name = _catName.Value }); break;
                case "additem": EditBelt(new BeltEditReq { Op = 11, Name = a.EntryId, Text = ClientItems.Resolve(_ctx, _catItem.Value) }); break;
                case "restore": EditBelt(new BeltEditReq { Op = 13, Name = a.EntryId }); break;
            }
        }

        /// <summary>Pick in world, repeated: every container or station interacted with joins the belt until Esc.</summary>
        void PickBeltMembers(string beltId)
        {
            var panel = Panel;
            UiApi.Hide(panel);
            var added = 0;
            _ctx.Hud.PickInWorld(result =>
            {
                if (!result.IsCurrent || !result.HasNetworkId)
                {
                    _ctx.Hud.Notify("That was not a container or station Satisvampory can use.", kind: UiNoticeKind.Warning, seconds: 4);
                    return;
                }
                Send<BeltEditReq, BeltsMsg>(new BeltEditReq { Op = 7, BeltId = beltId, PickIndex = result.NetworkIdIndex, PickGeneration = result.NetworkIdGeneration, PickType = result.NetworkId2 }, m =>
                {
                    added++;
                    _beltsState = m;
                    _ctx.Hud.Notify($"Added to the belt ({added} so far). Keep picking, Esc to finish.", kind: UiNoticeKind.Success, seconds: 3);
                });
            }, () =>
            {
                UiApi.Show(panel);
                Send<GetBeltsReq, BeltsMsg>(new GetBeltsReq(), DrawBelts);
            }, new UiWorldPickOptions { Repeat = true, Prompt = "Interact with chests or stations to add them to the belt. Esc to finish." });
        }

        void EditBelt(BeltEditReq req) => Send<BeltEditReq, BeltsMsg>(req, DrawBelts);

        void DrawBelts(BeltsMsg m)
        {
            _ = Panel;
            _beltsState = m;
            _beltsMd.SetEntries(BeltsPage, m.Belts.Select(b => new UiMasterEntry(b.Id, b.Name)
            {
                Note = $"{b.Members.Count} chests",
                Badge = b.UsedBy.Count > 0 ? b.UsedBy.Count.ToString() : "",
                Tooltip = b.UsedBy.Count == 0 ? "Not used anywhere yet." : "Used by " + b.UsedBy.Count,
                Keywords = b.Members.Select(x => x.Name).ToList(),
                Term = "Belt",
            }));
            _beltsMd.SetEntries(CategoriesPage, m.Categories.Select(c => new UiMasterEntry(c.Name, c.Name)
            {
                Note = $"{c.Members.Count} items",
                Badge = c.Kind == "Built-in" ? "" : c.Kind,
                Keywords = c.Members,
                Term = "Category",
            }));
            ShowSelected();
        }

        void ShowSelected()
        {
            var m = _beltsState;
            if (m == null || _beltsMd.SelectedId is not { } id) return;
            var edit = m.CanEdit;
            if (_beltsMd.Tab == BeltsPage)
            {
                var b = m.Belts.FirstOrDefault(x => x.Id == id);
                if (b == null) return;
                var lines = new List<UiDetailLine>
                {
                    UiDetailLine.Title(b.Name,
                        new UiDetailButton("rename", "Rename") { Enabled = edit },
                        new UiDetailButton("duplicate", "Duplicate") { Enabled = edit },
                        new UiDetailButton("delete", "Delete") { Enabled = edit }),
                    UiDetailLine.Heading("Chests and stations", b.Members.Count, "Belt"),
                    UiDetailLine.MemberList(b.Members.Select((x, i) => new UiListItem(i.ToString(), x.Name) { Note = x.Where, Removable = edit }),
                        reorder: edit, remove: edit, emptyText: "No chests yet. Pick them in the world or add by name."),
                    UiDetailLine.Actions(
                        new UiDetailButton("pick", "◎ Pick in world") { Enabled = edit },
                        new UiDetailButton("byname", "+ Add by name") { Enabled = edit }),
                    UiDetailLine.Heading("Used by", b.UsedBy.Count),
                };
                if (b.UsedBy.Count == 0) lines.Add(UiDetailLine.Line("Not used anywhere yet. Add it to an Input or Output."));
                lines.AddRange(b.UsedBy.Select(u => UiDetailLine.Line(u, LabelTone.Normal)));
                _beltsMd.SetDetail(BeltsPage, lines);
            }
            else
            {
                var c = m.Categories.FirstOrDefault(x => x.Name == id);
                if (c == null) return;
                var lines = new List<UiDetailLine>
                {
                    UiDetailLine.Title(c.Name, new UiDetailButton("restore", c.Kind == "Custom" ? "Delete" : "Restore built-in") { Enabled = edit && c.Kind != "Built-in" }),
                    UiDetailLine.Line(c.Kind == "Built-in" ? "Built-in. Adding or removing an item makes an island copy." : c.Kind == "Edited" ? "A built-in category, changed on this island." : "Made on this island."),
                    UiDetailLine.Heading("Items", c.Members.Count, "Category"),
                    UiDetailLine.MemberList(c.Members.Select((x, i) => new UiListItem(i < c.Items.Count ? c.Items[i].ToString() : x, x) { Removable = edit }),
                        reorder: false, remove: edit, emptyText: "No items yet."),
                    UiDetailLine.Actions(new UiDetailButton("additem", "+ Add item") { Enabled = edit }),
                };
                if (c.Members.Count >= 80) lines.Add(UiDetailLine.Line("Showing the first 80."));
                _beltsMd.SetDetail(CategoriesPage, lines);
            }
        }

        // ---------------- Inventory ----------------

        static UiTableColumn Num(string key, string caption, string term = "", bool editable = false) =>
            new(key, caption, UiTableColumnKind.Number) { Term = term, Editable = editable, Format = "#,0", Hideable = key != "total" };

        void BuildInventory(IUiPanel p)
        {
            _invSearch = In(InventoryTab, p.Input("Search", ""));
            _invShort = In(InventoryTab, p.Toggle("Only short", false));
            Track(_invSearch.OnChanged(_ => DrawInventory()));
            Track(_invShort.OnChanged(_ => DrawInventory()));
            Track(In(InventoryTab, p.Button("Refresh")).OnClick(() => { StopLiveInventory(); LoadInventory(); }));
            _invIsland = In(InventoryTab, p.Label(""));
            _invCats = In(InventoryTab, p.Chips("inv.cats", Array.Empty<UiChip>(), new UiChipsOptions
            {
                Toggle = true,
                Remove = false,
                MaxLines = 2,
                EmptyText = "",
            }));
            Track(_invCats.Term("Category"));
            Track(_invCats.OnClick(_ => DrawInventory()));
            _invTable = In(InventoryTab, p.DataTable("inv", new[]
            {
                new UiTableColumn("item", "Item") { Hideable = false },
                Num("total", "Total", "Island"),
                Num("here", "Here"),
                Num("chests", "Chests"),
                Num("stations", "In stations"),
                Num("reserve", "Reserve", "Reserve", editable: true),
                Num("spare", "Spare"),
                Num("cap", "Cap", "Cap", editable: true),
                Num("demand", "Demand"),
                new UiTableColumn("status", "Status"),
            }, new UiDataTableOptions
            {
                VisibleRows = 14,
                TotalRow = false,
                Sort = new[] { new UiTableSortKey("item") },
                EmptyText = "Nothing stored on this island.",
            }));
            Track(_invTable.OnEdit(OnInventoryEdit));
            Track(_invTable.OnSortChanged(_ => SaveInventoryView()));
            Track(_invTable.OnColumnsChanged(_ => SaveInventoryView()));
            In(InventoryTab, p.Divider());
            _ruleTarget = In(InventoryTab, p.Input("Category reserve or cap: \"category <name>\" (items: edit the table)", ""));
            _ruleKind = In(InventoryTab, p.Dropdown("Set", new[] { "0", "1" }, "0", new[] { "Reserve", "Cap" }));
            _ruleValue = In(InventoryTab, p.Number("Value (−1 clears)", -1, -1, 1_000_000));
            Track(In(InventoryTab, p.Button("Apply")).OnClick(() =>
                Send<SetRuleReq, InventoryMsg>(new SetRuleReq { Target = ClientItems.Resolve(_ctx, _ruleTarget.Value), Rule = int.Parse(_ruleKind.Value), Value = (int)Math.Round(_ruleValue.Value) }, ApplySnapshot)));
            Track(new OnDispose(StopLiveInventory));
        }

        /// <summary>Opens the live subscription; the server sends the whole island, then only what changed.</summary>
        void LoadInventory()
        {
            _ = Panel;
            if (_invSub is { State: SubscriptionState.Pending or SubscriptionState.Live }) return;
            _invSub = _ctx.Subscriptions.Subscribe<InventoryMsg, InventoryDeltaMsg>("sv.inv.live", _invKey,
                (m, _) => ApplySnapshot(m),
                (d, _) => ApplyDelta(d),
                (why, text) =>
                {
                    _invSub = null;
                    if (!string.IsNullOrEmpty(text)) _invIsland.Text = "Not live: " + text;
                });
            Progenitor.Dev.MainThread.PostDelayed(3, WatchLiveInventory, _ctx.Owner);
        }

        /// <summary>Ends the subscription once the hub closes or another tab is chosen.</summary>
        void WatchLiveInventory()
        {
            if (_invSub == null) return;
            if (_panel == null || !_panel.Visible || _tabs.Selected != InventoryTab) { StopLiveInventory(); return; }
            Progenitor.Dev.MainThread.PostDelayed(3, WatchLiveInventory, _ctx.Owner);
        }

        void StopLiveInventory()
        {
            var sub = _invSub;
            _invSub = null;
            try { sub?.Dispose(); } catch { }
        }

        void ApplySnapshot(InventoryMsg m)
        {
            _invRows.Clear();
            foreach (var r in m.Rows) _invRows[r.Item] = r;
            _invFlash.Clear();
            _invIslandText = m.Island;
            _invCanEdit = m.CanEdit;
            if (!_invViewApplied)
            {
                _invViewApplied = true;
                ApplyInventoryView(m.Sort, m.Hidden);
            }
            DrawInventory();
        }

        void ApplyDelta(InventoryDeltaMsg d)
        {
            _invFlash.Clear();
            foreach (var r in d.Changed) { _invRows[r.Item] = r; _invFlash.Add(r.Item); }
            foreach (var item in d.Removed) _invRows.Remove(item);
            DrawInventory();
        }

        /// <summary>The player's remembered sort ("col:a,col:d") and hidden columns, from the server.</summary>
        void ApplyInventoryView(string sort, string hidden)
        {
            _invApplyingView = true;
            try
            {
                var keys = (sort ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Split(':'))
                    .Where(k => k.Length == 2 && _invTable.Columns.Any(c => c.Key == k[0]))
                    .Select(k => new UiTableSortKey(k[0], k[1] != "d"))
                    .ToList();
                if (keys.Count > 0) _invTable.Sort = keys;
                var hide = new HashSet<string>((hidden ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
                foreach (var c in _invTable.Columns.Where(c => c.Hideable))
                    _invTable.SetColumnVisible(c.Key, !hide.Contains(c.Key));
            }
            finally { _invApplyingView = false; }
        }

        void SaveInventoryView()
        {
            if (_invApplyingView) return;
            var sort = string.Join(",", _invTable.Sort.Select(k => k.Column + ":" + (k.Ascending ? "a" : "d")));
            var hidden = string.Join(",", _invTable.HiddenColumns);
            _ctx.Requests.Send<SetViewReq, ViewSaved>(new SetViewReq { View = "inv", Sort = sort, Hidden = hidden }, _ => { });
        }

        static IEnumerable<string> Cats(InventoryRowMsg r) => r.Categories.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0);

        void DrawInventory()
        {
            static string From(int f) => f == 1 ? "category" : "default";
            var all = _invRows.Values.ToList();
            _invIsland.Text = _invIslandText + (all.Count >= 2000 ? " · first 2,000 items." : "") + (_invSub is { State: SubscriptionState.Live } ? " · live" : "");

            // Category chips: the categories present, most items first; selected ones filter the table.
            var picked = new HashSet<string>(_invCats.SelectedIds);
            var groups = all.SelectMany(Cats).GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(24).ToList();
            var chipSet = string.Join("|", groups.Select(g => g.Key + g.Count()));
            if (chipSet != _invChipSet)
            {
                _invChipSet = chipSet;
                _invCats.SetChips(groups.Select(g => new UiChip(g.Key, g.Key) { Note = g.Count().ToString(), Removable = false, Selected = picked.Contains(g.Key), Tooltip = "Show only " + g.Key }));
                picked = new HashSet<string>(_invCats.SelectedIds);
            }

            var search = NameKey(_invSearch.Value);
            IEnumerable<InventoryRowMsg> rows = all;
            if (picked.Count > 0) rows = rows.Where(r => Cats(r).Any(picked.Contains));
            if (_invShort.Value) rows = rows.Where(r => r.Status == "Short");
            if (search.Length > 0) rows = rows.Where(r => NameKey(ClientItems.NameOf(_ctx, r.Item, r.Name)).Contains(search) || NameKey(r.Categories).Contains(search));

            _invTable.SetRows(rows.Select(r =>
            {
                var row = new UiTableRow(r.Item.ToString()) { ItemPrefab = r.Item, Detail = r.Categories.Length > 0 ? "Categories: " + r.Categories : "" };
                var tone = _invFlash.Contains(r.Item) ? LabelTone.Ok : LabelTone.Normal;
                row.Text("item", ClientItems.NameOf(_ctx, r.Item, r.Name), tone)
                    .Number("total", r.Total)
                    .Number("here", r.Here)
                    .Number("chests", r.Chests)
                    .Number("stations", r.InStations)
                    .Number("reserve", r.Reserve, inherited: r.ReserveFrom != 2, source: r.ReserveFrom != 2 ? From(r.ReserveFrom) : "")
                    .Number("spare", r.Spare)
                    .Number("cap", r.Cap < 0 ? null : r.Cap, inherited: r.Cap >= 0 && r.CapFrom != 2, source: r.Cap >= 0 && r.CapFrom != 2 ? From(r.CapFrom) : "")
                    .Number("demand", r.Demand)
                    .Text("status", r.Status, r.Status is "Short" or "Below reserve" ? LabelTone.Warn : LabelTone.Normal);
                row.Locked = !_invCanEdit;
                return row;
            }));
        }

        static string NameKey(string? s) => (s ?? "").Trim().ToLowerInvariant();

        /// <summary>An inline Reserve or Cap edit: the server decides; a cleared cell goes back to the default.</summary>
        void OnInventoryEdit(UiTableEdit e)
        {
            if (!_invCanEdit) { e.Reject("Only clan members can change reserves and caps."); return; }
            var value = e.Clear || e.NewValue is not { } v ? -1 : (int)Math.Max(0, Math.Round(v));
            _ctx.Requests.Send<SetRuleReq, InventoryMsg>(new SetRuleReq { Target = e.RowId, Rule = e.Column == "cap" ? 1 : 0, Value = value }, r =>
            {
                if (r.Ok && r.Reply != null) { e.Resolve(); ApplySnapshot(r.Reply); }
                else e.Reject(r.Status == RequestStatus.Refused && !string.IsNullOrEmpty(r.Reason) ? r.Reason : "Not saved: " + r.Status);
            });
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
