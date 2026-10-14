// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Edits;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class CategoryOpsTests
    {
        [Fact]
        public void Renaming_a_custom_category_follows_every_reference()
        {
            var cat = Catalog();
            cat.SealBuiltIns();
            var st = new IslandState();
            st.Categories.Custom["shiny"] = new List<ItemId> { Ruby };
            var chest = ContainerId.New();
            var entry = ListEntry.Of(ContainerId.New());
            entry.Filter.Categories.Add("shiny");
            st.Graph.For(chest).Input.Add(entry);
            var d = new DepositOptions();
            d.CategoryPriority["shiny"] = 3;
            d.BannedCategories.Add("shiny");
            st.Deposit[chest] = d;
            st.MassDeposit.ItemRules.Add((new ItemSelector(ItemSelectorKind.Category, Category: "shiny"), new Target(TargetKind.Overflow)));
            st.MassDeposit.AdvancedScript = "for category shiny first overflow\nfor category \"shinyish\" first overflow";
            st.Rules.CategoryReserve["shiny"] = 5;
            st.Rules.CategoryCap["shiny"] = 50;

            Assert.Equal(7, CategoryOps.Usages(st, "Shiny", _ => "x").Count);
            Assert.True(CategoryOps.Rename(st, cat, "shiny", "gems of note").Ok);

            Assert.True(st.Categories.Custom.ContainsKey("gems of note"));
            Assert.Contains("gems of note", entry.Filter.Categories);
            Assert.Equal(3, d.CategoryPriority["gems of note"]);
            Assert.Contains("gems of note", d.BannedCategories);
            Assert.Equal("gems of note", st.MassDeposit.ItemRules[0].Selector.Category);
            Assert.StartsWith("for category \"gems of note\" first", st.MassDeposit.AdvancedScript);
            Assert.Contains("\"shinyish\"", st.MassDeposit.AdvancedScript);
            Assert.Equal(5, st.Rules.CategoryReserve["gems of note"]);
            Assert.Contains(chest, st.Touched);
            Assert.Empty(CategoryOps.Usages(st, "shiny", _ => "x"));
        }

        [Fact]
        public void Built_ins_cannot_be_renamed_and_names_stay_unique()
        {
            var cat = Catalog();
            cat.SealBuiltIns();
            var st = new IslandState();
            st.Categories.Custom["mine"] = new List<ItemId>();
            Assert.False(CategoryOps.Rename(st, cat, "ore", "rocks").Ok);
            Assert.False(CategoryOps.Rename(st, cat, "mine", "ore").Ok);
            Assert.True(CategoryOps.Duplicate(st, cat.WithEdits(st.Categories), "ore", "my ore").Ok);
            Assert.Equal(cat.Members("ore").Count, st.Categories.Custom["my ore"].Count);
        }
    }
}
