// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Satisvampory.Net;
using UiApi = Progenitor.Ui.Ui;

namespace Satisvampory.Client
{
    /// <summary>
    /// Native container tabs, mounted through Progenitor.
    /// Every edit redraws from the server's reply.
    /// </summary>
    internal sealed class Panels
    {
        readonly SatisContext _ctx;
        readonly Satisvampory.Config.PlayerConfig _player;
        VanillaPanelTarget _target;
        bool _hasTarget;
        int _openVersion;
        string _containerId = string.Empty;
        PanelMsg? _state;

        readonly ListView _input;
        readonly ListView _output;
        readonly DepositView _deposit;
        readonly PickerView _picker;
        readonly FilterPickerView _filterPicker;
        IVanillaPanelTabs? _nativeTabs;

        internal Panels(SatisContext ctx, Satisvampory.Config.PlayerConfig player)
        {
            _ctx = ctx;
            _player = player;
            _input = new ListView(this, 0);
            _output = new ListView(this, 1);
            _deposit = new DepositView(this);
            _picker = new PickerView(this);
            _filterPicker = new FilterPickerView(this);
        }

        internal SatisContext Ctx => _ctx;

        internal PanelMsg? State => _state;

        internal void Attach()
        {
            Tabs(VanillaUiSite.ContainerPanel, new[] { "Inventory", "Input", "Output", "Filter" });
            Tabs(VanillaUiSite.StationPanel, new[] { "Production", "Input", "Output" });
            // Unit stations (tomb, vermin nest, Nether Gate) have no tab row, so no Input/Output tabs:
            // they get edge buttons, shown only on those windows.
            Site(VanillaUiSite.StationPanel, VanillaPanelAnchor.LeftEdge, "Input", "Input: where this station's materials come from", () => Open(_input),
                kind => kind == VanillaPanelKind.UnitStation);
            Site(VanillaUiSite.StationPanel, VanillaPanelAnchor.RightEdge, "Output", "Output: where this station's products go", () => Open(_output),
                kind => kind == VanillaPanelKind.UnitStation);
            // A small Log button in the title row of every chest and station: the hub's Log on this one.
            LogButton(VanillaUiSite.ContainerPanel);
            LogButton(VanillaUiSite.StationPanel);
            Site(VanillaUiSite.CastleHeartPanel, VanillaPanelAnchor.LeftEdge, "Input", "Input: where this heart's blood comes from", () => Open(_input));
            Site(VanillaUiSite.CastleHeartPanel, VanillaPanelAnchor.RightEdge, "Output", "Output: only items you send out of the heart", () => Open(_output));
        }

        void Tabs(VanillaUiSite site, string[] captions)
        {
            var tabs = UiApi.AttachPanelTabs(site, captions, _ctx.Owner);
            _ctx.Track(tabs);
            tabs.ShowRedistribution = _player.ShowRedistribution;
            void Refresh() => tabs.ShowRedistribution = _player.ShowRedistribution;
            _player.Changed += Refresh;
            _ctx.Track(new OnDispose(() => _player.Changed -= Refresh));
            tabs.SelectionChanged += SelectTab;
            tabs.PanelChanged += OnPanelChanged;
            // The handle can outlive this plugin inside Progenitor; leaving these subscribed would keep
            // the unloaded plugin alive and block Core hot swaps.
            _ctx.Track(new OnDispose(() =>
            {
                tabs.SelectionChanged -= SelectTab;
                tabs.PanelChanged -= OnPanelChanged;
                if (_nativeTabs == tabs) _nativeTabs = null;
            }));

            void OnPanelChanged(VanillaPanelEvent e)
            {
                if (e.Change == VanillaPanelChange.Closed)
                {
                    if (_nativeTabs != tabs) return;
                    // A pick in the world closes the game window on purpose; the panel keeps its chest meanwhile.
                    if (HoldTarget) return;
                    _hasTarget = false;
                    _openVersion++;
                    HideAll();
                    _nativeTabs = null;
                    return;
                }
                _nativeTabs = tabs;
                _target = e.Target;
                _hasTarget = e.Target.HasNetworkId;
                _containerId = string.Empty;
                _state = null;
                _openVersion++;
                HideAll();
            }
        }

        internal void Mount(IUiPanel panel) => _nativeTabs?.Mount(panel);

        // Its own method: on a Core without SetTabEnabled only this call fails (and is caught).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void SetListTabs(IVanillaPanelTabs tabs, bool on)
        {
            const string why = "Private chest: Input and Output are off. Turn Private off on the Filter tab.";
            if (tabs.IsTabEnabled(1) == on && tabs.IsTabEnabled(2) == on) return;
            tabs.SetTabEnabled(1, on, on ? "" : why);
            tabs.SetTabEnabled(2, on, on ? "" : why);
        }

        const int ResearchScroll = 2065714452;

        void LogButton(VanillaUiSite site)
        {
            var handle = UiApi.AttachToVanilla(site, VanillaPanelAnchor.BesideRename, _ctx.Owner, _ctx.Guid + ".log");
            _ctx.Track(handle);
            // The game's Research Scroll icon (a parchment) as the face; the caption is its tooltip.
            var button = handle.Button("Log", VanillaButtonIcon.Item(ResearchScroll));
            _ctx.Track(button.Tooltip("What moved in and out of this, and who changed its settings (last 24 hours).", "Log"));
            _ctx.Track(button.OnClick(() =>
            {
                if (!_hasTarget) return;
                void Go() { if (_state != null) OpenLog?.Invoke(_state.ContainerId, _state.Name); }
                if (_state != null) Go();
                else Load(Go);
            }));
        }

        void Site(VanillaUiSite site, VanillaPanelAnchor anchor, string caption, string tip, Action onClick,
            Func<VanillaPanelKind, bool>? showFor = null)
        {
            var handle = UiApi.AttachToVanilla(site, anchor, _ctx.Owner, _ctx.Guid);
            if (showFor != null) handle.Visible = false;
            _ctx.Track(handle);
            var button = handle.Button(caption);
            _ctx.Track(button.OnClick(onClick));
            _ctx.Track(button.Tooltip(tip, caption));
            _ctx.Track(handle.OnPanel(e =>
            {
                if (e.Change == VanillaPanelChange.Closed)
                {
                    if (HoldTarget) return;
                    _hasTarget = false;
                    _openVersion++;
                    HideAll();
                    return;
                }
                if (showFor != null)
                {
                    handle.Visible = showFor(e.Target.Kind);
                    if (!handle.Visible) return;
                }
                if (!e.Target.HasNetworkId) return;
                var changed = !_hasTarget || e.Target.NetworkIdIndex != _target.NetworkIdIndex || e.Target.NetworkIdGeneration != _target.NetworkIdGeneration;
                _target = e.Target;
                _hasTarget = true;
                if (changed)
                {
                    _containerId = string.Empty;
                    _state = null;
                    HideAll();
                }
            }));
        }

        /// <summary>
        /// While a flow that closes the game window on purpose runs (Pick in world), the panels keep their chest:
        /// edits made meanwhile go to it and their answers are not thrown away.
        /// </summary>
        internal bool HoldTarget { get; set; }

        bool _noTabDisable;

        /// <summary>Opens the hub on a tab (set by the client half).</summary>
        internal Action<int>? OpenHub { get; set; }

        /// <summary>Open the hub's Log on one chest or station (id, name).</summary>
        internal Action<string, string>? OpenLog { get; set; }

        /// <summary>Open the hub's Find on one chest or station (id, name).</summary>
        internal Action<string, string>? FindContainer { get; set; }

        /// <summary>Show a container in the Object preview; the action runs when the preview closes.</summary>
        internal Action<string, Action>? PreviewContainer { get; set; }

        internal TargetMsg Target() => new()
        {
            NetIndex = _target.NetworkIdIndex,
            NetGeneration = _target.NetworkIdGeneration,
            NetType = _target.NetworkId2,
            ContainerId = _containerId,
        };

        void Open(View view)
        {
            if (!_hasTarget) return;
            HideAll();
            var version = ++_openVersion;
            Load(() => { if (_hasTarget && version == _openVersion) view.Show(); });
        }

        /// <summary>Ask the server for the panel and redraw every open view from the reply.</summary>
        internal void Load(Action? then = null)
        {
            var target = _target;
            var version = _openVersion;
            Send<GetPanelReq, PanelMsg>(new GetPanelReq { Target = Target() }, reply =>
            {
                if (!_hasTarget || !_target.Equals(target) || version != _openVersion) return;
                Apply(reply);
                then?.Invoke();
            });
        }

        internal void Apply(PanelMsg reply)
        {
            _state = reply;
            _containerId = reply.ContainerId;
            // A private chest's Input and Output do not run: grey their tabs (Progenitor #106; older Cores keep the notice).
            if (_nativeTabs != null && !_noTabDisable)
                try { SetListTabs(_nativeTabs, !reply.Deposit.Private); }
                catch (Exception e)
                {
                    _noTabDisable = true; // once per load
                    _ctx.Log.LogInfo("Satisvampory: tab disabling unavailable (Progenitor too old?): " + e.Message);
                }
            // Only open views: redrawing builds a view's panel, and a built panel shows.
            _input.RedrawIfOpen();
            _output.RedrawIfOpen();
            _deposit.RedrawIfOpen();
        }

        internal void Send<TReq, TReply>(TReq req, Action<TReply> ok) where TReq : class, new() where TReply : class, new()
        {
            var target = _target;
            var version = _openVersion;
            _ctx.Requests.Send<TReq, TReply>(req, r =>
            {
                if (r.Reply is PanelMsg && (!_hasTarget || !_target.Equals(target) || version != _openVersion)) return;
                if (r.Ok && r.Reply != null) ok(r.Reply);
                else _ctx.Hud.Notify(Reason(r.Status, r.Reason), kind: UiNoticeKind.Warning, seconds: 6);
            });
        }

        internal void Edit(EditListReq req) => Send<EditListReq, PanelMsg>(req, Apply);

        internal void OpenPicker(int side) => _picker.Show(side);

        internal void OpenFilterPicker(DepositView into) => _filterPicker.Show(into);

        internal void SelectTab(int tab)
        {
            if (tab == 0) { _openVersion++; HideAll(); return; } // The game's inventory/production remains authoritative.
            Open(tab == 1 ? _input : tab == 2 ? _output : _deposit);
        }

        static string Reason(RequestStatus status, string reason) => status switch
        {
            RequestStatus.Refused => string.IsNullOrEmpty(reason) ? "Refused." : reason,
            RequestStatus.Timeout => "The server did not answer.",
            RequestStatus.NotNegotiated => "This server does not run Satisvampory 2.0.",
            RequestStatus.RateLimited => "Too many requests; try again in a moment.",
            _ => "Could not reach Satisvampory on the server (" + status + ").",
        };

        void HideAll()
        {
            _input.Hide();
            _output.Hide();
            _deposit.Hide();
            _picker.Hide();
            _filterPicker.Hide();
        }
    }

    /// <summary>A Satisvampory panel shown beside a vanilla window.</summary>
    internal abstract class View
    {
        protected readonly Panels Owner;
        IUiPanel? _panel;

        int _nodeKind = -1;

        protected View(Panels owner) => Owner = owner;

        protected abstract string Title { get; }
        protected virtual int NavigationTab => -1;

        protected abstract void Build(IUiPanel panel);

        public abstract void Redraw();

        protected IUiPanel Panel
        {
            get
            {
                var kind = Owner.State?.NodeKind ?? -1;
                if (_panel != null && NavigationTab >= 0 && _nodeKind != kind)
                {
                    _panel.Dispose();
                    _panel = null;

                }
                if (_panel == null)
                {
                    _nodeKind = kind;
                    _panel = UiApi.Panel(Title, Owner.Ctx.Owner, Owner.Ctx.Guid).GameWindow();
                    _panel.Footer = Hub.VersionFooter;
                    _panel.CaptureMode = UiCaptureMode.WhenHovered;
                    Owner.Ctx.Track(_panel);
                    if (NavigationTab >= 0) Owner.Mount(_panel);
                    Build(_panel);
                }
                return _panel;
            }
        }

        /// <summary>The one-time tip card this panel shows the first time it opens, if any.</summary>
        protected virtual string? TipId => null;

        public void Show()
        {
            _open = true;
            Redraw();
            if (NavigationTab >= 0) Owner.Mount(Panel);
            UiApi.Show(Panel);
            if (TipId != null) Help.Tip(Owner.Ctx, TipId, Panel);
        }

        public void Hide()
        {
            _open = false;
            if (_panel != null) UiApi.Hide(_panel);
        }

        bool _open;

        public void RedrawIfOpen()
        {
            if (_open) Redraw();
        }
    }

    /// <summary>An Input (side 0) or Output (side 1) list.</summary>
    internal sealed class ListView : View
    {
        IUiLabel _others = null!;
        IUiDropdown _add = null!;

        protected override string? TipId => Help.ListsTip;

        readonly int _side;
        IUiLabel _header = null!;
        IUiDropdown _order = null!;
        IUiDataTable _list = null!;
        IUiLabel _loop = null!;
        IUiDropdown _loopMode = null!;
        bool _redrawing;

        public ListView(Panels owner, int side) : base(owner) => _side = side;

        protected override string Title => "Logistics";
        protected override int NavigationTab => _side + 1;

        static readonly string[] PullValues = { "0", "1" };
        static readonly string[] PullLabels = { "Respect list order", "Reverse Mass Deposit (worst-stored first)" };
        static readonly string[] SendValues = { "0", "1" };
        static readonly string[] SendLabels = { "Fill in order", "Spread evenly" };

        protected override void Build(IUiPanel panel)
        {
            var ctx = Owner.Ctx;
            _header = panel.Label("");
            // One Add menu: the chest/belt list, or clicking chests in the world.
            panel.BeginInline();
            _add = panel.CompactDropdown("Add", new[] { "", "list", "pick" }, "", new[] { "Choose…", "Chests or belts…", "Pick in world" });
            panel.EndInline();
            ctx.Track(_add.OnChanged(v =>
            {
                if (_redrawing || v.Length == 0) return;
                _redrawing = true;
                try { _add.Value = ""; }
                finally { _redrawing = false; }
                if (v == "list") Owner.OpenPicker(_side);
                else if (v == "pick") PickInWorld();
            }));

            // The connections as a table: a few blank rows even when empty, the row's own buttons at its end.
            _list = panel.DataTable("rows", new[]
            {
                new UiTableColumn("name", _side == 0 ? "Pulls from" : "Sends to") { Hideable = false },
                new UiTableColumn("details", "Details") { Width = 230 },
                new UiTableColumn("actions", "", UiTableColumnKind.Actions) { ActionSlots = 4 },
            }, new UiDataTableOptions
            {
                VisibleRows = 8, MinRows = 6, Selectable = false, ColumnChooser = false, TotalRow = false, Sort = Array.Empty<UiTableSortKey>(),
                EmptyText = _side == 0 ? "No sources yet: Add a chest or belt." : "No destinations yet: Add a chest, belt or Mass Deposit.",
            });
            ctx.Track(_list.OnRowAction((rowId, action) =>
            {
                var rows = Owner.State == null ? null : _side == 0 ? Owner.State.Input : Owner.State.Output;
                if (rows == null || !int.TryParse(rowId, out var i) || i < 0 || i >= rows.Count) return;
                switch (action)
                {
                    case "find":
                        if (rows[i].ContainerId.Length > 0) Owner.FindContainer?.Invoke(rows[i].ContainerId, rows[i].Name);
                        else if (rows[i].Kind == 1) Owner.OpenHub?.Invoke(Hub.BeltsTab);
                        break;
                    case "up":
                        if (i > 0 && rows[i].LinkOwner.Length == 0) Owner.Edit(new EditListReq { Target = Owner.Target(), Side = _side, Op = 2, Index = i, To = i - 1 });
                        break;
                    case "down":
                        if (i < rows.Count - 1 && rows[i + 1].LinkOwner.Length == 0 && rows[i].LinkOwner.Length == 0) Owner.Edit(new EditListReq { Target = Owner.Target(), Side = _side, Op = 2, Index = i, To = i + 1 });
                        break;
                    case "remove":
                        var r = rows[i];
                        Owner.Edit(r.LinkOwner.Length > 0
                            ? new EditListReq { Target = Owner.Target(), Side = r.LinkSide, Op = 7, Index = r.LinkIndex, LinkOwner = r.LinkOwner }
                            : new EditListReq { Target = Owner.Target(), Side = _side, Op = 1, Index = i });
                        break;
                }
            }));

            // How the list is worked through: a setting under it.
            _order = _side == 0
                ? panel.Dropdown("Pull order", PullValues, "0", PullLabels)
                : panel.Dropdown("Send order", SendValues, "0", SendLabels);
            ctx.Track(_order.OnChanged(v =>
            {
                if (_redrawing || Owner.State == null) return;
                var st = Owner.State;
                Owner.Edit(new EditListReq
                {
                    Target = Owner.Target(),
                    Side = _side,
                    Op = 3,
                    PullOrder = _side == 0 ? int.Parse(v) : st.PullOrder,
                    SendOrder = _side == 1 ? int.Parse(v) : st.SendOrder,
                });
            }));
            _others = panel.Label("");
            _loop = panel.Label("");
            _loopMode = panel.Dropdown("Loop mode", new[] { "0", "1", "2" }, "0", new[] { "Cycle once, then wait", "Even distribution", "Off (closing edge never runs)" });
            ctx.Track(_loopMode.OnChanged(v =>
            {
                if (_redrawing || Owner.State == null || Owner.State.LoopKey.Length == 0) return;
                Owner.Send<SetLoopReq, PanelMsg>(new SetLoopReq { Target = Owner.Target(), LoopKey = Owner.State.LoopKey, Mode = int.Parse(v) }, Owner.Apply);
            }));

        }

        public override void Redraw()
        {
            var s = Owner.State;
            if (s == null) return;
            _ = Panel;
            _redrawing = true;
            try
            {
                _header.Text = $"{s.Name} · {s.Where}" + (s.CanEdit ? "" : " · " + s.ReadOnlyReason) + (s.Notice.Length > 0 ? "\n" + s.Notice : "")
                    + (s.Deposit.Private ? "\nPrivate chest: Input and Output are off. Turn Private off on the Filter tab to use them." : "");
                _add.Visible = !s.Deposit.Private;
                _order.Value = (_side == 0 ? s.PullOrder : s.SendOrder).ToString();
                var rows = _side == 0 ? s.Input : s.Output;
                _list.SetRows(rows.Select((r, i) => new UiTableRow(i.ToString()) { BuildingPrefab = r.BuildingPrefab }
                    .Text("name", r.Auto ? r.Name + " (automatic)" : r.Name)
                    .Text("details", string.Join(" · ", new[] { r.Type, r.Where, r.Status }.Where(x => !string.IsNullOrEmpty(x))))
                    .Action("find", UiRowActionIcon.Locate,
                        r.ContainerId.Length > 0 ? "Find this chest or station" : r.Kind == 1 ? "Open this belt in Belts" : "Mass Deposit has no place to find",
                        r.ContainerId.Length > 0 || r.Kind == 1)
                    // A link set on the other end is ordered there: no arrows here.
                    .Action("up", UiRowActionIcon.Up, "Earlier in the list", s.CanEdit && r.LinkOwner.Length == 0 && i > 0)
                    .Action("down", UiRowActionIcon.Down, "Later in the list",
                        s.CanEdit && r.LinkOwner.Length == 0 && i < rows.Count - 1 && rows[i + 1].LinkOwner.Length == 0)
                    .Action("remove", UiRowActionIcon.Remove, "Remove", s.CanEdit)));
                var others = _side == 0 ? s.InputFromOthers : s.OutputFromOthers;
                _others.Text = others.Count == 0 ? "" : (_side == 0 ? "Also pulled in by other lists:\n  " : "Also taken by other lists:\n  ") + string.Join("\n  ", others.Take(12));
                _others.Visible = others.Count > 0;
                _loop.Text = s.LoopBanner;
                _loop.Visible = s.LoopBanner.Length > 0;
                _loopMode.Visible = s.LoopKey.Length > 0;
                _loopMode.Value = s.LoopMode.ToString();
            }
            finally
            {
                _redrawing = false;
            }
        }

        void PickInWorld()
        {
            var ctx = Owner.Ctx;
            Hide();
            Owner.HoldTarget = true;
            ctx.Hud.PickInWorld(result =>
            {
                if (!result.IsCurrent || !result.HasNetworkId)
                {
                    ctx.Hud.Notify("That was not a container or station Satisvampory can use.", kind: UiNoticeKind.Warning, seconds: 5);
                    return;
                }
                Owner.Send<EditListReq, PanelMsg>(new EditListReq
                {
                    Target = Owner.Target(),
                    Side = _side,
                    Op = 0,
                    Entry = new RowMsg { Kind = 0, PickIndex = result.NetworkIdIndex, PickGeneration = result.NetworkIdGeneration, PickType = result.NetworkId2 },
                }, reply =>
                {
                    Owner.Apply(reply);
                    var rows = _side == 0 ? reply.Input : reply.Output;
                    var added = rows.LastOrDefault();
                    var what = added == null ? "it" : $"{added.Name} · {added.Where}";
                    ctx.Hud.Notify($"Added {what} to {(_side == 0 ? "Input" : "Output")} of {reply.Name} · {reply.Where}.", kind: UiNoticeKind.Success, seconds: 4);
                });
            }, () =>
            {
                // Back to the list as it is now (what the picks added), still on the same chest.
                Owner.HoldTarget = false;
                Owner.Load(() => Show());
            }, new UiWorldPickOptions
            {
                CloseGameWindows = true, // the open container and character panels would hide the world
                // Pick several in one go; Esc ends it and brings the list back.
                Repeat = true,
                Prompt = _side == 0 ? "Interact with containers or stations to pull from. Esc when done." : "Interact with containers or stations to send to. Esc when done.",
            });
        }

    }

    /// <summary>A chest's deposit settings (spec 01).</summary>
    /// <summary>
    /// The Filter tab: what this chest takes from belts and automatic deposits. Each listed item or
    /// category is Allowed at a priority of 1 to 99 (higher wins with Mass Deposit) or Blocked. Private
    /// takes the chest out of every shared island use.
    /// </summary>
    internal sealed class DepositView : View
    {
        protected override string? TipId => Help.DepositTip;

        sealed class Entry
        {
            public int Item;
            public string Category = "";
            public string Label = "";
            public bool Blocked;
            public int Priority = Satisvampory.Engine.Model.DepositOptions.DefaultEntryPriority;
            public string Key => Item != 0 ? "i:" + Item : "c:" + Category;
        }

        IUiLabel _header = null!;
        IUiLabel _restriction = null!;
        IUiDropdown _takes = null!;
        IUiDataTable _entries = null!;
        IUiDropdown _batchRule = null!;
        IUiNumber _batchPriority = null!;
        IUiButton _batchApply = null!;
        IUiToggle _private = null!;
        IUiLabel _privateNote = null!;
        IUiButton _empty = null!;
        float _emptyArmedAt = -10;
        readonly List<Entry> _list = new();
        readonly HashSet<string> _selected = new();
        int _special;
        bool _dirty, _redrawing;
        string _draftFor = "";

        public DepositView(Panels owner) : base(owner)
        {
        }

        protected override string Title => "Filter";
        protected override int NavigationTab => 3;

        static readonly string[] TakesValues = { "0", "1", "2" };
        static readonly string[] TakesLabels = { "Anything not blocked", "Only allowed items", "Nothing" };

        internal IReadOnlyCollection<string> ListedKeys => _list.Select(e => e.Key).ToList();

        protected override void Build(IUiPanel panel)
        {
            var ctx = Owner.Ctx;
            _header = panel.Label("");
            panel.Label("What belts and automatic deposits may put here. Manual deposits are unaffected.");
            _restriction = panel.Label("");
            _takes = panel.Dropdown("Takes", TakesValues, "0", TakesLabels);
            ctx.Track(_takes.OnChanged(_ => { if (!_redrawing) _dirty = true; }));
            ctx.Track(panel.Button("+ Add items or categories…").OnClick(() => Owner.OpenFilterPicker(this)));

            _entries = panel.DataTable("filter.entries", new[]
            {
                new UiTableColumn("name", "Item or category") { Hideable = false },
                new UiTableColumn("type", "Type") { Width = 120 },
                new UiTableColumn("rule", "Rule") { Width = 110 },
                new UiTableColumn("priority", "Priority", UiTableColumnKind.Number) { Editable = true, Format = "0", Width = 100, Term = "Priority" },
                new UiTableColumn("actions", "", UiTableColumnKind.Actions) { ActionSlots = 2 },
            }, new UiDataTableOptions
            {
                VisibleRows = 8, MinRows = 6, Selectable = true, MultiSelect = true, ColumnChooser = false, TotalRow = false,
                Sort = new[] { new UiTableSortKey("priority", false) },
                EmptyText = "Nothing listed. Add items or categories.",
            });
            ctx.Track(_entries.OnSelectionChanged(ids =>
            {
                _selected.Clear();
                foreach (var id in ids) _selected.Add(id);
                _batchApply.Interactable = _selected.Count > 0 && (Owner.State?.CanEdit ?? false);
            }));
            ctx.Track(_entries.OnRowAction((id, action) =>
            {
                var e = _list.FirstOrDefault(x => x.Key == id);
                if (e == null) return;
                if (action == "rule") e.Blocked = !e.Blocked;
                else if (action == "remove") { _list.Remove(e); _selected.Remove(id); }
                _dirty = true;
                DrawEntries();
            }));
            ctx.Track(_entries.OnEdit(edit =>
            {
                var e = _list.FirstOrDefault(x => x.Key == edit.RowId);
                if (e == null || edit.NewValue is not { } v) { edit.Reject("Type a priority from 1 to 99."); return; }
                if (e.Blocked) { edit.Reject("A blocked entry has no priority."); return; }
                var p = (int)Math.Round(v);
                if (p < 1 || p > 99) { edit.Reject("Type a priority from 1 to 99."); return; }
                e.Priority = p;
                _dirty = true;
                edit.Resolve();
            }));

            // Set several at once: select rows (new picks arrive selected), choose, Apply.
            panel.BeginInline();
            _batchRule = panel.CompactDropdown("Selected", new[] { "allow", "block" }, "allow", new[] { "Allowed", "Blocked" });
            _batchPriority = panel.Number("Priority", Satisvampory.Engine.Model.DepositOptions.DefaultEntryPriority, 1, 99);
            _batchApply = panel.Button("Apply to selected");
            panel.EndInline();
            ctx.Track(_batchApply.OnClick(() =>
            {
                var block = _batchRule.Value == "block";
                var p = (int)Math.Round(_batchPriority.Value);
                foreach (var e in _list.Where(x => _selected.Contains(x.Key)))
                {
                    e.Blocked = block;
                    if (!block) e.Priority = Math.Clamp(p, 1, 99);
                }
                _dirty = true;
                DrawEntries();
            }));

            _private = panel.Toggle("Private chest", false);
            ctx.Track(_private.Term("Private chest"));
            ctx.Track(_private.OnChanged(_ => { if (_redrawing) return; _dirty = true; ShowPrivate(); }));
            _privateNote = panel.Label("Out of every shared castle and clan inventory: no deposits, belts, crafting or Find. Input and Output are off.");

            ctx.Track(panel.Button("Save").OnClick(Save));
            _empty = panel.Button("Empty trash");
            ctx.Track(_empty.OnClick(() =>
            {
                var now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _emptyArmedAt > 3)
                {
                    _emptyArmedAt = now;
                    ctx.Hud.Notify("Destroy everything in this trash chest? Press Empty trash again to confirm.", kind: UiNoticeKind.Warning, seconds: 3);
                    return;
                }
                _emptyArmedAt = -10;
                Owner.Send<EmptyTrashReq, PanelMsg>(new EmptyTrashReq { Target = Owner.Target() }, reply =>
                {
                    Owner.Apply(reply);
                    ctx.Hud.Notify("Trash emptied.", kind: UiNoticeKind.Success, seconds: 3);
                });
            }));
        }

        public override void Redraw()
        {
            var s = Owner.State;
            if (s == null) return;
            _ = Panel;
            // Another panel's reply must not throw away edits not yet saved here.
            if (_dirty && _draftFor == s.ContainerId) return;
            _dirty = false;
            _draftFor = s.ContainerId;
            _restriction.Text = s.NativeRestriction;
            var d = s.Deposit;
            _list.Clear();
            foreach (var p in d.Priorities)
                _list.Add(new Entry { Item = p.Item, Category = p.Item != 0 ? "" : p.Category, Label = p.Label, Priority = Math.Clamp(p.Adjust, 1, 99) });
            foreach (var b in d.Bans)
                _list.Add(new Entry { Item = b.Item, Category = b.Item != 0 ? "" : b.Category, Label = b.Label, Blocked = true });
            _special = d.Special;
            _header.Text = $"{s.Name} · {s.Where}" + (s.CanEdit ? "" : " · " + s.ReadOnlyReason) + (s.Notice.Length > 0 ? "\n" + s.Notice : "");
            _redrawing = true;
            try
            {
                _takes.Value = d.Takes.ToString();
                _private.Value = d.Private;
            }
            finally { _redrawing = false; }
            _empty.Visible = d.Special == (int)Satisvampory.Engine.Model.SpecialKind.Trash && s.CanEdit;
            _selected.Clear();
            _batchApply.Interactable = false;
            ShowPrivate();
            DrawEntries();
        }

        void ShowPrivate() => _privateNote.Visible = _private.Value;

        string Name(Entry e) =>
            e.Item != 0 ? ClientItems.NameOf(Owner.Ctx, e.Item, e.Label) : Help.TitleCase(e.Category);

        void DrawEntries()
        {
            var canEdit = Owner.State?.CanEdit ?? false;
            _entries.SetRows(_list.Select(e => new UiTableRow(e.Key) { ItemPrefab = e.Item }
                .Text("name", Name(e))
                .Text("type", e.Item != 0 ? "Item" : "Category")
                .Text("rule", e.Blocked ? "Blocked" : "Allowed", e.Blocked ? LabelTone.Danger : LabelTone.Ok)
                .Number("priority", e.Blocked ? null : e.Priority, e.Blocked ? "—" : null)
                .Action("rule", UiRowActionIcon.Check, e.Blocked ? "Allow it" : "Block it", canEdit)
                .Action("remove", UiRowActionIcon.Remove, "Remove from the list", canEdit)));
            _entries.SetSelected(_selected.ToArray());
        }

        /// <summary>From the picker: new entries arrive Allowed at 50 and selected, ready for Apply to selected.</summary>
        internal void AddPicked(IEnumerable<string> keys)
        {
            _selected.Clear();
            foreach (var key in keys)
            {
                if (_list.Any(x => x.Key == key)) { _selected.Add(key); continue; }
                var e = key.StartsWith("i:") && int.TryParse(key.Substring(2), out var guid)
                    ? new Entry { Item = guid, Label = ClientItems.NameOf(Owner.Ctx, guid) ?? key }
                    : new Entry { Category = key.Substring(2), Label = key.Substring(2) };
                _list.Add(e);
                _selected.Add(e.Key);
            }
            _dirty = true;
            _batchApply.Interactable = _selected.Count > 0 && (Owner.State?.CanEdit ?? false);
            Show();
            DrawEntries();
        }

        void Save()
        {
            var draft = new DepositMsg
            {
                Takes = int.TryParse(_takes.Value, out var t) ? t : 0,
                Special = _special,
                Private = _private.Value,
            };
            foreach (var e in _list)
            {
                var m = new PriorityMsg { Item = e.Item, Category = e.Category, Label = e.Label, Adjust = e.Priority };
                (e.Blocked ? draft.Bans : draft.Priorities).Add(m);
            }
            Owner.Send<SetDepositReq, PanelMsg>(new SetDepositReq { Target = Owner.Target(), Deposit = draft }, reply =>
            {
                _dirty = false;
                Owner.Apply(reply);
                Owner.Ctx.Hud.Notify("Filter saved.", kind: UiNoticeKind.Success, seconds: 3);
            });
        }
    }

    /// <summary>The Filter tab's Add picker: items and categories with icons and stored counts, several at once.</summary>
    internal sealed class FilterPickerView : View
    {
        IUiChecklist _list = null!;
        DepositView? _into;

        public FilterPickerView(Panels owner) : base(owner)
        {
        }

        protected override string Title => "Add to Filter";

        protected override void Build(IUiPanel panel)
        {
            var ctx = Owner.Ctx;
            panel.GameWindow(900);
            _list = panel.Checklist("filter.pick", Array.Empty<UiChecklistEntry>(), new UiChecklistOptions
            {
                Placeholder = "Search items or categories",
                Columns = new[]
                {
                    new UiTableColumn("type", "Type") { Width = 130 },
                    new UiTableColumn("count", "Stored", UiTableColumnKind.Number) { Format = "#,0", Width = 130 },
                },
                Sort = new[] { new UiTableSortKey("count", false) },
                Filters = new[]
                {
                    new UiChecklistFilter("stored", "In storage"),
                    new UiChecklistFilter("items", "Items"),
                    new UiChecklistFilter("categories", "Categories"),
                },
                DefaultFilter = "stored",
                AllFilterLabel = "Everything",
                ConfirmFormat = "Add {0}",
                FillHeight = true,
                EmptyText = "Nothing matches.",
            });
            ctx.Track(_list.OnConfirmed(picked =>
            {
                var into = _into;
                Hide();
                into?.AddPicked(picked);
            }));
            ctx.Track(_list.OnCancelled(() => { var into = _into; Hide(); into?.Show(); }));
        }

        public void Show(DepositView into)
        {
            _into = into;
            _ = Panel;
            Owner.Send<FilterPickerReq, FilterPickerReply>(new FilterPickerReq { Target = Owner.Target() }, r =>
            {
                var listed = new HashSet<string>(into.ListedKeys);
                var entries = new List<UiChecklistEntry>();
                for (var i = 0; i < r.Categories.Count; i++)
                {
                    var key = "c:" + r.Categories[i];
                    var n = i < r.CategoryCounts.Count ? r.CategoryCounts[i] : 0;
                    entries.Add(new UiChecklistEntry(key, Help.TitleCase(r.Categories[i]))
                    {
                        Filters = n > 0 ? new[] { "categories", "stored" } : new[] { "categories" },
                        Keywords = new[] { "category" },
                        DisabledReason = listed.Contains(key) ? "Already listed" : "",
                    }.Text("type", "Category").Number("count", n));
                }
                for (var i = 0; i < r.Items.Count; i++)
                {
                    // Only items the game names: the catalog also holds internal stand-ins.
                    if (ClientItems.NameOf(Owner.Ctx, r.Items[i]) is not { Length: > 0 } name) continue;
                    var key = "i:" + r.Items[i];
                    var n = i < r.ItemCounts.Count ? r.ItemCounts[i] : 0;
                    entries.Add(new UiChecklistEntry(key, name)
                    {
                        ItemPrefab = r.Items[i],
                        Filters = n > 0 ? new[] { "items", "stored" } : new[] { "items" },
                        DisabledReason = listed.Contains(key) ? "Already listed" : "",
                    }.Text("type", "Item").Number("count", n));
                }
                _list.SetSelected(Array.Empty<string>());
                _list.Query = "";
                _list.SetEntries(entries);
                into.Hide();
                base.Show();
            });
        }

        public override void Redraw()
        {
        }
    }

    /// <summary>The Add… picker for an Input or Output list.</summary>
    internal sealed class PickerView : View
    {
        int _side;
        IUiChecklist _list = null!;
        IUiLabel _note = null!;

        public PickerView(Panels owner) : base(owner)
        {
        }

        protected override string Title => "Add";

        protected override void Build(IUiPanel panel)
        {
            var ctx = Owner.Ctx;
            panel.GameWindow(1100); // room for the plot, distance and contents columns
            _note = panel.Label("");
            _list = panel.Checklist("targets", Array.Empty<UiChecklistEntry>(), new UiChecklistOptions
            {
                Placeholder = "Search chests, belts, plots, or what they hold",
                Columns = new[]
                {
                    new UiTableColumn("plot", "Plot") { Width = 150 },
                    new UiTableColumn("dist", "Distance", UiTableColumnKind.Number) { Format = "0 m", Width = 110 },
                    new UiTableColumn("what", "Holds / makes / wants") { Width = 280 },
                },
                Sort = new[] { new UiTableSortKey("dist") },
                Filters = new[]
                {
                    new UiChecklistFilter("common", "Chests & belts"),
                    new UiChecklistFilter("chests", "Chests"),
                    new UiChecklistFilter("belts", "Belts"),
                    new UiChecklistFilter("stations", "Stations"),
                },
                DefaultFilter = "common",
                AllFilterLabel = "Everything",
                ConfirmFormat = "Add {0}",
                Preview = true,
                FillHeight = true,
                EmptyText = "Nothing matches.",
            });
            ctx.Track(_list.OnConfirmed(picked =>
            {
                foreach (var id in picked)
                {
                    var row = id == "md" ? new RowMsg { Kind = 2 }
                        : id.StartsWith("b:") ? new RowMsg { Kind = 1, BeltId = id.Substring(2) }
                        : new RowMsg { Kind = 0, ContainerId = id.Substring(2) };
                    Owner.Edit(new EditListReq { Target = Owner.Target(), Side = _side, Op = 0, Entry = row });
                }
                Hide();
            }));
            ctx.Track(_list.OnCancelled(Hide));
            ctx.Track(panel.Button("Manage belts…").OnClick(() => { Hide(); Owner.OpenHub?.Invoke(Hub.BeltsTab); }));
            // View: the Object preview of a chest or station, then back to the list as it was.
            ctx.Track(_list.OnPreview(id =>
            {
                if (!id.StartsWith("c:") || Owner.PreviewContainer == null) return;
                _list.Suspend();
                Owner.PreviewContainer(id.Substring(2), () => _list.Resume());
            }));
        }

        public void Show(int side)
        {
            _side = side;
            _ = Panel;
            _note.Text = side == 0 ? "Add to Input: where this pulls from." : "Add to Output: where this sends to.";
            Owner.Send<PickerReq, PickerReply>(new PickerReq { Target = Owner.Target(), Side = side }, r =>
            {
                _list.SetSelected(Array.Empty<string>());
                _list.Query = "";
                _list.SetEntries(r.Rows.Select(x => new UiChecklistEntry(x.Key, x.Label)
                {
                    Section = x.Section,
                    Note = x.ClosesLoop ? "closes a loop" : "",
                    Keywords = new[] { x.Plot, x.Hint },
                    Filters = x.Kind switch
                    {
                        "chest" => new[] { "common", "chests" },
                        "belt" => new[] { "common", "belts" },
                        "station" => new[] { "stations" },
                        "special" => new[] { "common", "chests" },
                        _ => new[] { "common" },
                    },
                    BuildingPrefab = x.BuildingPrefab,
                    DisabledReason = x.Refused ?? "",
                }
                .Text("plot", x.Plot)
                .Number("dist", x.Distance >= 0 ? x.Distance : null, x.Distance >= 0 ? null : "")
                .Text("what", x.Hint)));
                base.Show();
            });
        }

        public override void Redraw()
        {
        }
    }

}
