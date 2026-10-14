// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Satisvampory.Engine.Edits;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class EditServiceTests
    {
        static readonly PlotId Home = new(1), Outside = new(9);
        readonly FakeWorld _w = new();
        readonly IslandState _island = new();
        readonly EditService _svc;

        public EditServiceTests() => _svc = new EditService(_w, Catalog(), _island, () => new[] { Home });

        [Fact]
        public void Guests_cannot_edit_and_bad_targets_are_refused_with_reasons()
        {
            var a = _w.Chest(Home, "a");
            var skip = _w.Chest(Home, "s");
            skip.Deposit.Special = SpecialKind.Skip;
            var heart = _w.Chest(Home, "", kind: NodeKind.Heart);
            var far = _w.Chest(Outside, "far");

            Assert.Equal("Clan members can change logistics", _svc.AddEntry(ActorRole.Guest, a.Id, ListSide.Input, ListEntry.Of(skip.Id)).Reason);
            Assert.Equal("A container cannot list itself", _svc.AddEntry(ActorRole.ClanMember, a.Id, ListSide.Input, ListEntry.Of(a.Id)).Reason);
            Assert.Equal("Skip chests are never used", _svc.AddEntry(ActorRole.ClanMember, a.Id, ListSide.Input, ListEntry.Of(skip.Id)).Reason);
            Assert.Equal("Hearts only give items through their own Output", _svc.AddEntry(ActorRole.ClanMember, a.Id, ListSide.Input, ListEntry.Of(heart.Id)).Reason);
            Assert.Equal("Outside your sharing island", _svc.AddEntry(ActorRole.ClanMember, a.Id, ListSide.Input, ListEntry.Of(far.Id)).Reason);
            Assert.Contains("needs a filter", _svc.AddEntry(ActorRole.ClanMember, a.Id, ListSide.Input, ListEntry.MassDeposit()).Reason);
            Assert.Equal(0, _svc.Revision);
        }

        [Fact]
        public void Add_flags_a_loop_refuses_duplicates_and_moves_rows()
        {
            var a = _w.Chest(Home, "a");
            var b = _w.Chest(Home, "b");
            var c = _w.Chest(Home, "c");
            Assert.False(_svc.AddEntry(ActorRole.ClanMember, a.Id, ListSide.Output, ListEntry.Of(b.Id)).ClosesLoop);
            Assert.Equal("Already in this list", _svc.AddEntry(ActorRole.ClanMember, a.Id, ListSide.Output, ListEntry.Of(b.Id)).Reason);
            Assert.True(_svc.AddEntry(ActorRole.ClanMember, b.Id, ListSide.Output, ListEntry.Of(a.Id)).ClosesLoop);

            _svc.AddEntry(ActorRole.ClanMember, a.Id, ListSide.Output, ListEntry.Of(c.Id));
            Assert.True(_svc.MoveEntry(ActorRole.ClanMember, a.Id, ListSide.Output, 1, 0).Ok);
            Assert.Equal(c.Id, _island.Graph.For(a.Id).Output[0].Container);
            Assert.False(_svc.RemoveEntry(ActorRole.ClanMember, a.Id, ListSide.Output, 5).Ok);
            Assert.Equal(4, _svc.Revision);
        }

        [Fact]
        public void Deposit_options_apply_live_and_hearts_are_refused()
        {
            var a = _w.Chest(Home, "a");
            var heart = _w.Chest(Home, "", kind: NodeKind.Heart);
            Assert.True(_svc.SetDepositOptions(ActorRole.ClanMember, a.Id, d => { d.BasePriority = 99; d.BannedCategories.Add("ore"); }).Ok);
            Assert.Equal(DepositOptions.MaxPriority, a.Deposit.BasePriority);
            Assert.Contains("ore", a.Deposit.BannedCategories);
            Assert.False(_svc.SetDepositOptions(ActorRole.ClanMember, heart.Id, d => d.BasePriority = 1).Ok);
        }

        [Fact]
        public void Reserves_and_caps_validate_and_clear()
        {
            Assert.True(_svc.SetItemReserve(ActorRole.ClanMember, IronOre, 40).Ok);
            Assert.Equal(40, _island.Rules.ItemReserve[IronOre]);
            Assert.True(_svc.SetItemReserve(ActorRole.ClanMember, IronOre, null).Ok);
            Assert.False(_island.Rules.ItemReserve.ContainsKey(IronOre));
            Assert.Equal("Use 0 or more", _svc.SetItemCap(ActorRole.ClanMember, Ruby, -1).Reason);
            Assert.True(_svc.SetCategoryCap(ActorRole.ClanMember, "gem", 500).Ok);  // alias resolves to "gems"
            Assert.Equal(500, _island.Rules.CategoryCap["gems"]);
            Assert.False(_svc.SetCategoryReserve(ActorRole.ClanMember, "nonsense", 5).Ok);
        }

        [Fact]
        public void Script_errors_keep_the_last_good_script_and_belt_delete_marks_review()
        {
            _svc.CreateBelt(ActorRole.ClanMember, "Vault", out var belt);
            Assert.True(_svc.SaveScript(ActorRole.ClanMember, "for category gems: first belt \"Vault\"").Ok);
            var good = _svc.ActiveScript;

            var bad = _svc.SaveScript(ActorRole.ClanMember, "for category gems: first belt \"Nope\"");
            Assert.StartsWith("Line 1:", bad.Reason);
            Assert.Same(good, _svc.ActiveScript);

            Assert.True(_svc.DeleteBelt(ActorRole.ClanMember, belt!.Id, out var removed).Ok);
            Assert.Contains(removed, u => u.Kind == BeltUseKind.ScriptLine);
            Assert.NotNull(_svc.ScriptNeedsReview);
            Assert.Same(good, _svc.ActiveScript);
        }

        [Fact]
        public void Belt_members_must_be_on_the_island_and_not_hearts()
        {
            var a = _w.Chest(Home, "a");
            var heart = _w.Chest(Home, "", kind: NodeKind.Heart);
            var far = _w.Chest(Outside, "far");
            _svc.CreateBelt(ActorRole.ClanMember, "B", out var belt);
            Assert.True(_svc.AddBeltMembers(ActorRole.ClanMember, belt!.Id, new[] { a.Id, heart.Id, far.Id }).Ok);
            Assert.Equal(new[] { a.Id }, belt.Members);
            Assert.False(_svc.AddBeltMembers(ActorRole.ClanMember, belt.Id, new[] { far.Id }).Ok);
        }
    }
}
