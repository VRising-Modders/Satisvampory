// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using Progenitor.Networking;

namespace Satisvampory.Net
{
    // Island-level screens (specs 03, 10, 14, 15, 07). The island is always the requester's,
    // resolved on the server from where they stand.

    [ModMessage("sv.line.error")]
    public sealed class LineErrorMsg
    {
        [Field(1)] public int Line;
        [Field(2)] public string Text = "";
        [Field(3)] public bool IsError;
    }

    // ---------------- Castle ----------------

    [ModMessage("sv.castle")]
    public sealed class CastleMsg
    {
        [Field(1)] public string PlotLabel = "";
        [Field(2)] public int Level;
        [Field(3)] public string Owner = "";
        /// <summary>"no sharing", "castle share", "clan share".</summary>
        [Field(4)] public string Sharing = "";
        [Field(5)] public bool IsOwner;
        [Field(6)] public bool CanEdit;
        [Field(7)] public bool LogisticsOn = true;
        [Field(8)] public int DefaultReserve = 10;
        [Field(9)] public bool FeedSalvage;
        /// <summary>Unused since heart feed became a server setting; kept so the wire layout holds.</summary>
        [Field(10)] public bool HeartAutoFeed = true;
        [Field(11)] public bool RepeatHunts = true;
        [Field(12)] public bool Exclude;
        /// <summary>Top station inputs short on the island: "Iron Ore · wants 50 · spare 30".</summary>
        [Field(13)] public List<string> Need = new();
        [Field(14)] public string TidyResult = "";
        /// <summary>The island's item aliases: "rb = Ruby; ior = Iron Ore".</summary>
        [Field(15)] public string Aliases = "";
        /// <summary>Clan page: one line per castle.</summary>
        [Field(16)] public List<string> Clan = new();
        /// <summary>Request only: turn repeat hunts on for every clan castle.</summary>
        [Field(17)] public bool RepeatAllOn;
    }

    [ModMessage("sv.castle.get")]
    public sealed class GetCastleReq
    {
        [Field(1)] public int Unused;
    }

    [ModMessage("sv.castle.set")]
    public sealed class SetCastleReq
    {
        [Field(1)] public CastleMsg Castle = new();
    }

    [ModMessage("sv.castle.tidy")]
    public sealed class TidyReq
    {
        [Field(1)] public int Unused;
    }

    // ---------------- Mass Deposit ----------------

    [ModMessage("sv.override")]
    public sealed class OverrideMsg
    {
        /// <summary>"any", an item name, or "category &lt;name&gt;".</summary>
        [Field(1)] public string Items = "";
        /// <summary>"belt", "chest", "plot", "overflow", "general", "special".</summary>
        [Field(2)] public string TargetKind = "";
        [Field(3)] public string TargetName = "";
        [Field(4)] public string TargetPlot = "";
    }

    [ModMessage("sv.md")]
    public sealed class MassDepositMsg
    {
        [Field(1)] public List<OverrideMsg> Overrides = new();
        [Field(2)] public string Script = "";
        [Field(3)] public List<LineErrorMsg> Messages = new();
        /// <summary>Set when the stored script failed after a belt change; the last good one keeps running.</summary>
        [Field(4)] public string NeedsReview = "";
        [Field(5)] public bool CanEdit;
        /// <summary>Overview: "Iron Ore → Ore bin · here (Category ore +3 on base 0), Ore 2 · Plot 2 …".</summary>
        [Field(6)] public List<string> Overview = new();
        /// <summary>Overview table: every item and category with a home, its homes in order and each chest's priority.</summary>
        [Field(7)] public List<HomeRowMsg> Homes = new();
    }

    [ModMessage("sv.md.home")]
    public sealed class HomeRowMsg
    {
        /// <summary>"i:&lt;guid&gt;" for an item, "c:&lt;name&gt;" for a category.</summary>
        [Field(1)] public string Key = "";
        [Field(2)] public string Label = "";
        [Field(3)] public string ContainerId = "";
        [Field(4)] public string Chest = "";
        [Field(5)] public string Where = "";
        [Field(6)] public int Priority;
        [Field(7)] public string Why = "";
        [Field(8)] public int NetIndex;
        [Field(9)] public int NetGeneration;
        [Field(10)] public int NetType;
    }

    /// <summary>Overview edit: set one chest's priority for one item or category (written into that chest's settings).</summary>
    [ModMessage("sv.md.home.set")]
    public sealed class SetHomePriorityReq
    {
        [Field(1)] public string ContainerId = "";
        [Field(2)] public string Key = "";
        [Field(3)] public int Priority;
    }

    [ModMessage("sv.md.get")]
    public sealed class GetMassDepositReq
    {
        [Field(1)] public int Unused;
    }

    [ModMessage("sv.md.set")]
    public sealed class SetMassDepositReq
    {
        [Field(1)] public List<OverrideMsg> Overrides = new();
        [Field(2)] public string Script = "";
        [Field(3)] public bool SaveOverrides;
        [Field(4)] public bool SaveScript;
    }

    /// <summary>Island-level Try it: push or pull n of X across the island.</summary>
    [ModMessage("sv.md.try")]
    public sealed class IslandTryReq
    {
        [Field(1)] public string Item = "";
        [Field(2)] public int Amount;
        /// <summary>0 push, 1 pull.</summary>
        [Field(3)] public int Direction;
    }

    // ---------------- Belts and categories ----------------

    [ModMessage("sv.belt")]
    public sealed class BeltMsg
    {
        [Field(1)] public string Id = "";
        [Field(2)] public string Name = "";
        [Field(3)] public List<RowMsg> Members = new();
        [Field(4)] public List<string> UsedBy = new();
        /// <summary>Plot labels the belt spans.</summary>
        [Field(5)] public List<string> Plots = new();
        [Field(6)] public List<RowMsg> Suppliers = new();
        [Field(7)] public List<RowMsg> Receivers = new();
        [Field(8)] public bool BuiltIn;
    }

    [ModMessage("sv.category")]
    public sealed class CategoryMsg
    {
        [Field(1)] public string Name = "";
        /// <summary>"Built-in", "Edited", "Custom".</summary>
        [Field(2)] public string Kind = "";
        [Field(3)] public List<string> Members = new();
        /// <summary>Item guids, same order as <see cref="Members"/>.</summary>
        [Field(4)] public List<int> Items = new();
        /// <summary>Island stock per item, same order.</summary>
        [Field(5)] public List<long> Stock = new();
        [Field(6)] public List<string> UsedBy = new();
    }

    [ModMessage("sv.belts")]
    public sealed class BeltsMsg
    {
        [Field(1)] public List<BeltMsg> Belts = new();
        [Field(2)] public List<CategoryMsg> Categories = new();
        [Field(3)] public bool CanEdit;
        /// <summary>Add by name preview: the belt it is for, and what would be added.</summary>
        [Field(4)] public string PreviewFor = "";
        [Field(5)] public List<string> Preview = new();
        [Field(6)] public int PreviewDirection;
        [Field(7)] public string PreviewText = "";
    }

    [ModMessage("sv.belts.get")]
    public sealed class GetBeltsReq
    {
        [Field(1)] public int Unused;
    }

    [ModMessage("sv.belts.edit")]
    public sealed class BeltEditReq
    {
        /// <summary>0 create, 1 rename, 2 delete, 3 add members by name, 4 remove member, 5 move member, 6 duplicate,
        /// 7 add the member picked in the world (Pick* fields), 8 preview add by name,
        /// 14 rename a custom category (Name to Text), 15 duplicate a category (Name to Text),
        /// 10 category create/replace members, 11 category add item, 12 category remove item, 13 category restore/delete.</summary>
        [Field(1)] public int Op;
        [Field(2)] public string BeltId = "";
        [Field(3)] public string Name = "";
        [Field(4)] public string Text = "";
        [Field(5)] public int Index;
        [Field(6)] public int To;
        [Field(7)] public int PickIndex;
        [Field(8)] public int PickGeneration;
        [Field(9)] public int PickType;
        /// <summary>0 supplier (Output), 1 receiver (Input).</summary>
        [Field(10)] public int Direction;
        [Field(11)] public string MemberId = "";
    }

    // ---------------- Inventory ----------------

    [ModMessage("sv.inv.row")]
    public sealed class InventoryRowMsg
    {
        [Field(1)] public int Item;
        [Field(2)] public string Name = "";
        [Field(3)] public string Categories = "";
        [Field(4)] public long Total;
        [Field(5)] public long Here;
        [Field(6)] public int Chests;
        [Field(7)] public long InStations;
        [Field(8)] public int Reserve;
        /// <summary>0 default, 1 category, 2 item override.</summary>
        [Field(9)] public int ReserveFrom;
        [Field(10)] public long Spare;
        /// <summary>-1 unlimited.</summary>
        [Field(11)] public int Cap = -1;
        [Field(12)] public int CapFrom;
        [Field(13)] public long Demand;
        [Field(14)] public string Status = "";
        /// <summary>Per-plot breakdown lines: "Hollow Keep · 320 in 3 chests · most in Ore box (200)".</summary>
        [Field(15)] public List<string> Plots = new();
        /// <summary>Plot labels holding any, for the plot filter.</summary>
        [Field(16)] public List<string> PlotLabels = new();
    }

    [ModMessage("sv.inv")]
    public sealed class InventoryMsg
    {
        [Field(1)] public List<InventoryRowMsg> Rows = new();
        [Field(2)] public string Island = "";
        [Field(3)] public bool CanEdit;
        /// <summary>The player's remembered table sort: "column:a" / "column:d", comma separated.</summary>
        [Field(4)] public string Sort = "";
        /// <summary>The player's hidden columns, comma separated.</summary>
        [Field(5)] public string Hidden = "";
    }

    /// <summary>Live inventory (spec 15): the rows that changed or went away since the last snapshot or delta.</summary>
    [ModMessage("sv.inv.delta")]
    public sealed class InventoryDeltaMsg
    {
        [Field(1)] public List<InventoryRowMsg> Changed = new();
        [Field(2)] public List<int> Removed = new();
    }

    /// <summary>Remember a table view for this player (sort keys and hidden columns).</summary>
    [ModMessage("sv.view.set")]
    public sealed class SetViewReq
    {
        [Field(1)] public string View = "";
        [Field(2)] public string Sort = "";
        [Field(3)] public string Hidden = "";
    }

    [ModMessage("sv.view.ok")]
    public sealed class ViewSaved
    {
        [Field(1)] public bool Ok;
    }

    [ModMessage("sv.inv.get")]
    public sealed class GetInventoryReq
    {
        /// <summary>Column index of the engine's InventoryColumn.</summary>
        [Field(1)] public int SortColumn;
        [Field(2)] public bool Descending;
        [Field(3)] public string Search = "";
        [Field(4)] public bool OnlyShort;
    }

    [ModMessage("sv.inv.rule")]
    public sealed class SetRuleReq
    {
        /// <summary>Item name/alias/guid, or "category &lt;name&gt;".</summary>
        [Field(1)] public string Target = "";
        /// <summary>0 reserve, 1 cap.</summary>
        [Field(2)] public int Rule;
        /// <summary>-1 clears the override.</summary>
        [Field(3)] public int Value;
    }

    // ---------------- Find and pull ----------------

    [ModMessage("sv.find")]
    public sealed class FindReq
    {
        [Field(1)] public string Text = "";
        [Field(2)] public bool IncludeClanPlots;
        [Field(3)] public bool PartialMatch = true;
        [Field(4)] public int Kind;
        /// <summary>Items whose game display name (known only on the client) matches the text, best first.</summary>
        [Field(5)] public List<int> ClientItems = new();
    }

    [ModMessage("sv.found")]
    public sealed class FoundMsg
    {
        [Field(1)] public string Name = "";
        [Field(2)] public long Total;
        [Field(3)] public List<EvalRowMsg> Chests = new();
        [Field(4)] public int Item;
        [Field(5)] public int MatchRank;
    }

    [ModMessage("sv.find.reply")]
    public sealed class FindReply
    {
        [Field(1)] public List<FoundMsg> Items = new();
        [Field(2)] public List<EvalRowMsg> ChestsByName = new();
        [Field(3)] public List<string> Categories = new();
        [Field(4)] public bool Ambiguous;
    }

    [ModMessage("sv.find.preview")]
    public sealed class PreviewReq
    {
        [Field(1)] public string ContainerId = "";
        [Field(2)] public bool IncludeClanPlots;
    }

    [ModMessage("sv.find.preview.reply")]
    public sealed class PreviewReply
    {
        [Field(1)] public string Token = "";
        [Field(2)] public float X;
        [Field(3)] public float Y;
        [Field(4)] public float Z;
        [Field(5)] public float Yaw;
        [Field(6)] public int FloorHeight = -1;
        /// <summary>What the chest holds (a station: input and output together), item guids and amounts in step.</summary>
        [Field(7)] public List<int> Items = new();
        [Field(8)] public List<int> Amounts = new();
        [Field(9)] public string Name = "";
        /// <summary>A plain chest (not a station): the game's own window can show it remotely.</summary>
        [Field(10)] public bool Chest;
    }

    /// <summary>Island numbers for a hovered recipe: its product and each requirement.</summary>
    [ModMessage("sv.recipe.info")]
    public sealed class RecipeInfoReq
    {
        /// <summary>The station (none for the character crafting tab).</summary>
        [Field(1)] public TargetMsg Target = new();
        /// <summary>The product first, then the requirements.</summary>
        [Field(2)] public List<int> Items = new();
    }

    [ModMessage("sv.recipe.info.reply")]
    public sealed class RecipeInfoReply
    {
        /// <summary>In step with the request's items.</summary>
        [Field(1)] public List<long> Island = new();
        /// <summary>What the station's Inputs can reach; -1 without a station.</summary>
        [Field(2)] public List<long> Reachable = new();
        [Field(4)] public List<int> Reserve = new();
        /// <summary>-1: no cap.</summary>
        [Field(5)] public List<int> Cap = new();
        /// <summary>"this castle", "your castles", "the clan's castles".</summary>
        [Field(6)] public string Scope = "";
    }

    [ModMessage("sv.find.preview.end")]
    public sealed class EndPreviewReq
    {
        [Field(1)] public string Token = "";
        /// <summary>Keep the preview's view of the room open (Keep open) instead of ending it.</summary>
        [Field(2)] public bool Hold;
    }

    [ModMessage("sv.pull")]
    public sealed class PullReq
    {
        /// <summary>Item name/alias/guid, or "category &lt;name&gt;" for one stack (or Amount) of each.</summary>
        [Field(1)] public string Text = "";
        [Field(2)] public int Amount;
    }

    /// <summary>Inventory tooltip line (spec 07, "Find"): how many of an item the island stores.</summary>
    [ModMessage("sv.storage.count")]
    public sealed class StorageCountReq
    {
        [Field(1)] public int Item;
    }

    [ModMessage("sv.storage.count.reply")]
    public sealed class StorageCountReply
    {
        [Field(1)] public long Total;
        [Field(2)] public int Chests;
        [Field(3)] public int Reserve;
        /// <summary>-1: no cap.</summary>
        [Field(4)] public int Cap = -1;
    }

    /// <summary>The Log tab: what Satisvampory moved and who changed what, for the reader's island.</summary>
    [ModMessage("sv.log.get")]
    public sealed class LogReq
    {
        /// <summary>A chest or station (either end of a move, or the one changed); empty for all.</summary>
        [Field(1)] public string ContainerId = "";
        /// <summary>Item name/alias, or "category &lt;name&gt;"; empty for all.</summary>
        [Field(2)] public string Item = "";
        /// <summary>Items whose game name (known only on the client) matches <see cref="Item"/>.</summary>
        [Field(3)] public List<int> ClientItems = new();
        [Field(4)] public string Player = "";
        /// <summary>0 moves and changes, 1 moves only, 2 changes only.</summary>
        [Field(5)] public int Kind;
        /// <summary>1 to 24.</summary>
        [Field(6)] public int Hours = 24;
    }

    [ModMessage("sv.log.row")]
    public sealed class LogRowMsg
    {
        /// <summary>Unix seconds (UTC) of the last move in the entry.</summary>
        [Field(1)] public long At;
        [Field(2)] public int Item;
        [Field(3)] public int Amount;
        [Field(4)] public int Moves;
        [Field(5)] public string From = "";
        [Field(6)] public string FromName = "";
        [Field(7)] public string FromPlot = "";
        [Field(8)] public string To = "";
        [Field(9)] public string ToName = "";
        [Field(10)] public string ToPlot = "";
        [Field(11)] public string Why = "";
        /// <summary>A station feed's product.</summary>
        [Field(12)] public int For;
        [Field(13)] public string Actor = "";
        /// <summary>A settings change; empty for a move.</summary>
        [Field(14)] public string Detail = "";
    }

    [ModMessage("sv.log")]
    public sealed class LogReply
    {
        [Field(1)] public List<LogRowMsg> Rows = new();
    }

    [ModMessage("sv.huntlog.get")]
    public sealed class GetHuntLogReq
    {
        [Field(1)] public int Unused;
    }

    [ModMessage("sv.huntlog")]
    public sealed class HuntLogReply
    {
        [Field(1)] public List<string> Lines = new();
        [Field(2)] public List<string> Servants = new();
    }

    [ModMessage("sv.text")]
    public sealed class TextReply
    {
        [Field(1)] public string Text = "";
    }

    /// <summary>Admin tools on the Server page; the server checks admin rights.</summary>
    [ModMessage("sv.admin")]
    public sealed class AdminReq
    {
        /// <summary>1 empty all trash on this plot, 2 give to storage, 3 diagnostics.</summary>
        [Field(1)] public int Op;
        [Field(2)] public string Item = "";
        [Field(3)] public int Amount;
    }
}
