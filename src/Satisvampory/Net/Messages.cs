// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using Progenitor.Networking;

namespace Satisvampory.Net
{
    // Versioned request/reply messages over Progenitor's ctx.Requests (spec 12). Field numbers are
    // permanent: never reuse or retype one; add new fields with Since = n.

    /// <summary>Which container a panel is showing: the game's network id from the panel site, or a stable id once known.</summary>
    [ModMessage("sv.target")]
    public sealed class TargetMsg
    {
        [Field(1)] public int NetIndex;
        [Field(2)] public int NetGeneration;
        [Field(3)] public int NetType;
        /// <summary>Stable container id ("N" format) when the client already has it from an earlier reply.</summary>
        [Field(4)] public string ContainerId = "";
    }

    [ModMessage("sv.filter")]
    public sealed class FilterMsg
    {
        [Field(1)] public List<int> Items = new();
        [Field(2)] public List<string> Categories = new();
        [Field(3)] public List<int> ExcludeItems = new();
        [Field(4)] public List<string> ExcludeCategories = new();
    }

    /// <summary>One Input or Output row as the panel shows it.</summary>
    [ModMessage("sv.row")]
    public sealed class RowMsg
    {
        /// <summary>0 container, 1 belt, 2 Mass Deposit (Push on Output, Pull on Input).</summary>
        [Field(1)] public int Kind;
        [Field(2)] public string ContainerId = "";
        [Field(3)] public string BeltId = "";
        [Field(4)] public string Name = "";
        /// <summary>"here · 6 m", "Plot 2 (Ashfall)", "3 chests · here, Plot 2".</summary>
        [Field(5)] public string Where = "";
        [Field(6)] public string Filter = "";
        /// <summary>"moving · 320 last minute", "only reserve left (10)", "idle".</summary>
        [Field(7)] public string Status = "";
        /// <summary>0 idle, 1 moving, 2 blocked.</summary>
        [Field(8)] public int Tone;
        [Field(9)] public FilterMsg FilterSpec = new();
    }

    [ModMessage("sv.priority")]
    public sealed class PriorityMsg
    {
        /// <summary>Item guid, or 0 for a category entry.</summary>
        [Field(1)] public int Item;
        [Field(2)] public string Category = "";
        [Field(3)] public string Label = "";
        [Field(4)] public int Adjust;
    }

    /// <summary>A chest's deposit settings (spec 01).</summary>
    [ModMessage("sv.deposit")]
    public sealed class DepositMsg
    {
        /// <summary>0 anything, 1 only what's listed, 2 nothing.</summary>
        [Field(1)] public int Takes;
        [Field(2)] public int BasePriority;
        [Field(3)] public List<PriorityMsg> Priorities = new();
        [Field(4)] public List<PriorityMsg> Bans = new();
        /// <summary>The engine's SpecialKind as an int.</summary>
        [Field(5)] public int Special;
        [Field(6)] public List<string> Belts = new();
    }

    /// <summary>Everything one container's panels show.</summary>
    [ModMessage("sv.panel")]
    public sealed class PanelMsg
    {
        [Field(1)] public string ContainerId = "";
        [Field(2)] public string Name = "";
        [Field(3)] public string Where = "";
        /// <summary>0 chest, 1 station, 2 heart.</summary>
        [Field(4)] public int NodeKind;
        [Field(5)] public bool CanEdit;
        [Field(6)] public string ReadOnlyReason = "";
        [Field(7)] public List<RowMsg> Input = new();
        [Field(8)] public List<RowMsg> Output = new();
        /// <summary>0 list order, 1 reverse Mass Deposit.</summary>
        [Field(9)] public int PullOrder;
        /// <summary>0 fill in order, 1 spread evenly.</summary>
        [Field(10)] public int SendOrder;
        [Field(11)] public DepositMsg Deposit = new();
        /// <summary>"Loop · 3 chests · cycle once · waiting for a change", or empty.</summary>
        [Field(12)] public string LoopBanner = "";
        /// <summary>Whether Pull: reverse Mass Deposit may be offered on the Input (stations, hearts).</summary>
        [Field(13)] public bool HasDemand;
        [Field(14)] public bool HasOutput = true;
    }

    [ModMessage("sv.panel.get")]
    public sealed class GetPanelReq
    {
        [Field(1)] public TargetMsg Target = new();
    }

    /// <summary>One list edit. The reply is the whole panel, as the server now sees it.</summary>
    [ModMessage("sv.list.edit")]
    public sealed class EditListReq
    {
        [Field(1)] public TargetMsg Target = new();
        /// <summary>0 input, 1 output.</summary>
        [Field(2)] public int Side;
        /// <summary>0 add, 1 remove, 2 move, 3 set orders, 4 set filter.</summary>
        [Field(3)] public int Op;
        [Field(4)] public int Index;
        [Field(5)] public int To;
        [Field(6)] public RowMsg Entry = new();
        [Field(7)] public int PullOrder;
        [Field(8)] public int SendOrder;
    }

    [ModMessage("sv.deposit.set")]
    public sealed class SetDepositReq
    {
        [Field(1)] public TargetMsg Target = new();
        [Field(2)] public DepositMsg Deposit = new();
    }

    [ModMessage("sv.picker.get")]
    public sealed class PickerReq
    {
        [Field(1)] public TargetMsg Target = new();
        [Field(2)] public int Side;
        [Field(3)] public string Search = "";
    }

    [ModMessage("sv.picker.row")]
    public sealed class PickerRowMsg
    {
        /// <summary>"c:&lt;id&gt;", "b:&lt;id&gt;", "md".</summary>
        [Field(1)] public string Key = "";
        [Field(2)] public string Label = "";
        [Field(3)] public string Section = "";
        [Field(4)] public string Note = "";
        [Field(5)] public string Refused = "";
        [Field(6)] public bool ClosesLoop;
    }

    [ModMessage("sv.picker")]
    public sealed class PickerReply
    {
        [Field(1)] public List<PickerRowMsg> Rows = new();
    }

    /// <summary>Try it: what if n of X (spec 03, "Evaluator").</summary>
    [ModMessage("sv.tryit")]
    public sealed class TryItReq
    {
        [Field(1)] public TargetMsg Target = new();
        /// <summary>0 input (pull), 1 output (rows), 2 deposit settings (push from the island).</summary>
        [Field(2)] public int Mode;
        /// <summary>Item name, alias, or guid as text.</summary>
        [Field(3)] public string Item = "";
        [Field(4)] public int Amount;
    }

    [ModMessage("sv.tryit.row")]
    public sealed class EvalRowMsg
    {
        [Field(1)] public string Name = "";
        [Field(2)] public string Where = "";
        [Field(3)] public int Amount;
        [Field(4)] public string Why = "";
    }

    [ModMessage("sv.tryit.reply")]
    public sealed class TryItReply
    {
        [Field(1)] public string Item = "";
        [Field(2)] public List<EvalRowMsg> Steps = new();
        [Field(3)] public List<EvalRowMsg> NotUsed = new();
        [Field(4)] public int Short;
    }
}
