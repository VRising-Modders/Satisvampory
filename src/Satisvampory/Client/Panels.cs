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
        IVanillaPanelTabs? _nativeTabs;

        internal Panels(SatisContext ctx, Satisvampory.Config.PlayerConfig player)
        {
            _ctx = ctx;
            _player = player;
            _input = new ListView(this, 0);
            _output = new ListView(this, 1);
            _deposit = new DepositView(this);
            _picker = new PickerView(this);
        }

        internal SatisContext Ctx => _ctx;

        internal PanelMsg? State => _state;

        internal void Attach()
        {
            Tabs(VanillaUiSite.ContainerPanel, new[] { "Inventory", "Input", "Output", "Accepts" });
            Tabs(VanillaUiSite.StationPanel, new[] { "Production", "Input", "Output" });
            // Unit stations (tomb, vermin nest, Nether Gate) have no tab row, so no Input/Output tabs:
            // they get edge buttons, shown only on those windows.
            Site(VanillaUiSite.StationPanel, VanillaPanelAnchor.LeftEdge, "Input", "Input: where this station's materials come from", () => Open(_input),
                kind => kind == VanillaPanelKind.UnitStation);
            Site(VanillaUiSite.StationPanel, VanillaPanelAnchor.RightEdge, "Output", "Output: where this station's products go", () => Open(_output),
                kind => kind == VanillaPanelKind.UnitStation);
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

        /// <summary>Opens the hub on a tab (set by the client half).</summary>
        internal Action<int>? OpenHub { get; set; }

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
        IUiOrderedList _list = null!;
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
            _add = panel.CompactDropdown("Add", new[] { "list", "pick" }, "", new[] { "Chests or belts…", "Pick in world" });
            panel.EndInline();
            ctx.Track(_add.OnChanged(v =>
            {
                if (_redrawing) return;
                _redrawing = true;
                try { _add.Value = ""; }
                finally { _redrawing = false; }
                if (v == "list") Owner.OpenPicker(_side);
                else if (v == "pick") PickInWorld();
            }));

            _list = panel.OrderedList("rows", Array.Empty<UiListItem>(), new UiOrderedListOptions
            {
                EmptyText = _side == 0 ? "No sources yet: Add a chest or belt." : "No destinations yet: Add a chest, belt or Mass Deposit.",
            });
            ctx.Track(_list.Term(_side == 0 ? "Input" : "Output"));
            ctx.Track(_list.OnReorder(m => Owner.Edit(new EditListReq { Target = Owner.Target(), Side = _side, Op = 2, Index = m.From, To = m.To })));
            ctx.Track(_list.OnRemove(item => Owner.Edit(new EditListReq { Target = Owner.Target(), Side = _side, Op = 1, Index = int.Parse(item.Id) })));

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
                _header.Text = $"{s.Name} · {s.Where}" + (s.CanEdit ? "" : " · " + s.ReadOnlyReason) + (s.Notice.Length > 0 ? "\n" + s.Notice : "");
                _order.Value = (_side == 0 ? s.PullOrder : s.SendOrder).ToString();
                var rows = _side == 0 ? s.Input : s.Output;
                _list.SetItems(rows.Select((r, i) => new UiListItem(i.ToString(), r.Name)
                {
                    Badge = r.Kind switch { 1 => "Belt", 2 => _side == 0 ? "Pull" : "Push", _ => "Chest" },
                    Note = r.Where + (string.IsNullOrEmpty(r.Status) ? "" : " · " + r.Status),
                    Removable = s.CanEdit,
                }));
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
            }, () => Show(), new UiWorldPickOptions
            {
                // Pick several in one go; Esc ends it and brings the list back.
                Repeat = true,
                Prompt = _side == 0 ? "Interact with containers or stations to pull from. Esc when done." : "Interact with containers or stations to send to. Esc when done.",
            });
        }

    }

    /// <summary>A chest's deposit settings (spec 01).</summary>
    internal sealed class DepositView : View
    {
        protected override string? TipId => Help.DepositTip;

        IUiLabel _header = null!;
        IUiLabel _restriction = null!;
        IUiDropdown _takes = null!;
        IUiNumber _base = null!;
        IUiOrderedList _entries = null!;
        IUiInput _entryName = null!;
        IUiNumber _entryAdjust = null!;
        IUiDropdown _special = null!;
        IUiButton _empty = null!;
        float _emptyArmedAt = -10;
        IUiToggle _advanced = null!;
        IUiButton _ban = null!;
        DepositMsg _draft = new();
        bool _dirty;
        string _draftFor = "";

        public DepositView(Panels owner) : base(owner)
        {
        }

        protected override string Title => "Logistics";
        protected override int NavigationTab => 3;

        static readonly string[] TakesValues = { "0", "1", "2" };
        static readonly string[] TakesLabels = { "Anything", "Only what's listed", "Nothing" };
        static readonly string[] SpecialValues = { "0", "1", "2", "3", "4", "5", "6" };
        static readonly string[] SpecialLabels = { "None", "Overflow", "Skip", "Salvage", "Spawner", "Trash", "Spoils" };

        protected override void Build(IUiPanel panel)
        {
            var ctx = Owner.Ctx;
            _header = panel.Label("");
            panel.Label("Items accepted from belts and automatic deposits. Manual deposits are unaffected.");
            _restriction = panel.Label("");
            _takes = panel.Dropdown("Accepts", TakesValues, "0", TakesLabels);
            ctx.Track(_takes.Term("Accepts"));
            _advanced = panel.Toggle("Advanced settings", false);
            ctx.Track(_advanced.OnChanged(_ => ShowAdvanced()));
            _base = panel.Number("Base priority", 0, -10, 10);
            _entries = panel.OrderedList("entries", Array.Empty<UiListItem>(), new UiOrderedListOptions
            {
                Reorder = false,
                EmptyText = "No allowed items. Add an item or category.",
            });
            ctx.Track(_entries.OnRemove(item =>
            {
                var parts = item.Id.Split(':');
                var list = parts[0] == "p" ? _draft.Priorities : _draft.Bans;
                var i = int.Parse(parts[1]);
                if (i >= 0 && i < list.Count) list.RemoveAt(i);
                _dirty = true;
                DrawEntries();
            }));
            _entryName = panel.Input("Item or category", "");
            _entryAdjust = panel.Number("Priority change", 0, -10, 10);
            ctx.Track(panel.Button("+ Add item or category").OnClick(() => AddEntry(ban: false)));
            _ban = panel.Button("+ Never deposit here");
            ctx.Track(_ban.OnClick(() => AddEntry(ban: true)));
            _special = panel.Dropdown("Special kind", SpecialValues, "0", SpecialLabels);
            ShowAdvanced();
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
            _draft = new DepositMsg
            {
                Takes = d.Takes,
                BasePriority = d.BasePriority,
                Special = d.Special,
                Priorities = d.Priorities.ToList(),
                Bans = d.Bans.ToList(),
                Belts = d.Belts.ToList(),
            };
            _header.Text = $"{s.Name} · {s.Where}" + (s.CanEdit ? "" : " · " + s.ReadOnlyReason) + (s.Notice.Length > 0 ? "\n" + s.Notice : "");
            _takes.Value = d.Takes.ToString();
            _base.Value = d.BasePriority;
            _special.Value = d.Special.ToString();
            _empty.Visible = d.Special == (int)Satisvampory.Engine.Model.SpecialKind.Trash && s.CanEdit;
            DrawEntries();
        }

        void ShowAdvanced()
        {
            _base.Visible = _entryAdjust.Visible = _special.Visible = _ban.Visible = _advanced.Value;
        }

        void DrawEntries()
        {
            var items = new List<UiListItem>();
            for (var i = 0; i < _draft.Priorities.Count; i++)
            {
                var p = _draft.Priorities[i];
                items.Add(new UiListItem("p:" + i, p.Item != 0 ? ClientItems.NameOf(Owner.Ctx, p.Item, p.Label) : p.Label.Length > 0 ? p.Label : p.Category) { Badge = (p.Adjust >= 0 ? "+" : "−") + Math.Abs(p.Adjust) });
            }
            for (var i = 0; i < _draft.Bans.Count; i++)
            {
                var b = _draft.Bans[i];
                items.Add(new UiListItem("b:" + i, b.Item != 0 ? ClientItems.NameOf(Owner.Ctx, b.Item, b.Label) : b.Label.Length > 0 ? b.Label : b.Category) { Badge = "Never" });
            }
            _entries.SetItems(items);
        }

        void AddEntry(bool ban)
        {
            var name = _entryName.Value.Trim();
            if (name.Length == 0) return;
            var resolved = ClientItems.Resolve(Owner.Ctx, name);
            var entry = int.TryParse(resolved, out var guid)
                ? new PriorityMsg { Item = guid, Label = name, Adjust = (int)Math.Round(_entryAdjust.Value) }
                : new PriorityMsg { Category = name, Label = name, Adjust = (int)Math.Round(_entryAdjust.Value) };
            (ban ? _draft.Bans : _draft.Priorities).Add(entry);
            _dirty = true;
            _entryName.Value = string.Empty;
            DrawEntries();
        }

        void Save()
        {
            _draft.Takes = int.TryParse(_takes.Value, out var t) ? t : 0;
            _draft.BasePriority = (int)Math.Round(_base.Value);
            _draft.Special = int.TryParse(_special.Value, out var sp) ? sp : 0;
            Owner.Send<SetDepositReq, PanelMsg>(new SetDepositReq { Target = Owner.Target(), Deposit = _draft }, reply =>
            {
                _dirty = false;
                Owner.Apply(reply);
                Owner.Ctx.Hud.Notify("Accepts settings saved.", kind: UiNoticeKind.Success, seconds: 3);
            });
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
                    new UiTableColumn("plot", "Plot") { Width = 220 },
                    new UiTableColumn("dist", "Distance", UiTableColumnKind.Number) { Format = "0 m", Width = 130 },
                    new UiTableColumn("what", "Holds / makes / wants") { Width = 330 },
                },
                Sort = new[] { new UiTableSortKey("dist") },
                Filters = new[]
                {
                    new UiChecklistFilter("common", "Chests & belts"),
                    new UiChecklistFilter("chests", "Chests"),
                    new UiChecklistFilter("belts", "Belts"),
                    new UiChecklistFilter("stations", "Stations"),
                    new UiChecklistFilter("special", "Special chests"),
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
                        "special" => new[] { "special" },
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
