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
        public const int CastleTab = 0, MassDepositTab = 1, BeltsTab = 2, InventoryTab = 3, FindTab = 4, HuntLogTab = 5, LogTab = 6;

        readonly SatisContext _ctx;
        readonly PlayerConfig _player;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _tidyArmedAt = -10;
        IUiPanel? _panel;
        IUiTabs _tabs = null!;

        // Castle
        IUiLabel _castleHeader = null!;
        IUiInput _plotLabel = null!;
        IUiToggle _logistics = null!, _salvage = null!, _repeat = null!, _exclude = null!;
        IUiNumber _defaultReserve = null!;
        IUiLabel _need = null!, _tidyResult = null!;

        // Mass Deposit
        IUiLabel _overview = null!;
        IUiDataTable _homes = null!;
        readonly List<HomeRowMsg> _homeRows = new();
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

        // ---------------- Log ----------------

        IUiLabel _logFor = null!;
        IUiButton _logClear = null!;
        IUiInput _logItem = null!;
        IUiInput _logPlayer = null!;
        IUiDropdown _logKind = null!;
        IUiDropdown _logHours = null!;
        IUiDataTable _logTable = null!;
        string _logContainer = "", _logContainerName = "";
        List<LogRowMsg> _logRows = new();
        int _logSeq;

        void BuildLog(IUiPanel p)
        {
            p.BeginInline();
            _logFor = _tabs.Add(LogTab, p.Label("All chests and stations"));
            _logClear = _tabs.Add(LogTab, p.Button("Show all"));
            p.EndInline();
            Track(_logClear.OnClick(() => { _logContainer = _logContainerName = ""; RunLog(); }));
            p.BeginInline();
            _logItem = _tabs.Add(LogTab, p.Input("Item or category", ""));
            _logPlayer = _tabs.Add(LogTab, p.Input("Player", ""));
            _logKind = _tabs.Add(LogTab, p.CompactDropdown("Show", new[] { "0", "1", "2" }, "0", new[] { "Moves and changes", "Moves", "Setting changes" }));
            // "Everything kept" (whatever the server keeps), or a shorter span; a span past what is kept shows everything kept.
            _logHours = _tabs.Add(LogTab, p.CompactDropdown("Last", new[] { "0", "1", "6", "24", "72", "168" }, "0",
                new[] { "Everything kept", "Hour", "6 hours", "Day", "3 days", "Week" }));
            Track(_tabs.Add(LogTab, p.Button("Refresh")).OnClick(RunLog));
            p.EndInline();
            Track(_logItem.OnSubmitted(_ => RunLog()));
            Track(_logPlayer.OnSubmitted(_ => RunLog()));
            Track(_logKind.OnChanged(_ => RunLog()));
            Track(_logHours.OnChanged(_ => RunLog()));
            _logTable = _tabs.Add(LogTab, p.DataTable("log.rows", new[]
            {
                new UiTableColumn("time", "Time") { Width = 90, Sortable = false },
                new UiTableColumn("what", "What") { Hideable = false, Sortable = false },
                new UiTableColumn("amount", "Amount", UiTableColumnKind.Number) { Width = 110, Format = "#,0", Sortable = false },
                new UiTableColumn("from", "From") { Width = 260, Sortable = false },
                new UiTableColumn("to", "To") { Width = 260, Sortable = false },
                new UiTableColumn("why", "Why") { Width = 230, Sortable = false },
                new UiTableColumn("player", "Player") { Width = 150, Sortable = false },
                new UiTableColumn("actions", "", UiTableColumnKind.Actions) { ActionSlots = 2 },
            }, new UiDataTableOptions
            {
                VisibleRows = 14, MinRows = 8, Selectable = false, ColumnChooser = false, TotalRow = false, Sort = Array.Empty<UiTableSortKey>(),
                EmptyText = "Nothing logged for this filter yet.",
            }));
            Track(_logTable.OnRowAction((id, action) =>
            {
                if (!int.TryParse(id, out var i) || i < 0 || i >= _logRows.Count) return;
                var r = _logRows[i];
                if (action == "from" && r.From.Length > 0) FindContainer(r.From, r.FromName, LogTab);
                else if (action == "to" && r.To.Length > 0) FindContainer(r.To, r.ToName, LogTab);
            }));
        }

        bool _logShown = true, _builtWithLog = true;

        /// <summary>The server keeps a log: the Log tab is there. Changing it rebuilds the hub on its next open.</summary>
        internal bool LogShown
        {
            get => _logShown;
            set
            {
                if (_logShown == value) return;
                _logShown = value;
                if (_panel != null && _builtWithLog != value)
                {
                    UiApi.Hide(_panel);
                    _panel.Dispose();
                    _panel = null;
                }
            }
        }

        /// <summary>The Log tab on one chest or station (the small Log button on its window).</summary>
        internal void OpenLog(string containerId, string name)
        {
            if (!_logShown) return;
            _logContainer = containerId;
            _logContainerName = name;
            _ = Panel;
            _logItem.Value = "";
            _logPlayer.Value = "";
            Open(LogTab);
        }

        void RunLog()
        {
            if (_panel == null) return;
            _logFor.Text = _logContainer.Length > 0 ? "Only " + _logContainerName : "All chests and stations";
            _logClear.Visible = _logContainer.Length > 0;
            var text = _logItem.Value.Trim();
            var seq = ++_logSeq;
            Send<LogReq, LogReply>(new LogReq
            {
                ContainerId = _logContainer,
                Item = text,
                ClientItems = text.Length > 0 ? ClientItems.Matching(_ctx, text, true, 30) : new List<int>(),
                Player = _logPlayer.Value.Trim(),
                Kind = int.TryParse(_logKind.Value, out var k) ? k : 0,
                Hours = int.TryParse(_logHours.Value, out var h) ? h : 0,
            }, reply =>
            {
                if (seq != _logSeq) return;
                _logRows = reply.Rows;
                string Name(int guid) => ClientItems.NameOf(_ctx, guid) ?? "Item";
                string End(string name, string plot) => name.Length == 0 ? "" : plot.Length > 0 ? $"{name} ({plot})" : name;
                _logTable.SetRows(reply.Rows.Select((r, i) =>
                {
                    var change = r.Detail.Length > 0;
                    var time = DateTimeOffset.FromUnixTimeSeconds(r.At).ToLocalTime().ToString("HH:mm");
                    var why = r.For != 0 ? $"{r.Why}, for {Name(r.For)}" : r.Why;
                    if (r.Moves > 1) why += $" (×{r.Moves} moves)";
                    return new UiTableRow(i.ToString()) { ItemPrefab = change ? 0 : r.Item }
                        .Text("time", time)
                        .Text("what", change ? r.Detail : Name(r.Item), change ? LabelTone.Muted : LabelTone.Normal)
                        .Number("amount", change ? null : r.Amount, change ? "" : null)
                        .Text("from", End(r.FromName, r.FromPlot))
                        .Text("to", End(r.ToName, r.ToPlot))
                        .Text("why", why)
                        .Text("player", r.Actor)
                        .Action("from", UiRowActionIcon.Locate, "Find " + (change ? "this chest or station" : "where it came from"), r.From.Length > 0)
                        .Action("to", UiRowActionIcon.Chest, "Find where it went", r.To.Length > 0);
                }));
            });
        }
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
            // Build the hub (hidden) right after load: built on the first key press it stalled that frame for
            // about a second. At load the player is at the menu or joining, where it goes unnoticed.
            Progenitor.Dev.MainThread.PostDelayed(2, WarmUp, _ctx.Owner);
            // Chat links in Satisvampory's deposit and hunt lines: a chest pins it on the map, an item opens Find.
            _ctx.Chat.OnModLink("satisvampory", OnChestLink);
            _ctx.Chat.OnLinkClicked(OnItemLink);

            var inv = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, _ctx.Owner, _ctx.Guid);
            _ctx.Track(inv);
            // One short button fits beside the game's sort bar; it opens a small menu with Stash,
            // Inventory and Find (spec 07, 15). Double-tap sort and the Stash keybind still stash in one step.
            var storage = inv.Button("Storage");
            _ctx.Track(storage.OnClick(ToggleStorageMenu));
            _ctx.Track(storage.Tooltip("Stash, the island inventory, and Find.", "Storage"));
            AttachTooltip();
            // Needs Progenitor's recipe tooltip site (#89); an older Core leaves it off.
            try { RegisterAltHold(); }
            catch (Exception e) { _ctx.Log.LogInfo("Satisvampory: hold Alt for tooltip numbers off (Progenitor too old?): " + e.Message); }
            try { AttachRecipeTooltip(); }
            catch (Exception e) { _ctx.Log.LogInfo("Satisvampory: recipe tooltip numbers off (Progenitor too old?): " + e.Message); }
            _ctx.Events.On<InventorySortUsed>(OnSort);
        }

        IUiPanel? _storageMenu;

        void ToggleStorageMenu()
        {
            if (_storageMenu == null)
            {
                var p = _storageMenu = UiApi.Panel("Storage", _ctx.Owner, _ctx.Guid);
                p.Footer = VersionFooter;
                p.GameWindow();
                p.CaptureMode = UiCaptureMode.WhenHovered;
                _ctx.Track(p);
                var stash = p.Button("Stash");
                _ctx.Track(stash.OnClick(() =>
                {
                    if (!_ctx.Net.SendToServer(EventIds.Action, new ActionPacket { Magic = EventIds.Magic, Kind = (int)ActionKind.Stash }))
                        _ctx.Log.LogWarning("Satisvampory: could not send Stash to the server");
                    UiApi.Hide(p);
                }));
                _ctx.Track(stash.Tooltip("Put everything but your hotbar where it belongs (Mass Deposit).", "Stash"));
                var open = p.Button("Inventory");
                _ctx.Track(open.OnClick(() => { UiApi.Hide(p); Open(InventoryTab); }));
                _ctx.Track(open.Tooltip("Everything on your island, with reserves and caps.", "Inventory"));
                var find = p.Button("Find");
                _ctx.Track(find.OnClick(() => { UiApi.Hide(p); Open(FindTab); }));
                _ctx.Track(find.Tooltip("Find an item, category or chest on your island and take it.", "Find"));
                UiApi.Show(p);
                return;
            }
            if (_storageMenu.Visible) UiApi.Hide(_storageMenu);
            else UiApi.Show(_storageMenu);
        }

        /// <summary>
        /// Advanced tooltip numbers: the "Advanced tooltip numbers" setting is on, or Alt is held (the game's own
        /// advanced-tooltip key). Normal tooltips stay the game's own.
        /// </summary>
        bool Advanced => _player.AdvancedTooltipNumbers || _altHeld;

        bool _altHeld;
        IVanillaAttachHandle? _recipeTip, _itemTip;

        // Its own method: on a Core without lone-modifier holds (Progenitor #102) only this call fails (and is caught).
        [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void RegisterAltHold() =>
            _ctx.Keybinds.RegisterHold("AdvancedTooltipHold", KeyChord.Alt, "Show advanced tooltip numbers while held",
                onPressed: () => SetAltHeld(true), onReleased: () => SetAltHeld(false));

        void SetAltHeld(bool held)
        {
            if (_altHeld == held) return;
            _altHeld = held;
            try { RefreshTooltips(); }
            catch (Exception) { } // an older Core: the next hover shows the change
        }

        [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void RefreshTooltips()
        {
            _recipeTip?.Refresh();
            _itemTip?.Refresh();
        }

        static string CapText(int cap) => cap < 0 ? "Cap none" : $"Cap {cap:N0}";

        /// <summary>
        /// The game's recipe tooltip at a station or in the crafting tab, advanced only: small gray lines with the
        /// product's total, reserve and cap, and per requirement its total, reserve, available and cap. Total is
        /// what the island stores; available is what this station's Inputs reach (the island without a
        /// station) less the reserve.
        /// </summary>
        void AttachRecipeTooltip()
        {
            var body = _recipeTip = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipBody, _ctx.Owner, _ctx.Guid + ".recipe");
            var needs = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirements, _ctx.Owner, _ctx.Guid + ".recipe.req");
            _ctx.Track(body);
            _ctx.Track(needs);
            // Per requirement line where the Core has it (Progenitor #99): each item's numbers right under its line.
            IVanillaAttachHandle[]? perLine = null;
            try { perLine = AttachRequirementLines(); }
            catch (Exception e) { _ctx.Log.LogInfo("Satisvampory: per-line tooltip rows off (Progenitor too old?): " + e.Message); }
            void ClearLines() { if (perLine != null) foreach (var h in perLine) h.ClearRows(); }
            var sequence = 0;
            _ctx.Track(body.OnPanel(e =>
            {
                var seq = ++sequence;
                if (e.Change == VanillaPanelChange.Closed || e.Target.RecipeGuid == 0 || !Advanced) { body.ClearRows(); needs.ClearRows(); ClearLines(); return; }
                var t = e.Target;
                var items = new List<int>();
                if (t.ProductGuid != 0) items.Add(t.ProductGuid);
                items.AddRange(t.Requirements.Select(r => r.ItemGuid));
                if (items.Count == 0) return;
                var target = new TargetMsg { NetIndex = t.NetworkIdIndex, NetGeneration = t.NetworkIdGeneration, NetType = t.NetworkId2 };
                _ctx.Requests.Send<RecipeInfoReq, RecipeInfoReply>(new RecipeInfoReq { Target = target, Items = items }, r =>
                {
                    if (seq != sequence || !r.Ok || r.Reply == null || !body.IsOpen) return;
                    var a = r.Reply;
                    string Name(int guid) => ClientItems.NameOf(_ctx, guid) ?? "Item";
                    var first = 0;
                    if (t.ProductGuid != 0)
                    {
                        body.SetRows(new List<VanillaRow> { new VanillaRow($"Total {a.Island[0]:N0} · Reserve {a.Reserve[0]:N0} · {CapText(a.Cap[0])}", LabelTone.Muted) });
                        first = 1;
                    }
                    var lines = new List<VanillaRow>();
                    ClearLines();
                    for (var i = first; i < items.Count && i < a.Island.Count; i++)
                    {
                        var reach = a.Reachable[i] >= 0 ? a.Reachable[i] : a.Island[i];
                        var available = Math.Max(0, reach - a.Reserve[i]);
                        var numbers = $"Total {a.Island[i]:N0} · Reserve {a.Reserve[i]:N0} · Available {available:N0} · {CapText(a.Cap[i])}";
                        var line = i - first;
                        if (perLine != null && line < perLine.Length)
                            perLine[line].SetRows(new List<VanillaRow> { new VanillaRow(numbers, LabelTone.Muted) });
                        else
                            lines.Add(new VanillaRow($"{Name(items[i])}: {numbers}", LabelTone.Muted));
                    }
                    needs.SetRows(lines);
                });
            }));
        }

        // Its own method: on a Core without per-line anchors only this call fails (and is caught).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        IVanillaAttachHandle[] AttachRequirementLines()
        {
            var handles = new IVanillaAttachHandle[6]; // the game's tooltip has room for six requirement lines
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = UiApi.AttachToRequirementLine(i, _ctx.Owner, _ctx.Guid + ".recipe.line" + i);
                _ctx.Track(handles[i]);
            }
            return handles;
        }

        readonly Dictionary<int, (double At, string[] Lines)> _storageCache = new();

        /// <summary>
        /// Item tooltips while on a plot: "In storage: 1,240 (3 chests)", and with advanced numbers the reserve,
        /// available and cap too. Cached ten seconds per item.
        /// </summary>
        void AttachTooltip()
        {
            var tip = _itemTip = UiApi.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipBody, _ctx.Owner, _ctx.Guid);
            _ctx.Track(tip);
            _ctx.Track(tip.OnPanel(e =>
            {
                if (e.Change == VanillaPanelChange.Closed || e.Target.ItemGuid == 0)
                {
                    tip.SetRows(Array.Empty<string>());
                    return;
                }
                var item = e.Target.ItemGuid;
                var advanced = Advanced;
                var key = advanced ? ~item : item;
                var now = _clock.Elapsed.TotalSeconds;
                if (_storageCache.TryGetValue(key, out var hit) && now - hit.At < 10)
                {
                    tip.SetRows(hit.Lines);
                    return;
                }
                _ctx.Requests.Send<StorageCountReq, StorageCountReply>(new StorageCountReq { Item = item }, r =>
                {
                    var lines = Array.Empty<string>();
                    if (r.Ok && r.Reply is { } c)
                    {
                        var list = new List<string>();
                        if (c.Total > 0) list.Add($"In storage: {c.Total:N0} ({c.Chests} chest{(c.Chests == 1 ? "" : "s")})");
                        if (advanced) list.Add($"Total {c.Total:N0} · Reserve {c.Reserve:N0} · Available {Math.Max(0, c.Total - c.Reserve):N0} · {CapText(c.Cap)}");
                        lines = list.ToArray();
                    }
                    _storageCache[key] = (_clock.Elapsed.TotalSeconds, lines);
                    if (tip.IsOpen && tip.Target.ItemGuid == item) tip.SetRows(lines);
                });
            }));
        }

        /// <summary>Sorting your own inventory twice within a second stashes it (spec 07), when the player wants that.</summary>
        void OnSort(InventorySortUsed e)
        {
            if (e.Target != InventorySortTarget.Own) return;
            if (e.SincePreviousMs < 0 || e.SincePreviousMs > 1000) return;
            if (!_player.Rows.First(r => r.Entry.Definition.Key == "StashOnDoubleSort").Entry.Value) return;
            if (!_ctx.Net.SendToServer(EventIds.Action, new ActionPacket { Magic = EventIds.Magic, Kind = (int)ActionKind.Stash }))
                _ctx.Log.LogWarning("Satisvampory: could not send Stash to the server");
        }

        void OnKey(string id)
        {
            switch (id)
            {
                case Keys.MassDeposit: Open(MassDepositTab); break;
                case Keys.BeltsAndCategories: Open(BeltsTab); break;
                case Keys.Inventory: Open(InventoryTab); break;
                case Keys.Find: OpenFindFresh(); break;
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

        /// <summary>The Find key starts a new search: an empty field and no old results.</summary>
        void OpenFindFresh()
        {
            Open(FindTab);
            ++_findSequence; // a search still in flight must not land in the fresh field
            _findText.Value = "";
            DrawFind(new FindReply());
        }

        void Open(int tab)
        {
            var panel = Panel;
            UiApi.Show(panel);
            // Select after showing: showing a panel restores its remembered tab.
            _tabs.Selected = tab;
            Refresh(tab);
            if (tab == FindTab)
            {
                _findMatches.SetSelected(Array.Empty<string>());
                ShowFindStage(false);
                Progenitor.Dev.MainThread.PostDelayed(0.12, () => { if (panel.Visible && _tabs.Selected == FindTab) _findText.Focus(); }, _ctx.Owner);
            }
            Progenitor.Dev.MainThread.PostDelayed(0.05, () => { if (_tabs.Selected != tab) { _tabs.Selected = tab; Refresh(tab); } }, _ctx.Owner);
            Help.Tip(_ctx, Help.HubTip, panel);
            TabTip(tab);
        }

        void ShowFindBack()
        {
            if (_findBack == null) return;
            _findBack.Visible = _findBackTab >= 0;
            _findBack.Text = _findBackTab == LogTab ? "Back to Log" : "Back";
        }

        void TabTip(int tab)
        {
            (string? id, IUiHandle? control) = tab switch
            {
                MassDepositTab => (Help.MassDepositTip, (IUiHandle?)_homes),
                BeltsTab => (Help.BeltsTip, _beltsMd),
                InventoryTab => (Help.InventoryTip, _invTable),
                _ => (null, null),
            };
            if (id != null && _panel != null) Help.Tip(_ctx, id, _panel, control);
        }

        void Refresh(int tab)
        {
            switch (tab)
            {
                case CastleTab: Send<GetCastleReq, CastleMsg>(new GetCastleReq(), DrawCastle); break;
                case MassDepositTab: Send<GetMassDepositReq, MassDepositMsg>(new GetMassDepositReq(), DrawMassDeposit); break;
                case BeltsTab: Send<GetBeltsReq, BeltsMsg>(new GetBeltsReq(), DrawBelts); break;
                case InventoryTab: LoadInventory(); break;
                case FindTab: ShowFindStage(_findAtLocations); break;
                case LogTab: if (_logShown) RunLog(); break;
                case HuntLogTab:
                    Send<GetHuntLogReq, HuntLogReply>(new GetHuntLogReq(), r =>
                        _huntLog.Text = (r.Lines.Count == 0 ? "No hunts in the last 72 hours." : ClientItems.Missions(_ctx, string.Join("\n", r.Lines.Take(40))))
                            + (r.Servants.Count == 0 ? "" : "\n\nServants now:\n  " + string.Join("\n  ", r.Servants)));
                    break;
            }
        }

        void Send<TReq, TReply>(TReq req, Action<TReply> ok) where TReq : class, new() where TReply : class, new() =>
            _ctx.Requests.Send<TReq, TReply>(req, r =>
            {
                if (r.Ok && r.Reply != null) ok(r.Reply);
                else
                {
                    _ctx.Log.LogInfo($"Satisvampory: {typeof(TReq).Name} -> {r.Status} {r.Reason} {r.NotSent}");
                    _ctx.Hud.Notify(r.Status == RequestStatus.Refused && !string.IsNullOrEmpty(r.Reason) ? r.Reason : r.Status.ToString(), kind: UiNoticeKind.Warning, seconds: 6);
                }
            });

        int _warmTries;

        void WarmUp()
        {
            if (_panel != null) return;
            try { _ = Panel; }
            catch (Exception e)
            {
                // The game's UI may not be up yet (very early load): try again shortly, a few times.
                _panel = null;
                if (++_warmTries < 10) Progenitor.Dev.MainThread.PostDelayed(5, WarmUp, _ctx.Owner);
                else _ctx.Log.LogInfo("Satisvampory: hub warm-up skipped: " + e.Message);
            }
        }

        /// <summary>Every Satisvampory window says which Satisvampory and Progenitor it is.</summary>
        internal static string VersionFooter => $"Satisvampory {MyPluginInfo.PLUGIN_VERSION} · {UiApi.FrameworkFooter}";

        IUiPanel Panel
        {
            get
            {
                if (_panel != null) return _panel;
                var p = _panel = UiApi.Panel("Satisvampory", _ctx.Owner, _ctx.Guid);
                p.GameWindow(1440);
                p.CaptureMode = UiCaptureMode.WhileShown;
                _ctx.Track(p);
                p.Footer = VersionFooter;
                // The Log tab only while the server keeps a log (it is the last tab, so the others keep their numbers).
                var captions = new List<string> { "Castle", "Deposit", "Belts", "Inventory", "Find", "Hunts" };
                if (_logShown) captions.Add("Log");
                _builtWithLog = _logShown;
                _tabs = p.Tabs("tabs", captions.ToArray(), new UiTabsOptions());
                // Showing a tab shows all of its controls; the Back button only belongs there when Find came from another tab.
            _ctx.Track(_tabs.OnChanged(t => { Refresh(t); TabTip(t); if (t != FindTab) _findBackTab = -1; ShowFindBack(); }));
                BuildCastle(p);
                BuildMassDeposit(p);
                BuildBelts(p);
                BuildInventory(p);
                BuildFind(p);
                _huntLog = _tabs.Add(HuntLogTab, p.Label(""));
                if (_logShown) BuildLog(p);
                // A new panel draws at once; only Open shows the hub, so a redraw from a reply (Tidy) stays hidden.
                UiApi.Hide(p);
                return p;
            }
        }

        // Call the framework generic directly: a mod-defined generic instantiated with Core types pins the mod to Core on .NET 6.

        void Track(IDisposable d) => _ctx.Track(d);

        // ---------------- Castle ----------------

        void BuildCastle(IUiPanel p)
        {
            _castleHeader = _tabs.Add(CastleTab, p.Label(""));
            _plotLabel = _tabs.Add(CastleTab, p.Input("Plot label", ""));
            _logistics = _tabs.Add(CastleTab, p.Toggle("Logistics (lists run)", true));
            _defaultReserve = _tabs.Add(CastleTab, p.Number("Default reserve", 10, 0, 10000));
            Track(_defaultReserve.Term("Reserve"));
            _salvage = _tabs.Add(CastleTab, p.Toggle("Feed salvage chests to the devourer", false));
            _repeat = _tabs.Add(CastleTab, p.Toggle("Repeat hunts on this castle", true));
            _exclude = _tabs.Add(CastleTab, p.Toggle("Exclude this castle from sharing (owner only)", false));
            _aliases = _tabs.Add(CastleTab, p.Input("Item aliases (this island): rb = Ruby; ior = Iron Ore", ""));
            Track(_tabs.Add(CastleTab, p.Button("Save castle settings")).OnClick(() =>
                Send<SetCastleReq, CastleMsg>(new SetCastleReq
                {
                    Castle = new CastleMsg
                    {
                        PlotLabel = _plotLabel.Value,
                        LogisticsOn = _logistics.Value,
                        DefaultReserve = (int)Math.Round(_defaultReserve.Value),
                        FeedSalvage = _salvage.Value,
                        RepeatHunts = _repeat.Value,
                        Exclude = _exclude.Value,
                        Aliases = _aliases.Value,
                    },
                }, c => { DrawCastle(c); _ctx.Hud.Notify("Castle settings saved.", kind: UiNoticeKind.Success, seconds: 3); })));
            Track(_tabs.Add(CastleTab, p.Button("Tidy now")).OnClick(() => Send<TidyReq, CastleMsg>(new TidyReq(), DrawCastle)));
            _tidyResult = _tabs.Add(CastleTab, p.Label(""));
            Track(_tabs.Add(CastleTab, p.Button("Inventory: reserves and caps")).OnClick(() => OpenTab(InventoryTab)));
            Track(_tabs.Add(CastleTab, p.Button("Belts and categories")).OnClick(() => OpenTab(BeltsTab)));
            Track(_tabs.Add(CastleTab, p.Button("Mass Deposit")).OnClick(() => OpenTab(MassDepositTab)));
            _need = _tabs.Add(CastleTab, p.Label(""));
            Track(_tabs.Add(CastleTab, p.Button("Show all short items")).OnClick(() => { _ = Panel; _invShort.Value = true; OpenTab(InventoryTab); }));
            _tabs.Add(CastleTab, p.Divider());
            _clan = _tabs.Add(CastleTab, p.Label(""));
            Track(_tabs.Add(CastleTab, p.Button("Repeat hunts: all on")).OnClick(() =>
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
            Track(_tabs.Add(MassDepositTab, p.Label("Each chest's deposit settings decide where items go. Overrides and rules below change that island-wide.")).Term("Mass Deposit"));
            _overview = _tabs.Add(MassDepositTab, p.Label(""));
            _homes = _tabs.Add(MassDepositTab, p.DataTable("md.homes", new[]
            {
                new UiTableColumn("chest", "Chest") { Hideable = false },
                new UiTableColumn("where", "Plot"),
                new UiTableColumn("priority", "Priority", UiTableColumnKind.Number) { Editable = true, Format = "+0;-0;0", Term = "Priority" },
                new UiTableColumn("why", "Why"),
            }, new UiDataTableOptions { VisibleRows = 10, Grouped = true, TotalRow = false, Sort = Array.Empty<UiTableSortKey>(), EmptyText = "No chest lists anything yet; stash still fills chests that take anything." }));
            Track(_homes.OnEdit(e =>
            {
                if (!int.TryParse(e.RowId, out var i) || i >= _homeRows.Count || e.NewValue is not { } v) { e.Reject("Type a priority from -10 to 10."); return; }
                var h = _homeRows[i];
                _ctx.Requests.Send<SetHomePriorityReq, MassDepositMsg>(new SetHomePriorityReq { ContainerId = h.ContainerId, Key = h.Key, Priority = (int)Math.Round(v) }, r =>
                {
                    if (r.Ok && r.Reply != null) { e.Resolve(); DrawMassDeposit(r.Reply); }
                    else e.Reject(r.Reason.Length > 0 ? r.Reason : "Not saved: " + r.Status);
                });
            }));
            Track(_tabs.Add(MassDepositTab, p.Button("Locate selected chest")).OnClick(() =>
            {
                foreach (var id in _homes.Selected)
                    if (int.TryParse(id, out var i) && i < _homeRows.Count && _homeRows[i].NetType != 0
                        && _ctx.Hud.TryFindByNetworkId(_homeRows[i].NetIndex, _homeRows[i].NetGeneration, _homeRows[i].NetType, out var entity, out _))
                        _ctx.Hud.Locate(entity, _homeRows[i].Chest, 10);
            }));
            _tabs.Add(MassDepositTab, p.Divider());
            _overrides = _tabs.Add(MassDepositTab, p.OrderedList("md.overrides", Array.Empty<UiListItem>(), new UiOrderedListOptions { EmptyText = "No overrides. Chests' own settings decide." }));
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
            _ovItems = _tabs.Add(MassDepositTab, p.Input("For items (any / item / category name)", "any"));
            _ovKind = _tabs.Add(MassDepositTab, p.Dropdown("Use first", KindValues, "belt"));
            _ovTarget = _tabs.Add(MassDepositTab, p.Input("Target name (and plot after @)", ""));
            Track(_tabs.Add(MassDepositTab, p.Button("+ Add override")).OnClick(() =>
            {
                var parts = _ovTarget.Value.Split('@');
                _overrideDraft.Add(new OverrideMsg { Items = ClientItems.Resolve(_ctx, _ovItems.Value), TargetKind = _ovKind.Value, TargetName = parts[0].Trim(), TargetPlot = parts.Length > 1 ? parts[1].Trim() : "" });
                SaveOverrides();
            }));
            _tabs.Add(MassDepositTab, p.Divider());
            _script = _tabs.Add(MassDepositTab, p.CodeArea("Advanced rules", "", 10));
            _script.SetHighlighter(Highlight);
            _scriptNote = _tabs.Add(MassDepositTab, p.Label(""));
            Track(_tabs.Add(MassDepositTab, p.Button("Save rules")).OnClick(() =>
                Send<SetMassDepositReq, MassDepositMsg>(new SetMassDepositReq { Script = _script.Value, SaveScript = true }, DrawMassDeposit)));
            _tabs.Add(MassDepositTab, p.Divider());
            _mdItem = _tabs.Add(MassDepositTab, p.Input("Try it: item", ""));
            _mdAmount = _tabs.Add(MassDepositTab, p.Input("Amount", "100"));
            _mdDirection = _tabs.Add(MassDepositTab, p.Dropdown("Direction", new[] { "0", "1" }, "0", new[] { "Push: where would it go", "Pull: where would it come from" }));
            Track(_tabs.Add(MassDepositTab, p.Button("Try it")).OnClick(() =>
            {
                RunTry();
                if (!_tryWatching) { _tryWatching = true; Progenitor.Dev.MainThread.PostDelayed(5, RefreshTry, _ctx.Owner); }
            }));
            _mdResult = _tabs.Add(MassDepositTab, p.Label(""));
        }

        bool _tryWatching;

        void RunTry()
        {
            if (!int.TryParse(_mdAmount.Value, out var n)) n = 100;
            Send<IslandTryReq, TryItReply>(new IslandTryReq { Item = ClientItems.Resolve(_ctx, _mdItem.Value), Amount = n, Direction = int.Parse(_mdDirection.Value) },
                r => _mdResult.Text = TryText(n, r));
        }

        /// <summary>The evaluator's answer follows the island while the Mass Deposit tab stays open.</summary>
        void RefreshTry()
        {
            if (_panel == null || !_panel.Visible || _tabs.Selected != MassDepositTab) { _tryWatching = false; return; }
            RunTry();
            Progenitor.Dev.MainThread.PostDelayed(5, RefreshTry, _ctx.Owner);
        }

        void SaveOverrides() =>
            Send<SetMassDepositReq, MassDepositMsg>(new SetMassDepositReq { Overrides = _overrideDraft, SaveOverrides = true }, DrawMassDeposit);

        void DrawMassDeposit(MassDepositMsg m)
        {
            _ = Panel;
            _overrideDraft = m.Overrides.ToList();
            _overview.Text = "Homes by item and category, best first. Edit a priority to move a chest up or down; it is saved in that chest's own deposit settings.";
            _homeRows.Clear();
            _homeRows.AddRange(m.Homes);
            _homes.SetRows(m.Homes.Select((h, i) =>
            {
                var row = new UiTableRow(i.ToString()) { Groups = new[] { h.Label } };
                row.Text("chest", h.Chest).Text("where", h.Where).Number("priority", h.Priority).Text("why", h.Why);
                row.Locked = !m.CanEdit;
                return row;
            }));
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

        int _beltDirection;

        void BuildBelts(IUiPanel p)
        {
            _beltsMd = _tabs.Add(BeltsTab, p.MasterDetail("belts", new UiMasterDetailOptions
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

            Track(_beltsMd.OnRemove(item =>
            {
                if (_beltsMd.Tab == BeltsPage) WithBelt(id =>
                {
                    var parts = item.Id.Split(':');
                    EditBelt(new BeltEditReq { Op = 4, BeltId = id, Direction = int.Parse(parts[0]), Index = int.Parse(parts[1]), MemberId = parts[2] });
                });
                else if (_beltsMd.SelectedId is { } cat) EditBelt(new BeltEditReq { Op = 12, Name = cat, Text = item.Id });
            }));
            _beltName = _tabs.Add(BeltsTab, p.Input("Belt name (new or rename)", ""));
            _beltText = _tabs.Add(BeltsTab, p.Input("Add chests whose name contains", ""));
            _catName = _tabs.Add(BeltsTab, p.Input("Category name (new, rename or duplicate)", ""));
            _catItem = _tabs.Add(BeltsTab, p.Input("Item to add to the selected category", ""));
        }

        string? _deleteArmed;
        float _deleteArmedAt;

        void WithBelt(Action<string> act)
        {
            if (_beltsMd.Tab != BeltsPage || _beltsMd.SelectedId == null) { _ctx.Hud.Notify("Select a belt first.", kind: UiNoticeKind.Info, seconds: 3); return; }
            act(_beltsMd.SelectedId);
        }

        void OnBeltAction(UiDetailAction a)
        {
            if (a.Tab == BeltsPage)
            {
                if (a.ActionId == "suppliers" || a.ActionId == "receivers")
                { _beltDirection = a.ActionId == "suppliers" ? 0 : 1; ShowSelected(); return; }
                if (a.ActionId.StartsWith("move:"))
                {
                    var parts = a.ActionId.Split(':');
                    EditBelt(new BeltEditReq { Op = 5, BeltId = a.EntryId, Direction = int.Parse(parts[1]),
                        Index = int.Parse(parts[2]), To = int.Parse(parts[3]), MemberId = parts[4] });
                    return;
                }
                switch (a.ActionId)
                {
                    case UiDetailAction.New: EditBelt(new BeltEditReq { Op = 0, Name = _beltName.Value }); break;
                    case "rename": EditBelt(new BeltEditReq { Op = 1, BeltId = a.EntryId, Name = _beltName.Value }); break;
                    case "duplicate": EditBelt(new BeltEditReq { Op = 6, BeltId = a.EntryId, Name = _beltName.Value }); break;
                    case "delete":
                    {
                        // Spec 14: delete asks first, listing what uses the belt.
                        var now = UnityEngine.Time.realtimeSinceStartup;
                        if (_deleteArmed != a.EntryId || now - _deleteArmedAt > 4)
                        {
                            _deleteArmed = a.EntryId;
                            _deleteArmedAt = now;
                            var uses = _beltsState?.Belts.FirstOrDefault(b => b.Id == a.EntryId)?.UsedBy ?? new List<string>();
                            _ctx.Hud.Notify("Delete this belt? Press Delete again." + (uses.Count == 0 ? " Nothing uses it." : $" It is removed from: {string.Join("; ", uses.Take(4))}{(uses.Count > 4 ? "; …" : "")}."), kind: UiNoticeKind.Warning, seconds: 4);
                            break;
                        }
                        _deleteArmed = null;
                        EditBelt(new BeltEditReq { Op = 2, BeltId = a.EntryId });
                        break;
                    }
                    case "byname": Send<BeltEditReq, BeltsMsg>(new BeltEditReq { Op = 8, BeltId = a.EntryId, Direction = _beltDirection, Text = _beltText.Value }, DrawBelts); break;
                    case "byname-confirm": EditBelt(new BeltEditReq { Op = 3, BeltId = a.EntryId, Direction = _beltsState?.PreviewDirection ?? _beltDirection, Text = _beltsState?.PreviewText ?? _beltText.Value }); break;
                    case "locate":
                    {
                        var b = _beltsState?.Belts.FirstOrDefault(x => x.Id == a.EntryId);
                        if (b == null) break;
                        int pinged = 0;
                        foreach (var m in b.Members.Where(m => m.PickType != 0))
                            if (_ctx.Hud.TryFindByNetworkId(m.PickIndex, m.PickGeneration, m.PickType, out var entity, out _)) { _ctx.Hud.Locate(entity, m.Name, 10); pinged++; }
                        _ctx.Hud.Notify(pinged == 0 ? "None of its chests is near enough to ping." : $"Pinged {pinged} chest{(pinged == 1 ? "" : "s")}.", kind: UiNoticeKind.Info, seconds: 4);
                        break;
                    }
                    case "pick": PickBeltMembers(a.EntryId); break;
                }
                return;
            }
            switch (a.ActionId)
            {
                case UiDetailAction.New: EditBelt(new BeltEditReq { Op = 10, Name = _catName.Value }); break;
                case "additem": EditBelt(new BeltEditReq { Op = 11, Name = a.EntryId, Text = ClientItems.Resolve(_ctx, _catItem.Value) }); break;
                case "rename": EditBelt(new BeltEditReq { Op = 14, Name = a.EntryId, Text = _catName.Value }); break;
                case "duplicate": EditBelt(new BeltEditReq { Op = 15, Name = a.EntryId, Text = _catName.Value }); break;
                case "inventory":
                    _ = Panel;
                    _invSearch.Value = a.EntryId;
                    OpenTab(InventoryTab);
                    break;
                case "restore": EditBelt(new BeltEditReq { Op = 13, Name = a.EntryId }); break;
            }
        }

        /// <summary>Pick in world, repeated: every container or station interacted with joins the belt until Esc.</summary>
        void PickBeltMembers(string beltId)
        {
            var panel = Panel;
            UiApi.Hide(panel);
            var added = 0;
            var direction = _beltDirection;
            _ctx.Hud.PickInWorld(result =>
            {
                if (!result.IsCurrent || !result.HasNetworkId)
                {
                    _ctx.Hud.Notify("That was not a container or station Satisvampory can use.", kind: UiNoticeKind.Warning, seconds: 4);
                    return;
                }
                Send<BeltEditReq, BeltsMsg>(new BeltEditReq { Op = 7, BeltId = beltId, Direction = direction, PickIndex = result.NetworkIdIndex, PickGeneration = result.NetworkIdGeneration, PickType = result.NetworkId2 }, m =>
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
                Note = $"{b.Suppliers.Count} suppliers / {b.Receivers.Count} receivers" + (b.Plots.Count > 0 ? " · " + string.Join(", ", b.Plots.Take(3)) + (b.Plots.Count > 3 ? "…" : "") : ""),
                Keywords = b.Members.Select(x => x.Name).Concat(b.Plots).ToList(),
                Badge = b.UsedBy.Count > 0 ? b.UsedBy.Count.ToString() : "",
                Tooltip = b.UsedBy.Count == 0 ? "Not used anywhere yet." : "Used by " + b.UsedBy.Count,
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
            if (_pendingCategory is { } want && _beltsState != null)
            {
                _pendingCategory = null;
                if (_beltsMd.Tabs != null) _beltsMd.Tabs.Selected = CategoriesPage;
                var match = _beltsState.Categories.FirstOrDefault(c => string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase));
                if (match != null) _beltsMd.Select(match.Name);
            }
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
                        new UiDetailButton("rename", "Rename") { Enabled = edit && !b.BuiltIn },
                        new UiDetailButton("duplicate", "Duplicate") { Enabled = edit && !b.BuiltIn },
                        new UiDetailButton("delete", "Delete") { Enabled = edit && !b.BuiltIn }),
                    UiDetailLine.Actions(
                        new UiDetailButton("suppliers", _beltDirection == 0 ? "Adding suppliers (Output)" : "Add suppliers (Output)"),
                        new UiDetailButton("receivers", _beltDirection == 1 ? "Adding receivers (Input)" : "Add receivers (Input)")),
                    UiDetailLine.Actions(
                        new UiDetailButton("pick", "Pick in world") { Enabled = edit },
                        new UiDetailButton("byname", "Add by name...") { Enabled = edit },
                        new UiDetailButton("locate", "Locate all")),
                };
                lines.Add(UiDetailLine.Heading($"Suppliers (Output): {b.Suppliers.Count} / Receivers (Input): {b.Receivers.Count}"));
                // Master-detail supports one member list. Keep each direction's original index
                // in the ID so removal and ordering never use the combined display index.
                var connections = b.Suppliers.Select((x, i) => new UiListItem($"0:{i}:{x.ContainerId}", "Output - " + x.Name)
                    { Note = x.Where + (x.Status.Length > 0 ? " - " + x.Status : ""), Removable = edit })
                    .Concat(b.Receivers.Select((x, i) => new UiListItem($"1:{i}:{x.ContainerId}", "Input - " + x.Name)
                    { Note = x.Where + (x.Status.Length > 0 ? " - " + x.Status : ""), Removable = edit }));
                lines.Add(UiDetailLine.MemberList(connections, reorder: false, remove: edit,
                    emptyText: "No connections. Add suppliers (Output) or receivers (Input)."));
                foreach (var direction in new[] { 0, 1 })
                {
                    var members = direction == 0 ? b.Suppliers : b.Receivers;
                    var prefix = direction == 0 ? "Output - " : "Input - ";
                    if (edit)
                        for (var i = 0; i < members.Count; i++)
                        {
                            var member = members[i];
                            lines.Add(UiDetailLine.Actions(
                                new UiDetailButton($"move:{direction}:{i}:{i - 1}:{member.ContainerId}", prefix + member.Name + " up") { Enabled = i > 0 },
                                new UiDetailButton($"move:{direction}:{i}:{i + 1}:{member.ContainerId}", prefix + member.Name + " down") { Enabled = i + 1 < members.Count }));
                        }
                }
                if (m.PreviewFor == b.Id)
                {
                    lines.Add(UiDetailLine.Heading(m.PreviewDirection == 0 ? "Add suppliers by name" : "Add receivers by name", m.Preview.Count));
                    foreach (var preview in m.Preview) lines.Add(UiDetailLine.Line(preview, LabelTone.Normal));
                    if (m.Preview.Count > 0) lines.Add(UiDetailLine.Actions(new UiDetailButton("byname-confirm", "Add these") { Enabled = edit }));
                }
                lines.Add(UiDetailLine.Heading("Used by", b.UsedBy.Count));
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
                    UiDetailLine.Title(c.Name,
                        new UiDetailButton("restore", c.Kind == "Custom" ? "Delete" : "Restore built-in") { Enabled = edit && c.Kind != "Built-in" },
                        new UiDetailButton("rename", "Rename") { Enabled = edit && c.Kind == "Custom" },
                        new UiDetailButton("duplicate", "Duplicate") { Enabled = edit }),
                    UiDetailLine.Line(c.Kind == "Built-in" ? "Built-in. Adding or removing an item makes an island copy." : c.Kind == "Edited" ? "A built-in category, changed on this island." : "Made on this island."),
                    UiDetailLine.Heading("Items", c.Members.Count, "Category"),
                    UiDetailLine.MemberList(c.Members.Select((x, i) => new UiListItem(i < c.Items.Count ? c.Items[i].ToString() : x, x)
                        {
                            Note = i < c.Stock.Count ? $"{c.Stock[i]:N0} on the island" : "",
                            Removable = edit,
                        }),
                        reorder: false, remove: edit, emptyText: "No items yet."),
                    UiDetailLine.Actions(
                        new UiDetailButton("additem", "+ Add item") { Enabled = edit },
                        new UiDetailButton("inventory", "Show in inventory")),
                    UiDetailLine.Heading("Used by", c.UsedBy.Count, "Category"),
                };
                if (c.UsedBy.Count == 0) lines.Add(UiDetailLine.Line("Nothing names this category yet."));
                lines.AddRange(c.UsedBy.Take(12).Select(u => UiDetailLine.Line(u, LabelTone.Normal)));
                if (c.Members.Count >= 80) lines.Add(UiDetailLine.Line("Showing the first 80."));
                _beltsMd.SetDetail(CategoriesPage, lines);
            }
        }

        // ---------------- Inventory ----------------

        /// <summary>Columns shown by default; the rest are one click away in the column chooser (the panel is 640 px).</summary>
        static readonly HashSet<string> DefaultInventoryColumns = new() { "item", "total", "reserve", "cap", "status" };

        static UiTableColumn Num(string key, string caption, string term = "", bool editable = false) =>
            new(key, caption, UiTableColumnKind.Number)
            {
                Term = term, Editable = editable, Format = "#,0", Hideable = key != "total", Width = 72,
                Hidden = !DefaultInventoryColumns.Contains(key),
            };

        void BuildInventory(IUiPanel p)
        {
            _invSearch = _tabs.Add(InventoryTab, p.Input("Search", ""));
            _invShort = _tabs.Add(InventoryTab, p.Toggle("Only short", false));
            _invAtCap = _tabs.Add(InventoryTab, p.Toggle("Only at cap", false));
            _invMine = _tabs.Add(InventoryTab, p.Toggle("Only with my overrides", false));
            _invHere = _tabs.Add(InventoryTab, p.Toggle("Only here", false));
            _invPlot = _tabs.Add(InventoryTab, p.Input("Plot (blank: all plots)", ""));
            _invGroup = _tabs.Add(InventoryTab, p.Toggle("Group by category", false));
            Track(_invSearch.OnChanged(_ => DrawInventory()));
            foreach (var t in new[] { _invShort, _invAtCap, _invMine, _invHere }) Track(t.OnChanged(_ => DrawInventory()));
            Track(_invPlot.OnChanged(_ => DrawInventory()));
            Track(_invGroup.OnChanged(on => { _invTable.Grouped = on; DrawInventory(); }));
            Track(_tabs.Add(InventoryTab, p.Button("Refresh")).OnClick(() => { StopLiveInventory(); LoadInventory(); }));
            _invIsland = _tabs.Add(InventoryTab, p.Label(""));
            _invCats = _tabs.Add(InventoryTab, p.Chips("inv.cats", Array.Empty<UiChip>(), new UiChipsOptions
            {
                Toggle = true,
                Remove = false,
                MaxLines = 2,
                EmptyText = "",
            }));
            Track(_invCats.Term("Category"));
            Track(_invCats.OnClick(_ => DrawInventory()));
            _invTable = _tabs.Add(InventoryTab, p.DataTable("inv", new[]
            {
                new UiTableColumn("item", "Item") { Hideable = false, Width = 210 },
                new UiTableColumn("category", "Category") { Term = "Category", Hidden = true },
                Num("total", "Total", "Island"),
                Num("here", "Here"),
                Num("chests", "Chests"),
                Num("stations", "In stations"),
                Num("reserve", "Reserve", "Reserve", editable: true),
                Num("spare", "Spare"),
                Num("cap", "Cap", "Cap", editable: true),
                Num("demand", "Demand"),
                new UiTableColumn("status", "Status") { Width = 100 },
            }, new UiDataTableOptions
            {
                VisibleRows = 14,
                TotalRow = false,
                MultiSelect = true,
                Sort = new[] { new UiTableSortKey("item") },
                EmptyText = "Nothing stored on this island.",
            }));
            Track(_invTable.OnEdit(OnInventoryEdit));
            foreach (var key in new[] { "total", "here", "chests", "stations", "demand" })
                if (_invTable.Columns.FirstOrDefault(c => c.Key == key) is { } col) col.Subtotal = UiTableSubtotal.Sum;

            // Row actions on the selected (or expanded) rows (spec 15).
            Track(_invTable.OnExpand((id, open) => { if (open) _invTable.SetSelected(new[] { id }); }));
            _invTakeAmount = _tabs.Add(InventoryTab, p.Input("Take amount (blank: one stack)", ""));
            Track(_tabs.Add(InventoryTab, p.Button("Take")).OnClick(() => ForSelected(1, item =>
            {
                int.TryParse(_invTakeAmount.Value, out var n);
                Send<PullReq, TextReply>(new PullReq { Text = item.ToString(), Amount = n }, r => _ctx.Hud.Notify(r.Text, kind: UiNoticeKind.Success, seconds: 5));
            })));
            Track(_tabs.Add(InventoryTab, p.Button("Locate")).OnClick(() => ForSelected(1, item =>
                Send<FindReq, FindReply>(new FindReq { Text = item.ToString() }, r => { _lastFind = r; _findTable.SetSelected(Array.Empty<string>()); Locate(); }))));
            Track(_tabs.Add(InventoryTab, p.Button("Where does it go?")).OnClick(() => ForSelected(1, item =>
            {
                _mdItem.Value = _invRows.TryGetValue(item, out var row) ? ClientItems.NameOf(_ctx, item, row.Name) : item.ToString();
                OpenTab(MassDepositTab);
            })));
            Track(_tabs.Add(InventoryTab, p.Button("Category…")).OnClick(() => ForSelected(1, item =>
            {
                var cat = _invRows.TryGetValue(item, out var row) ? Cats(row).FirstOrDefault() : null;
                if (cat == null) { _ctx.Hud.Notify("That item is in no category.", kind: UiNoticeKind.Info, seconds: 3); return; }
                _pendingCategory = cat;
                OpenTab(BeltsTab);
            })));
            _invBulkValue = _tabs.Add(InventoryTab, p.Number("Bulk value (−1 clears the overrides)", -1, -1, 1_000_000));
            Track(_tabs.Add(InventoryTab, p.Button("Set reserve on selected")).OnClick(() => Bulk(0)));
            Track(_tabs.Add(InventoryTab, p.Button("Set cap on selected")).OnClick(() => Bulk(1)));
            Track(_invTable.OnSortChanged(_ => SaveInventoryView()));
            Track(_invTable.OnColumnsChanged(_ => SaveInventoryView()));
            _tabs.Add(InventoryTab, p.Divider());
            _ruleTarget = _tabs.Add(InventoryTab, p.Input("Category reserve or cap: \"category <name>\" (items: edit the table)", ""));
            _ruleKind = _tabs.Add(InventoryTab, p.Dropdown("Set", new[] { "0", "1" }, "0", new[] { "Reserve", "Cap" }));
            _ruleValue = _tabs.Add(InventoryTab, p.Number("Value (−1 clears)", -1, -1, 1_000_000));
            Track(_tabs.Add(InventoryTab, p.Button("Apply")).OnClick(() =>
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
                    _ctx.Log.LogInfo($"Satisvampory: live inventory ended: {why} {text}");
                    _invSub = null;
                    // Off every plot: keep the last island, read-only, with its time (spec 15).
                    _invStale = _invRows.Count > 0;
                    _invCanEdit = false;
                    if (_invStale) DrawInventory();
                    else if (!string.IsNullOrEmpty(text)) _invIsland.Text = "Not live: " + text;
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
            _invSnapshotUtc = DateTime.UtcNow;
            _invStale = false;
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
                // Empty: the player never chose, keep the defaults. "-": they show every column.
                if (!string.IsNullOrEmpty(hidden))
                {
                    var hide = new HashSet<string>(hidden.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    foreach (var c in _invTable.Columns.Where(c => c.Hideable))
                        _invTable.SetColumnVisible(c.Key, !hide.Contains(c.Key));
                }
            }
            finally { _invApplyingView = false; }
        }

        void SaveInventoryView()
        {
            if (_invApplyingView) return;
            var sort = string.Join(",", _invTable.Sort.Select(k => k.Column + ":" + (k.Ascending ? "a" : "d")));
            var hidden = _invTable.HiddenColumns.Count == 0 ? "-" : string.Join(",", _invTable.HiddenColumns);
            _ctx.Requests.Send<SetViewReq, ViewSaved>(new SetViewReq { View = "inv", Sort = sort, Hidden = hidden }, _ => { });
        }

        IUiToggle _invAtCap = null!, _invMine = null!, _invHere = null!, _invGroup = null!;
        IUiInput _invPlot = null!, _invTakeAmount = null!;
        IUiNumber _invBulkValue = null!;
        string? _pendingCategory;
        DateTime _invSnapshotUtc;
        bool _invStale;

        /// <summary>Runs an action for the selected rows (at most <paramref name="max"/>); tells the player when none is.</summary>
        void ForSelected(int max, Action<int> act)
        {
            var ids = _invTable.Selected.Select(x => int.TryParse(x, out var g) ? g : 0).Where(g => g != 0).Take(max).ToList();
            if (ids.Count == 0) { _ctx.Hud.Notify("Select a row first.", kind: UiNoticeKind.Info, seconds: 3); return; }
            foreach (var id in ids) act(id);
        }

        /// <summary>Bulk Set reserve / Set cap on the selected rows: one request per row, the last reply redraws.</summary>
        void Bulk(int rule)
        {
            if (!_invCanEdit) { _ctx.Hud.Notify("Only clan members can change reserves and caps.", kind: UiNoticeKind.Warning, seconds: 4); return; }
            var value = (int)Math.Round(_invBulkValue.Value);
            var ids = _invTable.Selected.ToList();
            if (ids.Count == 0) { _ctx.Hud.Notify("Select rows first (Ctrl- or Shift-click).", kind: UiNoticeKind.Info, seconds: 3); return; }
            var left = ids.Count;
            foreach (var id in ids)
                _ctx.Requests.Send<SetRuleReq, InventoryMsg>(new SetRuleReq { Target = id, Rule = rule, Value = value }, r =>
                {
                    if (--left == 0 && r.Ok && r.Reply != null) ApplySnapshot(r.Reply);
                    if (!r.Ok) _ctx.Hud.Notify(r.Reason.Length > 0 ? r.Reason : "Not saved: " + r.Status, kind: UiNoticeKind.Warning, seconds: 4);
                });
            _ctx.Hud.Notify($"{(rule == 0 ? "Reserve" : "Cap")} {(value < 0 ? "cleared" : "set to " + value)} on {ids.Count} item{(ids.Count == 1 ? "" : "s")}.", kind: UiNoticeKind.Success, seconds: 3);
        }

        static IEnumerable<string> Cats(InventoryRowMsg r) => r.Categories.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0);

        void DrawInventory()
        {
            static string From(int f) => f == 1 ? "category" : "default";
            var all = _invRows.Values.ToList();
            _invIsland.Text = _invIslandText + (all.Count >= 2000 ? " · first 2,000 items." : "")
                + (_invStale ? $" · read-only snapshot from {_invSnapshotUtc.ToLocalTime():HH:mm}" : _invSub is { State: SubscriptionState.Live } ? " · live" : "");

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
            if (_invAtCap.Value) rows = rows.Where(r => r.Status == "At cap");
            if (_invMine.Value) rows = rows.Where(r => r.ReserveFrom == 2 || r.CapFrom == 2);
            if (_invHere.Value) rows = rows.Where(r => r.Here > 0);
            var plotFilter = NameKey(_invPlot.Value);
            if (plotFilter.Length > 0) rows = rows.Where(r => r.PlotLabels.Any(l => NameKey(l).Contains(plotFilter)));
            if (search.Length > 0) rows = rows.Where(r => NameKey(ClientItems.NameOf(_ctx, r.Item, r.Name)).Contains(search) || NameKey(r.Categories).Contains(search));

            _invTable.SetRows(rows.Select(r =>
            {
                var cats = Cats(r).ToList();
                var row = new UiTableRow(r.Item.ToString())
                {
                    ItemPrefab = r.Item,
                    Groups = cats,
                    Detail = string.Join("\n", r.Plots.Take(4).DefaultIfEmpty("Not in any chest on the island.")),
                };
                row.Text("category", string.Join(", ", cats));
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
                row.Locked = !_invCanEdit || _invStale;
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

        IUiDataTable _findMatches = null!;
        IUiDataTable _findTable = null!;
        bool _findClan, _findPartial = true, _findScopeForced;
        IUiDropdown _findScope = null!;
        int _findKind;
        IUiLocatePing? _findPin;
        readonly List<EvalRowMsg> _findRows = new();
        int _findItem;
        int _findSequence;
        bool _findAtLocations;
        readonly List<IUiHandle> _findSearchControls = new();
        readonly List<IUiHandle> _findLocationControls = new();
        void RegisterFindControl(IUiHandle handle, bool location)
        {
            (location ? _findLocationControls : _findSearchControls).Add(handle);
            _tabs.Add(FindTab, handle);
        }
        // Concrete overloads avoid creating cross-context generic lifetime dependencies.
        IUiButton FindControl(IUiButton handle, bool location = false) { RegisterFindControl(handle, location); return handle; }
        IUiLabel FindControl(IUiLabel handle, bool location = false) { RegisterFindControl(handle, location); return handle; }
        IUiDropdown FindControl(IUiDropdown handle, bool location = false) { RegisterFindControl(handle, location); return handle; }
        IUiDataTable FindControl(IUiDataTable handle, bool location = false) { RegisterFindControl(handle, location); return handle; }
        IUiInput FindControl(IUiInput handle, bool location = false) { RegisterFindControl(handle, location); return handle; }
        void ShowFindStage(bool locations)
        {
            _findAtLocations = locations;
            foreach (var handle in _findSearchControls) handle.Visible = !locations && _tabs.Selected == FindTab;
            foreach (var handle in _findLocationControls) handle.Visible = locations && _tabs.Selected == FindTab;
        }
        IUiPanel? _previewPanel;
        IUiLabel _previewTitle = null!;
        IUiDataTable _previewContents = null!;
        Progenitor.Cameras.PluginCamera.CameraPreview? _preview;

        void BuildFind(IUiPanel p)
        {
            _findText = _tabs.Add(FindTab, p.SearchInput("Search items, chests or stations", ""));
            Track(_findText.OnChanged(_ => QueueFind()));
            Track(_findText.ActionButton("Find", _ => RunFind()));
            p.BeginInline();
            // The player's choices are kept in the mod's config, so they survive a hot reload and a restart.
            var savedScope = _ctx.Bind("Find", "Scope", "here", "Find: search this plot (here) or all clan plots (clan).");
            var savedMatch = _ctx.Bind("Find", "Match", "partial", "Find: partial or exact name match.");
            var savedShow = _ctx.Bind("Find", "Show", "0", "Find: 0 all results, 1 containers only, 2 items only.");
            if (savedScope.Value is not ("here" or "clan")) savedScope.Value = "here";
            if (savedMatch.Value is not ("partial" or "exact")) savedMatch.Value = "partial";
            if (savedShow.Value is not ("0" or "1" or "2" or "3")) savedShow.Value = "0";
            _findClan = savedScope.Value == "clan";
            _findPartial = savedMatch.Value == "partial";
            int.TryParse(savedShow.Value, out _findKind);
            var scope = _findScope = FindControl(p.CompactDropdown("Scope", new[] { "here", "clan" }, savedScope.Value, new[] { "This plot", "All clan plots" }));
            Track(scope.OnChanged(value =>
            {
                _findClan = value == "clan";
                if (!_findScopeForced) { savedScope.Value = value; Satisvampory.Config.PlayerConfig.Persist(savedScope); }
                RunFind();
            }));
            var matching = FindControl(p.CompactDropdown("Match", new[] { "partial", "exact" }, savedMatch.Value, new[] { "Partial name", "Full name" }));
            Track(matching.OnChanged(value => { _findPartial = value == "partial"; savedMatch.Value = value; Satisvampory.Config.PlayerConfig.Persist(savedMatch); RunFind(); }));
            var kind = FindControl(p.CompactDropdown("Show", new[] { "0", "1", "2", "3" }, savedShow.Value, new[] { "All results", "Containers only", "Items only", "Recipes only" }));
            Track(kind.OnChanged(value => { int.TryParse(value, out _findKind); savedShow.Value = value; Satisvampory.Config.PlayerConfig.Persist(savedShow); RunFind(); }));
            p.EndInline();
            _findResult = FindControl(p.Label("Search, choose a match, then choose its location."));
            _findMatches = FindControl(p.DataTable("find.matches", new[]
            {
                new UiTableColumn("name", "Matches — best first") { Hideable = false },
                new UiTableColumn("kind", "Type") { Width = 210 },
                new UiTableColumn("amount", "Stored", UiTableColumnKind.Number) { Width = 130 },
            }, new UiDataTableOptions { VisibleRows = 10, FillHeight = true, ColumnChooser = false, MultiSelect = false, TotalRow = false, EmptyText = "Enter a search above." }));
            Track(_findMatches.OnSelectionChanged(ids => SelectFindMatch(ids.FirstOrDefault())));
            Track(FindControl(p.Button("Back to matches"), true).OnClick(() => { _findMatches.SetSelected(Array.Empty<string>()); ShowFindStage(false); }));
            // Back where Find was opened from (a Log row), when it was.
            _findBack = _tabs.Add(FindTab, p.Button("Back"));
            Track(_findBack.OnClick(() =>
            {
                var tab = _findBackTab;
                _findBackTab = -1;
                ShowFindBack();
                if (tab >= 0) Open(tab);
            }));
            ShowFindBack();
            _findTable = FindControl(p.DataTable("find", new[]
            {
                new UiTableColumn("chest", "Chest or station") { Hideable = false },
                new UiTableColumn("type", "Type") { Width = 190 },
                new UiTableColumn("plot", "Plot") { Width = 260 },
                new UiTableColumn("count", "Stored", UiTableColumnKind.Number) { Format = "#,0", Width = 130 },
                new UiTableColumn("holds", "Details") { Width = 380 },
            }, new UiDataTableOptions { VisibleRows = 10, FillHeight = true, ColumnChooser = false, MultiSelect = false, TotalRow = false, EmptyText = "No locations in the selected plots." }), true);
            Track(_findTable.OnSelectionChanged(_ =>
            {
                UpdateFindActions();
                if (_player.AutoPinFind && SelectedFindLocation()?.HasPosition == true) Locate();
            }));
            p.BeginInline();
            _findView = FindControl(p.Button("View Object"), true);
            Track(_findView.OnClick(PreviewFind));
            _findPinButton = FindControl(p.Button("Pin location"), true);
            Track(_findPinButton.OnClick(Locate));
            p.EndInline();
            p.BeginInline();
            _takeWhat = FindControl(p.Label(""), true);
            _pullAmount = FindControl(p.Input("Amount (blank: one stack)", ""), true);
            _findTake = FindControl(p.Button("Take"), true);
            UpdateFindActions();
            Track(_findTake.OnClick(() =>
            {
                if (_findItem == 0) { _ctx.Hud.Notify("Choose an item first."); return; }
                int.TryParse(_pullAmount.Value, out var n);
                Send<PullReq, TextReply>(new PullReq { Text = _findItem.ToString(), Amount = n }, r => _ctx.Hud.Notify(r.Text, kind: UiNoticeKind.Success, seconds: 5));
            }));
            p.EndInline();
        }

        void QueueFind()
        {
            var sequence = ++_findSequence;
            Progenitor.Dev.MainThread.PostDelayed(0.25, () => { if (sequence == _findSequence) RunFind(suggestions: true); }, _ctx.Owner);
        }

        void RunFind() => RunFind(false);

        void RunFind(bool suggestions)
        {
            var sequence = ++_findSequence;
            var text = (_findText.Value ?? "").Trim();
            if (text.Length == 0) { DrawFind(new FindReply()); return; }
            Send<FindReq, FindReply>(new FindReq
            {
                Text = text, IncludeClanPlots = _findClan, PartialMatch = _findPartial, Kind = _findKind,
                ClientItems = _findKind == 1 ? new List<int>() : ClientItems.Matching(_ctx, text, _findPartial),
            }, r =>
            {
                if (sequence == _findSequence) DrawFind(r, suggestions);
                if (sequence == _findSequence && _findChest is { } wanted)
                {
                    _findChest = null;
                    var index = r.ChestsByName.FindIndex(c => c.ContainerId == wanted);
                    if (index >= 0) { _findMatches.SetSelected(new[] { "place:" + index }); SelectFindMatch("place:" + index); }
                }
            });
        }

        EvalRowMsg? SelectedFindLocation()
        {
            var id = _findTable.Selected.FirstOrDefault();
            return int.TryParse(id, out var index) && index >= 0 && index < _findRows.Count ? _findRows[index] : null;
        }

        /// <summary>
        /// A destination chest in a deposit or hunt line: open Find on that chest (its contents, preview and pin) and
        /// mark it on the map. The payload is the server's "id;x,y,z"; anything malformed is ignored.
        /// </summary>
        bool OnChestLink(Progenitor.Chat.ChatLinkClick click)
        {
            if (!click.IsFromServer || click.LinkType != "chest") return false;
            var halves = click.Payload.Split(';');
            if (halves.Length != 2 || !Guid.TryParseExact(halves[0], "N", out var chest)) return false;
            var parts = halves[1].Split(',');
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (parts.Length != 3
                || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, c, out var x)
                || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, c, out var y)
                || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, c, out var z)
                || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
                || Math.Abs(x) > 10000 || Math.Abs(y) > 1000 || Math.Abs(z) > 10000) return false;
            var name = click.Text.Length > 80 ? click.Text.Substring(0, 80) : click.Text;
            // The game's own map marker gives the route and compass guide.
            Progenitor.Ui.WorldMap.SetMarker(x, z);
            var at = name.LastIndexOf(" (", StringComparison.Ordinal);
            FindContainer(chest.ToString("N"), at > 0 ? name.Substring(0, at) : name);
            return true;
        }

        /// <summary>Find on one chest or station: searched by name over the clan's plots, its location chosen.</summary>
        internal void FindContainer(string containerId, string chestName) => FindContainer(containerId, chestName, -1);

        /// <summary>The hub tab Find was opened from (the Log), for its Back button; -1 for none.</summary>
        int _findBackTab = -1;
        IUiButton _findBack = null!;

        void FindContainer(string containerId, string chestName, int backTab)
        {
            _findChest = containerId;
            _findBackTab = backTab;
            OpenTab(FindTab);
            ShowFindBack();
            Progenitor.Dev.MainThread.PostDelayed(0.15, () =>
            {
                if (_panel == null || !_panel.Visible) return;
                _findText.Value = chestName;
                _findClan = true; // the chest may be on another plot of the island
                _findScopeForced = true; // for this search only: the player's saved scope stays
                try { _findScope.Value = "clan"; }
                finally { _findScopeForced = false; }
                RunFind();
            }, _ctx.Owner);
        }

        /// <summary>A chest a chat link asked Find for: chosen as soon as the search answers.</summary>
        string? _findChest;

        /// <summary>An item link anywhere in chat: show where that item is stored (Find).</summary>
        bool OnItemLink(Progenitor.Chat.ChatLinkClick click)
        {
            if (click.Kind != Progenitor.Chat.ChatLinkKind.Item || click.ItemPrefab == 0) return false;
            var name = ClientItems.NameOf(_ctx, click.ItemPrefab);
            if (string.IsNullOrEmpty(name)) return false;
            OpenTab(FindTab);
            Progenitor.Dev.MainThread.PostDelayed(0.15, () =>
            {
                if (_panel == null || !_panel.Visible) return;
                _findText.Value = name;
                RunFind();
            }, _ctx.Owner);
            return true;
        }

        void Locate()
        {
            var selected = SelectedFindLocation();
            if (selected == null && _findRows.Count == 1) selected = _findRows[0];
            if (selected == null || !selected.HasPosition) { _ctx.Hud.Notify("Choose one location to pin."); return; }
            _findPin?.Dispose();
            _findPin = _ctx.Hud.Locate(new Unity.Mathematics.float3(selected.X, selected.Y, selected.Z),
                selected.Name + " · " + selected.Where, 60, arrivalDistance: 3, showOnMaps: true);
            _ctx.Hud.Notify("Pinned " + selected.Name + ".");
        }

        void DrawFind(FindReply r, bool suggestions = false)
        {
            _ = Panel;
            _findMatches.SetSelected(Array.Empty<string>());
            _findTable.SetSelected(Array.Empty<string>());
            _lastFind = r; _findItem = 0; _findRows.Clear();
            ShowFindStage(false);
            _findTable.SetRows(Array.Empty<UiTableRow>());
            UpdateFindActions();
            var matches = new List<(int Rank, long Stock, string Name, UiTableRow Row)>();
            foreach (var item in r.Items)
            {
                var name = ClientItems.NameOf(_ctx, item.Item, item.Name);
                matches.Add((item.MatchRank, item.Total, name, new UiTableRow("item:" + item.Item) { ItemPrefab = item.Item }.Text("name", name).Text("kind", item.IsRecipe ? "Recipe" : "Item")
                    .Number("amount", item.IsRecipe ? null : item.Total, item.IsRecipe ? "" : null)));
            }
            for (int i = 0; i < r.ChestsByName.Count; i++)
            {
                var c = r.ChestsByName[i];
                matches.Add((Engine.Ui.Find.MatchRank(c.Name, _findText.Value ?? ""), c.Amount, c.Name,
                    new UiTableRow("place:" + i) { BuildingPrefab = c.BuildingPrefab }.Text("name", c.Name).Text("kind", string.IsNullOrEmpty(c.Type) ? "Chest / station" : c.Type).Number("amount", c.Amount)));
            }
            _findMatches.SetRows(matches.OrderBy(x => x.Rank).ThenByDescending(x => x.Stock > 0).ThenBy(x => x.Name).Take(suggestions ? 6 : 65).Select(x => x.Row));
            _findResult.Text = matches.Count == 0 ? "No matches in the selected plots." : suggestions ? "Suggestions: choose one, or press Enter to show all matches." : "Choose a match, then a chest or station.";
        }

        void SelectFindMatch(string? id)
        {
            if (id != null) ++_findSequence; // A chosen match wins over a late suggestion response.
            _findTable.SetSelected(Array.Empty<string>());
            _findRows.Clear(); _findItem = 0;
            if (_lastFind != null && id != null)
            {
                if (id.StartsWith("item:") && int.TryParse(id.Substring(5), out var item))
                {
                    _findItem = item;
                    _findRows.AddRange(_lastFind.Items.FirstOrDefault(x => x.Item == item)?.Chests ?? new List<EvalRowMsg>());
                }
                else if (id.StartsWith("place:") && int.TryParse(id.Substring(6), out var index) && index >= 0 && index < _lastFind.ChestsByName.Count)
                    _findRows.Add(_lastFind.ChestsByName[index]);
            }
            if (id != null) ShowFindStage(true);
            _findTable.SetRows(_findRows.Select((c, i) => new UiTableRow(i.ToString()) { BuildingPrefab = c.BuildingPrefab }
                .Text("chest", c.Name).Text("type", c.Type).Text("plot", c.Where).Number("count", c.Amount)
                .Text("holds", c.Why + (string.IsNullOrEmpty(c.Holds) ? "" : " · " + c.Holds))));
            // A single result is unambiguous; multiple locations wait for the player's choice.
            if (id != null && _findRows.Count == 1) _findTable.SetSelected(new[] { "0" });
            UpdateFindActions();
        }

        IUiButton _findView = null!, _findPinButton = null!, _findTake = null!;
        IUiLabel _takeWhat = null!;

        /// <summary>The location actions stay grayed until a chest or station is chosen (Take also needs an item).</summary>
        void UpdateFindActions()
        {
            if (_findView == null) return;
            var chosen = SelectedFindLocation();
            _findView.Interactable = chosen?.HasPosition == true;
            _findPinButton.Interactable = chosen?.HasPosition == true;
            // A recipe (Feed Prisoner) is made at a station, never stored: nothing to take.
            var recipe = _lastFind?.Items.FirstOrDefault(i => i.Item == _findItem)?.IsRecipe == true;
            _findTake.Interactable = chosen != null && _findItem != 0 && !recipe;
            _pullAmount.Interactable = chosen != null && _findItem != 0 && !recipe;
            _takeWhat.Text = recipe ? "A recipe: these are the stations that make it."
                : _findItem == 0
                ? "Choose an item and a location to take from storage."
                : $"Take {ClientItems.NameOf(_ctx, _findItem) ?? "this item"} from storage · one stack is {ClientItems.MaxStack(_ctx, _findItem):N0}";
        }

        int _previewSequence;
        string _previewToken = "";

        IUiLabel _previewHint = null!;
        IUiButton _previewKeep = null!;

        /// <summary>Keep the preview up until Return or Esc (the camera here, the room's streaming on the server).</summary>
        void KeepPreview()
        {
            if (_preview == null || !_preview.Active || _previewToken.Length == 0) return;
            if (!_preview.Hold()) return;
            Send<EndPreviewReq, ViewSaved>(new EndPreviewReq { Token = _previewToken, Hold = true }, _ => { });
            _previewHint.Text = "Right-drag to rotate. Kept open: Return or Escape closes it.";
            _previewKeep.Visible = false;
        }

        Progenitor.Inventory.IContainerPreview? _chestWindow;

        void TryOpenChestWindow(string containerId)
        {
            CloseChestWindow();
            if (!Guid.TryParseExact(containerId, "N", out var guid)) return;
            try { OpenChestWindow(guid); }
            catch (Exception e) { _ctx.Log.LogInfo("Satisvampory: chest window unavailable (Progenitor too old?): " + e.Message); }
        }

        // Its own method: on a Core without the API only this call fails (and is caught).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void OpenChestWindow(Guid guid)
        {
            var window = _chestWindow = _ctx.Inventory.Preview.Open(new Progenitor.Inventory.ContainerId(guid),
                new Progenitor.Inventory.ContainerPreviewOptions { KeepOpen = true });
            window.Closed += closed =>
            {
                if (_chestWindow != window) return;
                _chestWindow = null;
                // The player closed the chest window: the whole preview ends. Anything else: the list is back.
                if (closed.Reason is Progenitor.Inventory.ContainerPreviewCloseReason.Escape or Progenitor.Inventory.ContainerPreviewCloseReason.WindowClosed)
                {
                    ++_previewSequence; _preview?.Dispose(); EndFindPreview();
                }
                else if (_previewPanel != null && _previewPanel.Visible) { _previewContents.Visible = true; DockPreview(false); }
            };
            WatchChestWindow(window);
        }

        void WatchChestWindow(Progenitor.Inventory.IContainerPreview window)
        {
            if (_chestWindow != window) return;
            if (window.State == Progenitor.Inventory.ContainerPreviewState.Open)
            {
                DockPreview(true);
                // The game's window shows the contents beside the view (Progenitor 2026.10.93+); hold the view
                // until the player closes it.
                _previewContents.Visible = false;
                KeepPreview();
                return;
            }
            Progenitor.Dev.MainThread.PostDelayed(0.2, () => WatchChestWindow(window), _ctx.Owner);
        }

        void CloseChestWindow()
        {
            var w = _chestWindow;
            _chestWindow = null;
            w?.Dispose();
        }

        void EndFindPreview()
        {
            CloseChestWindow();
            var token = _previewToken; _previewToken = "";
            if (token.Length > 0) Send<EndPreviewReq, ViewSaved>(new EndPreviewReq { Token = token }, _ => { });
            if (_previewPanel != null) UiApi.Hide(_previewPanel);
            var back = _previewReturn;
            _previewReturn = null;
            if (back != null) back();
            else UiApi.Show(Panel);
        }

        void PreviewFind()
        {
            var row = SelectedFindLocation();
            if (row == null || !row.HasPosition) return;
            BeginPreview(row.ContainerId, _findClan, null);
        }

        /// <summary>The Object preview for another window (the Add list): it hides nothing and calls back when closed.</summary>
        internal void PreviewContainer(string containerId, Action returnTo) => BeginPreview(containerId, true, returnTo);

        Action? _previewReturn;

        void BeginPreview(string containerId, bool clan, Action? returnTo)
        {
            _preview?.Dispose();
            var sequence = ++_previewSequence;
            _previewReturn = returnTo;
            Send<PreviewReq, PreviewReply>(new PreviewReq { ContainerId = containerId, IncludeClanPlots = clan }, reply =>
            {
                if (sequence != _previewSequence)
                {
                    Send<EndPreviewReq, ViewSaved>(new EndPreviewReq { Token = reply.Token }, _ => { });
                    return;
                }
                _previewToken = reply.Token;
                if (_previewPanel == null)
                {
                    _previewPanel = UiApi.Panel("Object preview", _ctx.Owner, "sv.find.preview");
                    _previewPanel.Footer = VersionFooter;
                    _previewPanel.GameWindow(640);
                    UiApi.TopPanel(_previewPanel);
                    // At the left edge at its own width; the object stays dead center, clear of it on any screen
                    // (a framing offset as a share of the width threw it far off center on an ultrawide).
                    _previewPanel.Dock(UiDock.Left);
                    _previewPanel.CaptureMode = UiCaptureMode.WhileShown;
                    _ctx.Track(_previewPanel);
                    _previewHint = _previewPanel.Label("");
                    _previewTitle = _previewPanel.Label("");
                    _previewContents = _previewPanel.DataTable("preview.contents", new[]
                    {
                        new UiTableColumn("item", "Holds") { Hideable = false },
                        new UiTableColumn("amount", "Amount", UiTableColumnKind.Number) { Format = "#,0", Width = 160 },
                    }, new UiDataTableOptions { VisibleRows = 8, Selectable = false, TotalRow = false, ColumnChooser = false, EmptyText = "Empty." });
                    _previewPanel.BeginInline();
                    _previewKeep = _previewPanel.Button("Keep open");
                    Track(_previewKeep.OnClick(KeepPreview));
                    Track(_previewPanel.Button("Return").KeyHint("Esc").OnClick(() => { ++_previewSequence; _preview?.Dispose(); EndFindPreview(); }));
                    _previewPanel.EndInline();
                }
                _previewTitle.Text = reply.Name;
                DockPreview(false); // a strip only once the game's chest window is open
                // A plain chest opens in the game's own window on the right; the list stays for stations and
                // whenever the game cannot (another game window open, an older Progenitor, PvP rules).
                _previewContents.Visible = true;
                // Not when another window called for the preview (the Add list on a chest): the game has one container
                // window, and opening this chest in it would close the one the player is working in.
                if (reply.Chest && _previewReturn == null) TryOpenChestWindow(containerId);
                _previewHint.Text = "Right-drag to rotate. Escape returns. Ends after 5 seconds unless you keep it open.";
                _previewKeep.Visible = true;
                _previewContents.SetRows(reply.Items.Select((item, i) => new UiTableRow(i.ToString()) { ItemPrefab = item }
                    .Text("item", ClientItems.NameOf(_ctx, item) ?? "Item " + item)
                    .Number("amount", i < reply.Amounts.Count ? reply.Amounts[i] : 0)));
                if (_previewReturn == null) UiApi.Hide(Panel);
                UiApi.Show(_previewPanel);
                Progenitor.Dev.MainThread.PostDelayed(0.75, () =>
                {
                    if (sequence != _previewSequence || !_previewPanel.Visible) { EndFindPreview(); return; }
                    try
                    {
                        var options = new Progenitor.Cameras.CameraPreviewOptions
                        {
                            Seconds = 5, Ended = _ => EndFindPreview(), CompressedHeight = reply.FloorHeight,
                        };
                        // The player's own top-down distance and pitch, the game's station framing (Progenitor #100).
                        try { MatchPlayerCamera(options); }
                        catch (Exception) { }
                        _preview = _ctx.Camera.Preview(new Progenitor.Units.UnitVec3(reply.X, reply.Y + 0.7f, reply.Z), reply.Yaw, options);
                        WatchPreview();
                    }
                    catch (Exception e)
                    {
                        EndFindPreview();
                        _ctx.Hud.Notify("Unable to preview this: " + e.Message);
                    }
                }, _ctx.Owner);
            });
        }

        [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void MatchPlayerCamera(Progenitor.Cameras.CameraPreviewOptions options) => options.MatchPlayerCamera = true;

        /// <summary>
        /// With the game's chest window open (inventory left, chest right), the preview is a small strip under the gap
        /// between them; otherwise it sits at the left edge with its contents list (Progenitor #104 docks).
        /// </summary>
        void DockPreview(bool strip)
        {
            if (_previewPanel == null) return;
            try { DockPreviewCore(_previewPanel, strip); }
            catch (Exception) { _previewPanel.Dock(UiDock.Left); }
        }

        [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void DockPreviewCore(IUiPanel panel, bool strip)
        {
            if (strip) panel.Dock(UiDock.Bottom, new UiDockOptions { Align = UiDockAlign.Center, Width = 420 });
            else panel.Dock(UiDock.Left);
        }

        void WatchPreview()
        {
            if (_preview == null) return;
            if (!_preview.Active) { if (_previewToken.Length > 0) EndFindPreview(); return; }
            if (_previewPanel == null || !_previewPanel.Visible) { _preview.Dispose(); return; }
            Progenitor.Dev.MainThread.PostDelayed(0.033, WatchPreview, _ctx.Owner);
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
