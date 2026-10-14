// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Persistence
{
    // Plain records for disk. Ids are strings, item ids are prefab guids, so the files are
    // readable and stable across engine refactors. The plugin stores these through Progenitor
    // persistence (spec 12); the engine only maps them to and from its own types.

    public sealed class EntryDto
    {
        public string Kind { get; set; } = "container";
        public string? Container { get; set; }
        public string? Belt { get; set; }
        public FilterDto? Filter { get; set; }
        public long Sequence { get; set; }
    }

    public sealed class FilterDto
    {
        public List<int> Items { get; set; } = new();
        public List<string> Categories { get; set; } = new();
        public List<int> ExcludeItems { get; set; } = new();
        public List<string> ExcludeCategories { get; set; } = new();
    }

    public sealed class ContainerDto
    {
        public List<EntryDto> Input { get; set; } = new();
        public List<EntryDto> Output { get; set; } = new();
        public string PullOrder { get; set; } = nameof(Lists.PullOrder.ListOrder);
        public string SendOrder { get; set; } = nameof(Lists.SendOrder.FillInOrder);
        public string Takes { get; set; } = nameof(Model.Takes.Anything);
        public int BasePriority { get; set; }
        public Dictionary<int, int> ItemPriority { get; set; } = new();
        public Dictionary<string, int> CategoryPriority { get; set; } = new();
        public List<int> BannedItems { get; set; } = new();
        public List<string> BannedCategories { get; set; } = new();
        public string Special { get; set; } = nameof(SpecialKind.None);
    }

    public sealed class BeltDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> Members { get; set; } = new();
    }

    public sealed class TargetDto
    {
        public string Kind { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Plot { get; set; }
        public string Special { get; set; } = nameof(SpecialKind.None);
    }

    public sealed class ItemRuleDto
    {
        public string Selector { get; set; } = "any";
        public int Item { get; set; }
        public string Category { get; set; } = string.Empty;
        public TargetDto Target { get; set; } = new();
    }

    public sealed class RulesDto
    {
        public int DefaultReserve { get; set; } = 10;
        public Dictionary<int, int> ItemReserve { get; set; } = new();
        public Dictionary<string, int> CategoryReserve { get; set; } = new();
        public Dictionary<int, int> ItemCap { get; set; } = new();
        public Dictionary<string, int> CategoryCap { get; set; } = new();
        public bool LogisticsOn { get; set; } = true;
    }

    /// <summary>Everything one island's settings owner holds (spec 12): belts, Mass Deposit, rules, lists, loop modes.</summary>
    public sealed class IslandSave
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public long NextSequence { get; set; }
        public List<BeltDto> Belts { get; set; } = new();
        public List<ItemRuleDto> MassDepositItemRules { get; set; } = new();
        public string AdvancedScript { get; set; } = string.Empty;
        public RulesDto Rules { get; set; } = new();
        public Dictionary<string, ContainerDto> Containers { get; set; } = new();
        public Dictionary<string, string> LoopModes { get; set; } = new();
        public Dictionary<string, List<int>> EditedCategories { get; set; } = new();
        public Dictionary<string, List<int>> CustomCategories { get; set; } = new();
        public Dictionary<string, int> Aliases { get; set; } = new();

        /// <summary>Crafting-table recipe switches (spec 04): container id → recipe guids switched on.</summary>
        public Dictionary<string, List<int>> TableRecipesOn { get; set; } = new();
    }

    /// <summary>The engine objects an <see cref="IslandSave"/> becomes.</summary>
    public sealed class IslandState
    {
        public LogisticsGraph Graph { get; init; } = new();
        public MassDepositSettings MassDeposit { get; init; } = new();
        public CastleRules Rules { get; init; } = new();
        /// <summary>Deposit options per container, applied onto ContainerInfo by the adapter.</summary>
        public Dictionary<ContainerId, DepositOptions> Deposit { get; init; } = new();
        public Dictionary<string, LoopMode> LoopModes { get; init; } = new();
        public CategoryEdits Categories { get; init; } = new();

        /// <summary>Crafting tables have no game switch per recipe; these are Satisvampory's (default off).</summary>
        public Dictionary<ContainerId, HashSet<int>> TableRecipesOn { get; init; } = new();
    }

    public static class SaveCodec
    {
        static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string ToJson(IslandSave save) => JsonSerializer.Serialize(save, Json);

        /// <summary>Reads a save; a file from a newer version is refused rather than half-read.</summary>
        public static IslandSave FromJson(string json)
        {
            var save = JsonSerializer.Deserialize<IslandSave>(json, Json) ?? new IslandSave();
            if (save.Version > IslandSave.CurrentVersion)
                throw new InvalidOperationException($"Save version {save.Version} is newer than this build ({IslandSave.CurrentVersion})");
            return save;
        }

        // ---------------- engine → save ----------------

        public static IslandSave Capture(IslandState s, long nextSequence)
        {
            var save = new IslandSave
            {
                NextSequence = nextSequence,
                AdvancedScript = s.MassDeposit.AdvancedScript,
                Rules = new RulesDto
                {
                    DefaultReserve = s.Rules.DefaultReserve,
                    ItemReserve = s.Rules.ItemReserve.ToDictionary(k => k.Key.Guid, v => v.Value),
                    CategoryReserve = new(s.Rules.CategoryReserve),
                    ItemCap = s.Rules.ItemCap.ToDictionary(k => k.Key.Guid, v => v.Value),
                    CategoryCap = new(s.Rules.CategoryCap),
                    LogisticsOn = s.Rules.LogisticsOn,
                },
                LoopModes = s.LoopModes.ToDictionary(k => k.Key, v => v.Value.ToString()),
                EditedCategories = s.Categories.Edited.ToDictionary(k => k.Key, v => v.Value.Select(i => i.Guid).ToList()),
                CustomCategories = s.Categories.Custom.ToDictionary(k => k.Key, v => v.Value.Select(i => i.Guid).ToList()),
                Aliases = s.Categories.Aliases.ToDictionary(k => k.Key, v => v.Value.Guid),
                TableRecipesOn = s.TableRecipesOn.Where(kv => kv.Value.Count > 0).ToDictionary(k => Id(k.Key), v => v.Value.OrderBy(x => x).ToList()),
            };

            foreach (var b in s.Graph.Belts.Values)
                save.Belts.Add(new BeltDto { Id = b.Id.Value.ToString("N"), Name = b.Name, Members = b.Members.Select(Id).ToList() });

            foreach (var (sel, t) in s.MassDeposit.ItemRules)
                save.MassDepositItemRules.Add(new ItemRuleDto { Selector = sel.Kind.ToString(), Item = sel.Item.Guid, Category = sel.Category, Target = ToDto(t) });

            var ids = s.Graph.Lists.Keys.Concat(s.Deposit.Keys).Distinct();
            foreach (var id in ids)
            {
                var dto = new ContainerDto();
                if (s.Graph.Lists.TryGetValue(id, out var lists))
                {
                    dto.Input = lists.Input.Select(ToDto).ToList();
                    dto.Output = lists.Output.Select(ToDto).ToList();
                    dto.PullOrder = lists.PullOrder.ToString();
                    dto.SendOrder = lists.SendOrder.ToString();
                }
                if (s.Deposit.TryGetValue(id, out var d))
                {
                    dto.Takes = d.Takes.ToString();
                    dto.BasePriority = d.BasePriority;
                    dto.ItemPriority = d.ItemPriority.ToDictionary(k => k.Key.Guid, v => v.Value);
                    dto.CategoryPriority = new(d.CategoryPriority);
                    dto.BannedItems = d.BannedItems.Select(i => i.Guid).ToList();
                    dto.BannedCategories = d.BannedCategories.ToList();
                    dto.Special = d.Special.ToString();
                }
                save.Containers[Id(id)] = dto;
            }
            return save;
        }

        // ---------------- save → engine ----------------

        /// <summary>Rebuilds engine state. Unknown enum names fall back to defaults rather than failing the whole load.</summary>
        public static IslandState Restore(IslandSave save, out long nextSequence)
        {
            nextSequence = save.NextSequence;
            var state = new IslandState();
            state.Graph.Sequence = save.NextSequence;

            foreach (var b in save.Belts)
            {
                if (!Guid.TryParse(b.Id, out var g)) continue;
                var belt = new Belt(new BeltId(g), b.Name);
                belt.Members.AddRange(b.Members.Select(ParseId).Where(x => x != null).Select(x => x!.Value));
                state.Graph.Belts[belt.Id] = belt;
            }

            foreach (var ir in save.MassDepositItemRules)
            {
                var kind = Enum.TryParse<ItemSelectorKind>(ir.Selector, out var k) ? k : ItemSelectorKind.Any;
                state.MassDeposit.ItemRules.Add((new ItemSelector(kind, new ItemId(ir.Item), ir.Category), FromDto(ir.Target)));
            }
            state.MassDeposit.AdvancedScript = save.AdvancedScript ?? string.Empty;

            var rules = state.Rules;
            rules.DefaultReserve = save.Rules.DefaultReserve;
            rules.LogisticsOn = save.Rules.LogisticsOn;
            foreach (var kv in save.Rules.ItemReserve) rules.ItemReserve[new ItemId(kv.Key)] = kv.Value;
            foreach (var kv in save.Rules.CategoryReserve) rules.CategoryReserve[kv.Key] = kv.Value;
            foreach (var kv in save.Rules.ItemCap) rules.ItemCap[new ItemId(kv.Key)] = kv.Value;
            foreach (var kv in save.Rules.CategoryCap) rules.CategoryCap[kv.Key] = kv.Value;

            foreach (var (key, dto) in save.Containers)
            {
                if (ParseId(key) is not { } id) continue;
                var lists = state.Graph.For(id);
                lists.Input.AddRange(dto.Input.Select(FromDto).Where(e => e != null).Select(e => e!));
                lists.Output.AddRange(dto.Output.Select(FromDto).Where(e => e != null).Select(e => e!));
                lists.PullOrder = Enum.TryParse<PullOrder>(dto.PullOrder, out var po) ? po : PullOrder.ListOrder;
                lists.SendOrder = Enum.TryParse<SendOrder>(dto.SendOrder, out var so) ? so : SendOrder.FillInOrder;

                var d = new DepositOptions
                {
                    BasePriority = dto.BasePriority,
                    Takes = Enum.TryParse<Takes>(dto.Takes, out var tk) ? tk : Takes.Anything,
                };
                foreach (var kv in dto.ItemPriority) d.ItemPriority[new ItemId(kv.Key)] = kv.Value;
                foreach (var kv in dto.CategoryPriority) d.CategoryPriority[kv.Key] = kv.Value;
                foreach (var i in dto.BannedItems) d.BannedItems.Add(new ItemId(i));
                foreach (var c in dto.BannedCategories) d.BannedCategories.Add(c);
                d.Special = Enum.TryParse<SpecialKind>(dto.Special, out var sk) ? sk : SpecialKind.None;
                state.Deposit[id] = d;
            }

            foreach (var (name, items) in save.EditedCategories) state.Categories.Edited[name] = items.Select(i => new ItemId(i)).ToList();
            foreach (var (name, items) in save.CustomCategories) state.Categories.Custom[name] = items.Select(i => new ItemId(i)).ToList();
            foreach (var (alias, item) in save.Aliases) state.Categories.Aliases[alias] = new ItemId(item);
            foreach (var (key, recipes) in save.TableRecipesOn ?? new Dictionary<string, List<int>>())
                if (ParseId(key) is { } table) state.TableRecipesOn[table] = new HashSet<int>(recipes);

            foreach (var (key, mode) in save.LoopModes)
                if (Enum.TryParse<LoopMode>(mode, out var m)) state.LoopModes[key] = m;

            return state;
        }

        // ---------------- helpers ----------------

        static string Id(ContainerId id) => id.Value.ToString("N");

        static ContainerId? ParseId(string s) => Guid.TryParse(s, out var g) ? new ContainerId(g) : null;

        static EntryDto ToDto(ListEntry e) => new()
        {
            Kind = e.Kind switch { EntryKind.Belt => "belt", EntryKind.MassDeposit => "massDeposit", _ => "container" },
            Container = e.Kind == EntryKind.Container ? Id(e.Container) : null,
            Belt = e.Kind == EntryKind.Belt ? e.Belt.Value.ToString("N") : null,
            Filter = e.Filter.IsAnything ? null : new FilterDto
            {
                Items = e.Filter.Items.Select(i => i.Guid).ToList(),
                Categories = e.Filter.Categories.ToList(),
                ExcludeItems = e.Filter.ExcludeItems.Select(i => i.Guid).ToList(),
                ExcludeCategories = e.Filter.ExcludeCategories.ToList(),
            },
            Sequence = e.Sequence,
        };

        static ListEntry? FromDto(EntryDto d)
        {
            var filter = new ItemFilter();
            if (d.Filter != null)
            {
                foreach (var i in d.Filter.Items) filter.Items.Add(new ItemId(i));
                foreach (var c in d.Filter.Categories) filter.Categories.Add(c);
                foreach (var i in d.Filter.ExcludeItems) filter.ExcludeItems.Add(new ItemId(i));
                foreach (var c in d.Filter.ExcludeCategories) filter.ExcludeCategories.Add(c);
            }
            switch (d.Kind)
            {
                case "massDeposit":
                    return new ListEntry { Kind = EntryKind.MassDeposit, Filter = filter, Sequence = d.Sequence };
                case "belt":
                    return Guid.TryParse(d.Belt, out var b) ? new ListEntry { Kind = EntryKind.Belt, Belt = new BeltId(b), Filter = filter, Sequence = d.Sequence } : null;
                default:
                    return ParseId(d.Container ?? string.Empty) is { } id ? new ListEntry { Kind = EntryKind.Container, Container = id, Filter = filter, Sequence = d.Sequence } : null;
            }
        }

        static TargetDto ToDto(Target t) => new() { Kind = t.Kind.ToString(), Name = t.Name, Plot = t.Plot, Special = t.Special.ToString() };

        static Target FromDto(TargetDto d) => new(
            Enum.TryParse<TargetKind>(d.Kind, out var k) ? k : TargetKind.General,
            d.Name,
            d.Plot,
            Enum.TryParse<SpecialKind>(d.Special, out var s) ? s : SpecialKind.None);
    }
}
