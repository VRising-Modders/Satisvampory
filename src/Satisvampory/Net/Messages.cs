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
        /// <summary>Typed form ("ore, iron ingot, -copper ore"); when set, the server resolves it and ignores the lists.</summary>
        [Field(5)] public string Text = "";
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
        /// <summary>A pick-in-world result: the picked building's network id words, resolved on the server.</summary>
        [Field(10)] public int PickIndex;
        [Field(11)] public int PickGeneration;
        [Field(12)] public int PickType;
        /// <summary>"Storage", "Refining", "Belt", "Mass Deposit"...: the Type column.</summary>
        [Field(13)] public string Type = "";
        [Field(14)] public int BuildingPrefab;
        /// <summary>Added by Satisvampory (the Default Belt Input, a factory's products-to-storage Output), not by a player.</summary>
        [Field(15)] public bool Auto;
        /// <summary>
        /// A link set on the other end (its Output names this as a destination, or its Input as a source): that
        /// container's id, the side it is set on (0 Input, 1 Output) and its index there. Empty for this container's own.
        /// </summary>
        [Field(16)] public string LinkOwner = "";
        [Field(17)] public int LinkSide;
        [Field(18)] public int LinkIndex;
    }

    [ModMessage("sv.priority")]
    public sealed class PriorityMsg
    {
        /// <summary>Item guid, or 0 for a category entry.</summary>
        [Field(1)] public int Item;
        [Field(2)] public string Category = "";
        [Field(3)] public string Label = "";
        /// <summary>Priority 1 to 99 (higher wins); unused on a block.</summary>
        [Field(4)] public int Adjust;
        /// <summary>A category's icon: its most-stored item (0: none).</summary>
        [Field(5)] public int Icon;
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
        /// <summary>Belt ids, same order as <see cref="Belts"/>.</summary>
        [Field(7)] public List<string> BeltIds = new();
        /// <summary>Out of every shared island use; its Input and Output do not run.</summary>
        [Field(8)] public bool Private;
        /// <summary>A password is set (asked while the chest is private). The password itself never leaves the server.</summary>
        [Field(9)] public bool HasPassword;
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
        /// <summary>The loop this container is in, for changing its mode; empty when none.</summary>
        [Field(15)] public string LoopKey = "";
        /// <summary>0 cycle once, 1 even distribution, 2 off.</summary>
        [Field(16)] public int LoopMode;
        [Field(17)] public List<RecipeMsg> Recipes = new();
        /// <summary>One-shot notice, e.g. "2 entries removed: chest destroyed" (spec 12).</summary>
        [Field(18)] public string Notice = "";
        /// <summary>Links another container set up that reach this one (spec 02, "set on A"): into this one, and out of it.</summary>
        [Field(19)] public List<string> InputFromOthers = new();
        [Field(20)] public List<string> OutputFromOthers = new();
        [Field(21)] public string NativeRestriction = "";
    }

    [ModMessage("sv.recipe")]
    public sealed class RecipeMsg
    {
        [Field(1)] public int Guid;
        [Field(2)] public string Name = "";
        [Field(3)] public string Inputs = "";
        [Field(4)] public bool On;
    }

    [ModMessage("sv.recipes.set")]
    public sealed class SetRecipesReq
    {
        [Field(1)] public TargetMsg Target = new();
        /// <summary>Every recipe guid that should be on; all others are switched off.</summary>
        [Field(2)] public List<int> On = new();
    }

    [ModMessage("sv.trash.empty")]
    public sealed class EmptyTrashReq
    {
        [Field(1)] public TargetMsg Target = new();
    }

    /// <summary>Set, change or remove a private chest's password. Current is needed when one is set.</summary>
    [ModMessage("sv.lock.set")]
    public sealed class SetPasswordReq
    {
        [Field(1)] public TargetMsg Target = new();
        [Field(2)] public string Current = "";
        /// <summary>Empty: remove the password.</summary>
        [Field(3)] public string New = "";
    }

    [ModMessage("sv.loop.set")]
    public sealed class SetLoopReq
    {
        [Field(1)] public TargetMsg Target = new();
        [Field(2)] public string LoopKey = "";
        [Field(3)] public int Mode;
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
        /// <summary>0 add, 1 remove, 2 move, 3 set orders, 4 set filter, 5 put this container on a belt
        /// (Entry.BeltId, or a new belt named Entry.Name), 6 take it off belt Entry.BeltId.</summary>
        [Field(3)] public int Op;
        [Field(4)] public int Index;
        [Field(5)] public int To;
        [Field(6)] public RowMsg Entry = new();
        [Field(7)] public int PullOrder;
        [Field(8)] public int SendOrder;
        /// <summary>Op 7: remove the entry at Index on Side of this other container (a link shown from this end).</summary>
        [Field(9)] public string LinkOwner = "";
    }

    [ModMessage("sv.deposit.set")]
    public sealed class SetDepositReq
    {
        [Field(1)] public TargetMsg Target = new();
        [Field(2)] public DepositMsg Deposit = new();
    }

    /// <summary>The Filter tab's Add picker: every item and category, with how many the island stores.</summary>
    [ModMessage("sv.filter.picker")]
    public sealed class FilterPickerReq
    {
        [Field(1)] public TargetMsg Target = new();
    }

    [ModMessage("sv.filter.picker.reply")]
    public sealed class FilterPickerReply
    {
        [Field(1)] public List<int> Items = new();
        /// <summary>In step with <see cref="Items"/>.</summary>
        [Field(2)] public List<long> ItemCounts = new();
        [Field(3)] public List<string> Categories = new();
        /// <summary>In step with <see cref="Categories"/>.</summary>
        [Field(4)] public List<long> CategoryCounts = new();
        /// <summary>In step with <see cref="Categories"/>: the icon item (the most stored member, else the first).</summary>
        [Field(5)] public List<int> CategoryIcons = new();
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
        /// <summary>"chest", "station", "special", "belt" or "md": the Add list's Show filter.</summary>
        [Field(7)] public string Kind = "";
        [Field(8)] public string Plot = "";
        /// <summary>Metres from the player on the same plot; -1 when not known.</summary>
        [Field(9)] public float Distance = -1;
        [Field(10)] public string Hint = "";
        [Field(11)] public int BuildingPrefab;
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
        /// <summary>Network id words for Locate; 0 when unknown.</summary>
        [Field(5)] public int NetIndex;
        [Field(6)] public int NetGeneration;
        [Field(7)] public int NetType;
        /// <summary>Find: the plot's heart level, and what a chest found by name holds.</summary>
        [Field(8)] public int Level;
        [Field(9)] public string Holds = "";
        [Field(10)] public string ContainerId = "";
        [Field(11)] public bool HasPosition;
        [Field(12)] public float X;
        [Field(13)] public float Y;
        [Field(14)] public float Z;
        [Field(15)] public float Yaw;
        /// <summary>Find: the chest's or station's building prefab, for its icon; 0 when unknown.</summary>
        [Field(16)] public int BuildingPrefab;
        /// <summary>"Storage", "Crafting", "Refining", "Unit station", "Brazier", "Castle heart"...: what the place is.</summary>
        [Field(17)] public string Type = "";
    }

    [ModMessage("sv.tryit.reply")]
    public sealed class TryItReply
    {
        [Field(1)] public string Item = "";
        [Field(2)] public List<EvalRowMsg> Steps = new();
        [Field(3)] public List<EvalRowMsg> NotUsed = new();
        [Field(4)] public int Short;
    }

    /// <summary>Take an item from one list row's chest or belt into bags (spec 07, "Take…").</summary>
    [ModMessage("sv.take.row")]
    public sealed class TakeFromRowReq
    {
        [Field(1)] public TargetMsg Target = new();
        [Field(2)] public int Side;
        [Field(3)] public int Index;
        [Field(4)] public string Item = "";
        [Field(5)] public int Amount;
    }
}
