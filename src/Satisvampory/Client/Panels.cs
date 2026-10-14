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
    /// The container panels (spec 02, 01): Input on the left edge, Output on the right edge, and
    /// Deposit settings beside the rename field of chests; Input and Output on stations and
    /// hearts. Each button opens Satisvampory's own panel; every change is a request and the panel
    /// redraws from the server's reply, never optimistically.
    /// </summary>
    internal sealed class Panels
    {
        readonly SatisContext _ctx;
        VanillaPanelTarget _target;
        bool _hasTarget;
        string _containerId = string.Empty;
        PanelMsg? _state;

        readonly ListView _input;
        readonly ListView _output;
        readonly DepositView _deposit;
        readonly PickerView _picker;
        readonly RecipesView _recipes;

        internal Panels(SatisContext ctx)
        {
            _ctx = ctx;
            _input = new ListView(this, 0);
            _output = new ListView(this, 1);
            _deposit = new DepositView(this);
            _picker = new PickerView(this);
            _recipes = new RecipesView(this);
        }

        internal SatisContext Ctx => _ctx;

        internal PanelMsg? State => _state;

        internal void Attach()
        {
            Site(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, "Input", "Input: where this pulls from", () => Open(_input));
            Site(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.RightEdge, "Output", "Output: where this sends to", () => Open(_output));
            Site(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideRename, "Deposit", "Deposit settings: what this chest takes for Mass Deposit", () => Open(_deposit));
            Site(VanillaUiSite.StationPanel, VanillaPanelAnchor.LeftEdge, "Input", "Input: where this pulls from", () => Open(_input));
            Site(VanillaUiSite.StationPanel, VanillaPanelAnchor.RightEdge, "Output", "Output: where its products go", () => Open(_output));
            Site(VanillaUiSite.CastleHeartPanel, VanillaPanelAnchor.LeftEdge, "Input", "Input: where this heart's blood comes from", () => Open(_input));
            Site(VanillaUiSite.CastleHeartPanel, VanillaPanelAnchor.RightEdge, "Output", "Output: only items you send out of the heart", () => Open(_output));
        }

        void Site(VanillaUiSite site, VanillaPanelAnchor anchor, string caption, string tip, Action onClick)
        {
            var handle = UiApi.AttachToVanilla(site, anchor, _ctx.Owner, _ctx.Guid);
            _ctx.Track(handle);
            var button = handle.Button(caption);
            _ctx.Track(button.OnClick(onClick));
            _ctx.Track(button.Tooltip(tip, caption));
            _ctx.Track(handle.OnPanel(e =>
            {
                if (e.Change == VanillaPanelChange.Closed)
                {
                    HideAll();
                    return;
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
            Load(() => view.Show());
        }

        /// <summary>Ask the server for the panel and redraw every open view from the reply.</summary>
        internal void Load(Action? then = null) =>
            Send<GetPanelReq, PanelMsg>(new GetPanelReq { Target = Target() }, reply =>
            {
                Apply(reply);
                then?.Invoke();
            });

        internal void Apply(PanelMsg reply)
        {
            _state = reply;
            _containerId = reply.ContainerId;
            _input.Redraw();
            _output.Redraw();
            _deposit.Redraw();
            _recipes.Redraw();
        }

        internal void Send<TReq, TReply>(TReq req, Action<TReply> ok) where TReq : class, new() where TReply : class, new() =>
            _ctx.Requests.Send<TReq, TReply>(req, r =>
            {
                if (r.Ok && r.Reply != null) ok(r.Reply);
                else _ctx.Hud.Notify(Reason(r.Status, r.Reason), kind: UiNoticeKind.Warning, seconds: 6);
            });

        internal void Edit(EditListReq req) => Send<EditListReq, PanelMsg>(req, Apply);

        internal void OpenPicker(int side) => _picker.Show(side);

        internal void OpenRecipes() => _recipes.Show();

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
            _recipes.Hide();
        }
    }

    /// <summary>A Satisvampory panel shown beside a vanilla window.</summary>
    internal abstract class View
    {
        protected readonly Panels Owner;
        IUiPanel? _panel;

        protected View(Panels owner) => Owner = owner;

        protected abstract string Title { get; }

        protected abstract void Build(IUiPanel panel);

        public abstract void Redraw();

        protected IUiPanel Panel
        {
            get
            {
                if (_panel == null)
                {
                    _panel = UiApi.Panel(Title, Owner.Ctx.Owner, Owner.Ctx.Guid);
                    _panel.CaptureMode = UiCaptureMode.WhenHovered;
                    Owner.Ctx.Track(_panel);
                    Build(_panel);
                }
                return _panel;
            }
        }

        public void Show()
        {
            Redraw();
            UiApi.Show(Panel);
        }

        public void Hide()
        {
            if (_panel != null) UiApi.Hide(_panel);
        }
    }

    /// <summary>An Input (side 0) or Output (side 1) list.</summary>
    internal sealed class ListView : View
    {
        readonly int _side;
        IUiLabel _header = null!;
        IUiDropdown _order = null!;
        IUiOrderedList _list = null!;
        IUiLabel _loop = null!;
        IUiDropdown _loopMode = null!;
        IUiButton _recipesButton = null!;
        IUiInput _tryAmount = null!;
        IUiInput _tryItem = null!;
        IUiLabel _tryResult = null!;
        bool _redrawing;

        public ListView(Panels owner, int side) : base(owner) => _side = side;

        protected override string Title => _side == 0 ? "Input" : "Output";

        static readonly string[] PullValues = { "0", "1" };
        static readonly string[] PullLabels = { "Respect list order", "Reverse Mass Deposit (worst-stored first)" };
        static readonly string[] SendValues = { "0", "1" };
        static readonly string[] SendLabels = { "Fill in order", "Spread evenly" };

        protected override void Build(IUiPanel panel)
        {
            var ctx = Owner.Ctx;
            _header = panel.Label("");
            _order = _side == 0
                ? panel.Dropdown("Pull order", PullValues, "0", PullLabels)
                : panel.Dropdown("Send order", SendValues, "0", SendLabels);
            ctx.Track(_order.OnChanged(v =>
            {
                if (_redrawing || Owner.State == null) return;
                var s = Owner.State;
                Owner.Edit(new EditListReq
                {
                    Target = Owner.Target(),
                    Side = _side,
                    Op = 3,
                    PullOrder = _side == 0 ? int.Parse(v) : s.PullOrder,
                    SendOrder = _side == 1 ? int.Parse(v) : s.SendOrder,
                });
            }));

            _list = panel.OrderedList("rows", Array.Empty<UiListItem>(), new UiOrderedListOptions
            {
                EmptyText = _side == 0
                    ? "Add the chests or belts this pulls from."
                    : "Add where this sends items. Push: Mass Deposit sends each item to the chest that rates it highest.",
            });
            ctx.Track(_list.OnReorder(m => Owner.Edit(new EditListReq { Target = Owner.Target(), Side = _side, Op = 2, Index = m.From, To = m.To })));
            ctx.Track(_list.OnRemove(item => Owner.Edit(new EditListReq { Target = Owner.Target(), Side = _side, Op = 1, Index = int.Parse(item.Id) })));

            ctx.Track(panel.Button("+ Add…").OnClick(() => Owner.OpenPicker(_side)));
            ctx.Track(panel.Button("◎ Pick in world").OnClick(() =>
                ctx.Hud.Notify("Pick in world needs Progenitor to report the picked chest's network id; use Add… for now.", kind: UiNoticeKind.Info, seconds: 6)));
            _loop = panel.Label("");
            _loopMode = panel.Dropdown("Loop mode", new[] { "0", "1", "2" }, "0", new[] { "Cycle once, then wait", "Even distribution", "Off (closing edge never runs)" });
            ctx.Track(_loopMode.OnChanged(v =>
            {
                if (_redrawing || Owner.State == null || Owner.State.LoopKey.Length == 0) return;
                Owner.Send<SetLoopReq, PanelMsg>(new SetLoopReq { Target = Owner.Target(), LoopKey = Owner.State.LoopKey, Mode = int.Parse(v) }, Owner.Apply);
            }));
            if (_side == 0)
            {
                _recipesButton = panel.Button("Recipes…");
                ctx.Track(_recipesButton.OnClick(Owner.OpenRecipes));
                ctx.Track(_recipesButton.Tooltip("Which recipes this station works on. Only switched-on recipes pull inputs.", "Recipes"));
            }

            panel.Divider();
            _tryAmount = panel.Input("Try it: amount", "100");
            _tryItem = panel.Input("Item", "");
            ctx.Track(panel.Button("Try it").OnClick(TryIt));
            _tryResult = panel.Label("");
        }

        public override void Redraw()
        {
            var s = Owner.State;
            if (s == null) return;
            _ = Panel;
            _redrawing = true;
            try
            {
                _header.Text = $"{s.Name} · {s.Where}" + (s.CanEdit ? "" : " · " + s.ReadOnlyReason);
                _order.Value = (_side == 0 ? s.PullOrder : s.SendOrder).ToString();
                var rows = _side == 0 ? s.Input : s.Output;
                _list.SetItems(rows.Select((r, i) => new UiListItem(i.ToString(), r.Name)
                {
                    Badge = r.Kind switch { 1 => "Belt", 2 => _side == 0 ? "Pull" : "Push", _ => "Chest" },
                    Note = r.Where + (string.IsNullOrEmpty(r.Status) ? "" : " · " + r.Status),
                    Detail = r.Filter,
                    Removable = s.CanEdit,
                }));
                _loop.Text = s.LoopBanner;
                _loop.Visible = s.LoopBanner.Length > 0;
                _loopMode.Visible = s.LoopKey.Length > 0;
                _loopMode.Value = s.LoopMode.ToString();
                if (_side == 0) _recipesButton.Visible = s.NodeKind == 1;
            }
            finally
            {
                _redrawing = false;
            }
        }

        void TryIt()
        {
            if (!int.TryParse(_tryAmount.Value, out var n) || n <= 0) n = 100;
            Owner.Send<TryItReq, TryItReply>(new TryItReq { Target = Owner.Target(), Mode = _side == 0 ? 0 : 1, Item = _tryItem.Value, Amount = n }, r =>
            {
                var lines = new List<string> { $"{n:N0} {r.Item}:" };
                lines.AddRange(r.Steps.Select(x => $"  {x.Name} · {x.Where} — {x.Amount:N0}  ({x.Why})"));
                if (r.Short > 0) lines.Add($"  short by {r.Short:N0}");
                if (r.NotUsed.Count > 0)
                {
                    lines.Add("Not used:");
                    lines.AddRange(r.NotUsed.Take(8).Select(x => $"  {x.Name} · {x.Where} — {x.Why}"));
                    if (r.NotUsed.Count > 8) lines.Add($"  … and {r.NotUsed.Count - 8} more");
                }
                _tryResult.Text = string.Join("\n", lines);
            });
        }
    }

    /// <summary>A chest's deposit settings (spec 01).</summary>
    internal sealed class DepositView : View
    {
        IUiLabel _header = null!;
        IUiDropdown _takes = null!;
        IUiNumber _base = null!;
        IUiOrderedList _entries = null!;
        IUiInput _entryName = null!;
        IUiNumber _entryAdjust = null!;
        IUiDropdown _special = null!;
        IUiLabel _belts = null!;
        IUiButton _empty = null!;
        IUiInput _tryItem = null!;
        IUiLabel _tryResult = null!;
        DepositMsg _draft = new();

        public DepositView(Panels owner) : base(owner)
        {
        }

        protected override string Title => "Deposit settings";

        static readonly string[] TakesValues = { "0", "1", "2" };
        static readonly string[] TakesLabels = { "Anything", "Only what's listed", "Nothing" };
        static readonly string[] SpecialValues = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        static readonly string[] SpecialLabels = { "None", "Overflow", "Skip", "Salvage", "Spawner", "Brazier", "Brazier (night)", "Brazier (proximity)", "Trash", "Spoils" };

        protected override void Build(IUiPanel panel)
        {
            var ctx = Owner.Ctx;
            _header = panel.Label("");
            _takes = panel.Dropdown("Takes", TakesValues, "0", TakesLabels);
            _base = panel.Number("Base priority", 0, -10, 10);
            _entries = panel.OrderedList("entries", Array.Empty<UiListItem>(), new UiOrderedListOptions
            {
                Reorder = false,
                EmptyText = "No items or categories listed. On \"Only what's listed\" the chest takes nothing until you add some.",
            });
            ctx.Track(_entries.OnRemove(item =>
            {
                var parts = item.Id.Split(':');
                var list = parts[0] == "p" ? _draft.Priorities : _draft.Bans;
                var i = int.Parse(parts[1]);
                if (i >= 0 && i < list.Count) list.RemoveAt(i);
                DrawEntries();
            }));
            _entryName = panel.Input("Item or category", "");
            _entryAdjust = panel.Number("Priority change", 0, -10, 10);
            ctx.Track(panel.Button("+ Add priority").OnClick(() => AddEntry(ban: false)));
            ctx.Track(panel.Button("+ Never deposit here").OnClick(() => AddEntry(ban: true)));
            _special = panel.Dropdown("Special kind", SpecialValues, "0", SpecialLabels);
            _belts = panel.Label("");
            ctx.Track(panel.Button("Save").OnClick(Save));
            _empty = panel.Button("Empty trash");
            ctx.Track(_empty.OnClick(() =>
                Owner.Send<EmptyTrashReq, PanelMsg>(new EmptyTrashReq { Target = Owner.Target() }, reply =>
                {
                    Owner.Apply(reply);
                    ctx.Hud.Notify("Trash emptied.", kind: UiNoticeKind.Success, seconds: 3);
                })));

            panel.Divider();
            _tryItem = panel.Input("Try it: item", "");
            ctx.Track(panel.Button("Where would 1 stack go?").OnClick(TryIt));
            _tryResult = panel.Label("");
        }

        public override void Redraw()
        {
            var s = Owner.State;
            if (s == null) return;
            _ = Panel;
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
            _header.Text = $"{s.Name} · {s.Where}" + (s.CanEdit ? "" : " · " + s.ReadOnlyReason);
            _takes.Value = d.Takes.ToString();
            _base.Value = d.BasePriority;
            _special.Value = d.Special.ToString();
            _belts.Text = d.Belts.Count == 0 ? "On no belts." : "On belts: " + string.Join(", ", d.Belts);
            _empty.Visible = d.Special == 8 && s.CanEdit;
            DrawEntries();
        }

        void DrawEntries()
        {
            var items = new List<UiListItem>();
            for (var i = 0; i < _draft.Priorities.Count; i++)
            {
                var p = _draft.Priorities[i];
                items.Add(new UiListItem("p:" + i, p.Label.Length > 0 ? p.Label : p.Category) { Badge = (p.Adjust >= 0 ? "+" : "−") + Math.Abs(p.Adjust) });
            }
            for (var i = 0; i < _draft.Bans.Count; i++)
            {
                var b = _draft.Bans[i];
                items.Add(new UiListItem("b:" + i, b.Label.Length > 0 ? b.Label : b.Category) { Badge = "Never" });
            }
            _entries.SetItems(items);
        }

        void AddEntry(bool ban)
        {
            var name = _entryName.Value.Trim();
            if (name.Length == 0) return;
            var entry = new PriorityMsg { Category = name, Label = name, Adjust = (int)Math.Round(_entryAdjust.Value) };
            (ban ? _draft.Bans : _draft.Priorities).Add(entry);
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
                Owner.Apply(reply);
                Owner.Ctx.Hud.Notify("Deposit settings saved.", kind: UiNoticeKind.Success, seconds: 3);
            });
        }

        void TryIt() =>
            Owner.Send<TryItReq, TryItReply>(new TryItReq { Target = Owner.Target(), Mode = 2, Item = _tryItem.Value, Amount = 1 }, r =>
            {
                var here = Owner.State?.Name ?? "";
                var first = r.Steps.FirstOrDefault();
                var mine = r.NotUsed.FirstOrDefault(x => x.Name == here);
                _tryResult.Text = first == null
                    ? $"{r.Item}: no chest on the island takes it."
                    : $"{r.Item} → {first.Name} · {first.Where} ({first.Why})" + (mine != null ? $"\nThis chest: not used — {mine.Why}" : "");
            });
    }

    /// <summary>The Add… picker for an Input or Output list.</summary>
    internal sealed class PickerView : View
    {
        int _side;
        IUiPicker _picker = null!;
        IUiLabel _note = null!;

        public PickerView(Panels owner) : base(owner)
        {
        }

        protected override string Title => "Add";

        protected override void Build(IUiPanel panel)
        {
            var ctx = Owner.Ctx;
            _note = panel.Label("");
            _picker = panel.Picker("targets", Array.Empty<UiPickerEntry>(), new UiPickerOptions { Multi = true, Rows = 10, Placeholder = "Search chests, belts, plots, or what they hold" });
            ctx.Track(_picker.OnPicked(picked =>
            {
                foreach (var e in picked)
                {
                    var row = e.Id == "md" ? new RowMsg { Kind = 2 }
                        : e.Id.StartsWith("b:") ? new RowMsg { Kind = 1, BeltId = e.Id.Substring(2) }
                        : new RowMsg { Kind = 0, ContainerId = e.Id.Substring(2) };
                    Owner.Edit(new EditListReq { Target = Owner.Target(), Side = _side, Op = 0, Entry = row });
                }
                Hide();
            }));
        }

        public void Show(int side)
        {
            _side = side;
            _ = Panel;
            _note.Text = side == 0 ? "Add to Input: where this pulls from." : "Add to Output: where this sends to.";
            Owner.Send<PickerReq, PickerReply>(new PickerReq { Target = Owner.Target(), Side = side }, r =>
            {
                _picker.SetEntries(r.Rows.Select(x => new UiPickerEntry(x.Key, x.Label)
                {
                    Section = x.Section,
                    Note = x.ClosesLoop ? x.Note + " · closes a loop" : x.Note,
                    DisabledReason = string.IsNullOrEmpty(x.Refused) ? null : x.Refused,
                }));
                base.Show();
            });
        }

        public override void Redraw()
        {
        }
    }

    /// <summary>A station's recipe switches (spec 04): selected recipes are on.</summary>
    internal sealed class RecipesView : View
    {
        IUiLabel _note = null!;
        IUiPicker _picker = null!;

        public RecipesView(Panels owner) : base(owner)
        {
        }

        protected override string Title => "Recipes";

        protected override void Build(IUiPanel panel)
        {
            var ctx = Owner.Ctx;
            _note = panel.Label("Select the recipes this station should work on. Only those pull inputs, and only in whole crafts.");
            _picker = panel.Picker("recipes", Array.Empty<UiPickerEntry>(), new UiPickerOptions { Multi = true, Rows = 12, Placeholder = "Search recipes" });
            ctx.Track(panel.Button("Apply").OnClick(() =>
                Owner.Send<SetRecipesReq, PanelMsg>(new SetRecipesReq
                {
                    Target = Owner.Target(),
                    On = _picker.Selected.Select(int.Parse).ToList(),
                }, reply =>
                {
                    Owner.Apply(reply);
                    ctx.Hud.Notify("Recipes updated.", kind: UiNoticeKind.Success, seconds: 3);
                })));
        }

        public override void Redraw()
        {
            var s = Owner.State;
            if (s == null || s.NodeKind != 1) return;
            _ = Panel;
            _picker.SetEntries(s.Recipes.Select(r => new UiPickerEntry(r.Guid.ToString(), r.Name) { Note = r.Inputs }));
            _picker.ClearSelection();
            foreach (var r in s.Recipes.Where(r => r.On)) _picker.Select(r.Guid.ToString());
            _note.Visible = true;
        }
    }
}
