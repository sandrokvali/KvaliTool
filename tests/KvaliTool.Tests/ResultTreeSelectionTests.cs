using KvaliTool.Core.Models;
using KvaliTool.Core.Safety;
using KvaliTool.Core.Selection;
using static KvaliTool.Tests.TestReports;

namespace KvaliTool.Tests;

[TestClass]
public sealed class ResultTreeSelectionTests
{
    private static ResultRow Row(ResultTree tree, string path) => tree.AllRows.Single(r => r.Path == path && r.Kind == RowKind.Item);

    private static ResultRow CategoryRow(ResultTree tree, CategoryId id) => tree.CategoryRows.Single(c => c.Category.Definition.Id == id);

    private static ResultRow GroupRow(ResultTree tree, string name) => tree.AllRows.Single(r => r.Kind == RowKind.Group && r.Name == name);

    // ---------------------------------------------------------------- what "safe" means

    [TestMethod]
    public void Build_checks_safe_items_and_never_review_tier_items()
    {
        var tree = ResultTree.Build(Report(
            Category(CategoryId.UserTemp, Item(@"C:\t\a.tmp", 100)),
            Category(CategoryId.DeveloperCaches, Item(@"C:\dev\npm.blob", 500))));

        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\t\a.tmp").State);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\dev\npm.blob").State);
        Assert.AreEqual(100, tree.SelectedBytes);
        Assert.AreEqual(1, tree.SelectedItemCount);
        Assert.AreEqual(CheckState.Checked, CategoryRow(tree, CategoryId.UserTemp).State);
        Assert.AreEqual(CheckState.Unchecked, CategoryRow(tree, CategoryId.DeveloperCaches).State);
    }

    [TestMethod]
    public void Review_tier_categories_are_never_checked_by_select_safe_or_build()
    {
        var review = new[]
        {
            CategoryId.DeveloperCaches, CategoryId.DockerBuildCache, CategoryId.PreviousWindowsInstallation,
            CategoryId.ComponentStore, CategoryId.OldRestorePoints, CategoryId.DuplicateFiles, CategoryId.LargeFiles, CategoryId.OldDownloads,
        };
        var tree = ResultTree.Build(Report([.. review.Select(id => Category(id, Item(@"C:\x\" + id, 10)))]));
        Assert.AreEqual(0, tree.SelectedItemCount);

        tree.SelectAll();
        tree.SelectSafe();

        Assert.AreEqual(0, tree.SelectedItemCount);
        Assert.IsTrue(tree.AllRows.All(r => r.State == CheckState.Unchecked));
    }

    [TestMethod]
    public void Personal_and_cloud_items_are_not_checked_even_in_a_safe_category()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.UserTemp,
            Item(@"C:\u\plain.tmp", 1),
            Item(@"C:\u\Documents\mine.tmp", 2, personal: true),
            Item(@"C:\u\OneDrive\synced.tmp", 4, cloud: true))));

        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\u\plain.tmp").State);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\u\Documents\mine.tmp").State);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\u\OneDrive\synced.tmp").State);

        tree.ClearAll();
        tree.SelectSafe();

        Assert.AreEqual(1, tree.SelectedBytes);
        Assert.AreEqual(CheckState.Mixed, CategoryRow(tree, CategoryId.UserTemp).State);
    }

    [TestMethod]
    public void Report_only_items_are_never_checked_and_are_reported_as_not_selectable()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.PreviousWindowsInstallation,
            Item(@"C:\Windows.old", 900, reportOnly: true))));
        var row = Row(tree, @"C:\Windows.old");

        Assert.IsFalse(row.IsSelectable);
        Assert.IsFalse(CategoryRow(tree, CategoryId.PreviousWindowsInstallation).IsSelectable);

        var outcome = tree.SetChecked(row, true);
        Assert.AreEqual(new SelectionOutcome(0, 0, 1), outcome);
        Assert.AreEqual(CheckState.Unchecked, row.State);

        outcome = tree.SelectAll();
        Assert.AreEqual(0, outcome.Changed);
        Assert.AreEqual(1, outcome.NotSelectable);
        Assert.AreEqual(0, tree.SelectedBytes);
    }

    [TestMethod]
    public void Report_only_items_are_not_counted_in_parent_sizes()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.UserTemp,
            Item(@"C:\t\a", 100),
            Item(@"C:\t\b", 5000, reportOnly: true))));

        Assert.AreEqual(100, CategoryRow(tree, CategoryId.UserTemp).SizeBytes);
        Assert.AreEqual(tree.Report.TotalReclaimableBytes, tree.ReclaimableBytes);
        Assert.AreEqual(100, tree.ReclaimableBytes);
    }

    [TestMethod]
    public void Excluded_items_are_unchecked_cannot_be_checked_again_and_are_counted()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.UserTemp,
            Item(@"C:\t\keep.tmp", 10),
            Item(@"C:\t\photo.psd", 20),
            Item(@"C:\t\other.psd", 40))));
        Assert.AreEqual(70, tree.SelectedBytes);

        var count = tree.ApplyExclusions(new ExclusionList(["*.psd"]));

        Assert.AreEqual(2, count);
        var excluded = Row(tree, @"C:\t\photo.psd");
        Assert.IsTrue(excluded.IsExcluded);
        Assert.IsFalse(excluded.IsSelectable);
        Assert.AreEqual(CheckState.Unchecked, excluded.State);
        Assert.AreEqual(10, tree.SelectedBytes);
        Assert.AreEqual(1, tree.SelectedItemCount);

        Assert.AreEqual(1, tree.SetChecked(excluded, true).NotSelectable);
        Assert.AreEqual(CheckState.Unchecked, excluded.State);
        tree.SelectAll();
        tree.SelectSafe();
        Assert.AreEqual(CheckState.Unchecked, excluded.State);
        Assert.AreEqual(10, tree.SelectedBytes);

        // The parent is now judged on its remaining selectable item only.
        Assert.AreEqual(CheckState.Checked, CategoryRow(tree, CategoryId.UserTemp).State);
        Assert.AreEqual(0, tree.ApplyExclusions(new ExclusionList(["*.psd"])), "Already-excluded items are not counted twice.");
    }

    [TestMethod]
    public void Excluding_every_item_makes_the_parents_not_selectable()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.UserTemp, Item(@"C:\t\only.tmp", 10))));
        var category = CategoryRow(tree, CategoryId.UserTemp);
        Assert.IsTrue(category.IsSelectable);

        tree.ApplyExclusions(new ExclusionList([@"C:\t"]));

        Assert.IsFalse(category.IsSelectable);
        Assert.IsFalse(GroupRow(tree, "Group").IsSelectable);
        Assert.AreEqual(CheckState.Unchecked, category.State);
        Assert.AreEqual(0, tree.SelectedBytes);
    }

    [TestMethod]
    public void Select_safe_clears_everything_first_then_checks_only_safe_items()
    {
        var tree = ResultTree.Build(Report(
            Category(CategoryId.UserTemp, Item(@"C:\t\a", 1), Item(@"C:\t\b", 2)),
            Category(CategoryId.LargeFiles, Item(@"C:\big", 100))));
        tree.SetChecked(Row(tree, @"C:\t\a"), false);
        tree.SetChecked(Row(tree, @"C:\big"), true);
        Assert.AreEqual(102, tree.SelectedBytes);

        var outcome = tree.SelectSafe();

        Assert.AreEqual(3, tree.SelectedBytes);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\big").State);
        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\t\a").State);
        Assert.AreEqual(2, outcome.Changed, "Only the items whose state really changed are counted.");
    }

    [TestMethod]
    public void Select_all_checks_review_and_personal_items_because_the_user_asked_for_everything()
    {
        var tree = ResultTree.Build(Report(
            Category(CategoryId.LargeFiles, Item(@"C:\u\Documents\big", 100, personal: true))));

        tree.SelectAll();

        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\u\Documents\big").State);
        Assert.AreEqual(100, tree.SelectedBytes);
    }

    // ---------------------------------------------------------------- parents, counts, bytes

    [TestMethod]
    public void Parents_are_checked_unchecked_or_mixed_from_their_selectable_children()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.LargeFiles,
            Item(@"C:\big\a", 1, group: "G"),
            Item(@"C:\big\b", 2, group: "G"),
            Item(@"C:\big\rep", 4, group: "G", reportOnly: true))));
        var group = GroupRow(tree, "G");
        var category = CategoryRow(tree, CategoryId.LargeFiles);
        Assert.AreEqual(CheckState.Unchecked, group.State);

        tree.SetChecked(Row(tree, @"C:\big\a"), true);
        Assert.AreEqual(CheckState.Mixed, group.State);
        Assert.AreEqual(CheckState.Mixed, category.State);
        Assert.AreEqual(1, group.SelectedBytes);

        tree.SetChecked(Row(tree, @"C:\big\b"), true);
        Assert.AreEqual(CheckState.Checked, group.State, "The report-only child does not stop the parent being Checked.");
        Assert.AreEqual(CheckState.Checked, category.State);
        Assert.AreEqual(3, category.SelectedBytes);

        tree.SetChecked(Row(tree, @"C:\big\a"), false);
        tree.SetChecked(Row(tree, @"C:\big\b"), false);
        Assert.AreEqual(CheckState.Unchecked, group.State);
        Assert.AreEqual(0, category.SelectedBytes);
    }

    [TestMethod]
    public void Ticking_a_parent_ticks_all_selectable_children_and_a_mixed_parent_becomes_checked()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.LargeFiles,
            Item(@"C:\a", 1, group: "G1"),
            Item(@"C:\b", 2, group: "G1"),
            Item(@"C:\c", 4, group: "G2"))));
        var category = CategoryRow(tree, CategoryId.LargeFiles);
        tree.SetChecked(Row(tree, @"C:\a"), true);
        Assert.AreEqual(CheckState.Mixed, category.State);

        var outcome = tree.Toggle(category);

        Assert.AreEqual(CheckState.Checked, category.State);
        Assert.AreEqual(2, outcome.Changed);
        Assert.AreEqual(7, tree.SelectedBytes);

        tree.Toggle(GroupRow(tree, "G1"));
        Assert.AreEqual(CheckState.Mixed, category.State);
        Assert.AreEqual(4, tree.SelectedBytes);

        tree.Toggle(category);
        Assert.AreEqual(CheckState.Checked, category.State);
        tree.Toggle(category);
        Assert.AreEqual(CheckState.Unchecked, category.State);
        Assert.AreEqual(0, tree.SelectedBytes);
    }

    [TestMethod]
    public void Selected_bytes_follow_every_click_without_recomputing_from_scratch()
    {
        var tree = ResultTree.Build(Report(
            Category(CategoryId.UserTemp, Item(@"C:\a", 10, group: "G"), Item(@"C:\b", 20, group: "G")),
            Category(CategoryId.LargeFiles, Item(@"C:\c", 400))));

        Assert.AreEqual(30, tree.SelectedBytes);
        Assert.AreEqual(30, tree.SelectedBytesFor(CategoryId.UserTemp));
        Assert.AreEqual(0, tree.SelectedBytesFor(CategoryId.LargeFiles));
        Assert.AreEqual(0, tree.SelectedBytesFor(CategoryId.ChromeCache), "A category that isn't in the report has nothing selected.");

        tree.SetChecked(Row(tree, @"C:\c"), true);
        Assert.AreEqual(430, tree.SelectedBytes);
        Assert.AreEqual(400, tree.SelectedBytesFor(CategoryId.LargeFiles));

        tree.SetChecked(Row(tree, @"C:\a"), false);
        Assert.AreEqual(420, tree.SelectedBytes);
        Assert.AreEqual(20, GroupRow(tree, "G").SelectedBytes);
        Assert.AreEqual(2, tree.SelectedItemCount);

        tree.ClearAll();
        Assert.AreEqual(0, tree.SelectedBytes);
        Assert.AreEqual(0, tree.SelectedItemCount);
        Assert.IsTrue(tree.AllRows.All(r => r.State == CheckState.Unchecked && r.SelectedBytes == 0));
    }

    [TestMethod]
    public void Unknown_sizes_are_flagged_and_counted_as_a_lower_bound()
    {
        var tree = ResultTree.Build(Report(
            Category(CategoryId.UserTemp, Item(@"C:\a", 10)),
            Category(CategoryId.ComponentStore, Item("dism:store", null, kind: ItemKind.Virtual))));
        var store = CategoryRow(tree, CategoryId.ComponentStore);

        Assert.IsFalse(tree.SelectionHasUnknownSizes);
        Assert.IsTrue(store.HasUnknownSize);
        Assert.IsNull(store.SizeBytes, "A parent whose children are all unknown has no size.");

        tree.SetChecked(Row(tree, "dism:store"), true);
        Assert.IsTrue(tree.SelectionHasUnknownSizes);
        Assert.AreEqual(10, tree.SelectedBytes);

        tree.SetChecked(Row(tree, "dism:store"), false);
        Assert.IsFalse(tree.SelectionHasUnknownSizes);
    }

    [TestMethod]
    public void A_category_with_no_items_still_appears_and_cannot_be_ticked()
    {
        var tree = ResultTree.Build(Report(
            Category(CategoryId.EdgeCache, [], CategoryStatus.Skipped),
            Category(CategoryId.UserTemp, Item(@"C:\a", 1))));

        var edge = CategoryRow(tree, CategoryId.EdgeCache);
        Assert.AreEqual(CategoryStatus.Skipped, edge.Category.Status);
        Assert.IsFalse(edge.IsSelectable);
        tree.Toggle(edge);
        Assert.AreEqual(CheckState.Unchecked, edge.State);
        Assert.AreEqual(2, tree.CategoryRows.Count);
    }

    [TestMethod]
    public void Groups_are_created_in_first_seen_order_per_category()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.UserTemp,
            Item(@"C:\1", group: "B"), Item(@"C:\2", group: "A"), Item(@"C:\3", group: "B"))));

        var groups = tree.AllRows.Where(r => r.Kind == RowKind.Group).Select(g => g.Name).ToList();
        CollectionAssert.AreEqual(new[] { "B", "A" }, groups);
        Assert.AreEqual(2, GroupRow(tree, "B").ItemCount);
    }

    // ---------------------------------------------------------------- duplicate safety

    private static ResultTree DuplicateTreeWithKeeperUnderLargeFiles(int copies)
    {
        // The kept copy is a 2 GB file that is also listed under Large files.
        var members = Enumerable.Range(1, copies).Select(i => Item($@"C:\u\copy{i}.bin", 2_000, group: "Set 1", groupId: "g1", keeper: @"C:\u\original.bin"));
        return ResultTree.Build(Report(
            Category(CategoryId.DuplicateFiles, members),
            Category(CategoryId.LargeFiles, Item(@"C:\u\original.bin", 2_000, group: "Big"))));
    }

    private static ResultTree DuplicateTreeWithKeeperInsideOldDownloadsFolder() =>
        // The kept copy sits inside an old Downloads folder that is offered as a whole (a Folder row).
        ResultTree.Build(Report(
            Category(CategoryId.DuplicateFiles,
                Item(@"C:\u\Documents\copy1.bin", 2_000, group: "Set 1", groupId: "g1", keeper: @"C:\u\Downloads\Old\original.bin")),
            Category(CategoryId.OldDownloads,
                Item(@"C:\u\Downloads\Old", 9_000, group: "Downloads", kind: ItemKind.Folder))));

    [TestMethod]
    public void Folder_row_containing_the_keeper_counts_as_the_keeper_so_the_last_copy_is_refused()
    {
        var tree = DuplicateTreeWithKeeperInsideOldDownloadsFolder();
        Assert.AreEqual(1, tree.SetChecked(Row(tree, @"C:\u\Downloads\Old"), true).Changed);

        var outcome = tree.SetChecked(Row(tree, @"C:\u\Documents\copy1.bin"), true);

        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\u\Documents\copy1.bin").State);
    }

    [TestMethod]
    public void Folder_row_containing_the_keeper_is_refused_when_every_listed_copy_is_checked()
    {
        var tree = DuplicateTreeWithKeeperInsideOldDownloadsFolder();
        Assert.AreEqual(1, tree.SetChecked(Row(tree, @"C:\u\Documents\copy1.bin"), true).Changed);

        var outcome = tree.SetChecked(Row(tree, @"C:\u\Downloads\Old"), true);

        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\u\Downloads\Old").State);
    }

    [TestMethod]
    public void Select_all_never_selects_a_folder_row_and_every_copy_of_a_file_inside_it()
    {
        var tree = DuplicateTreeWithKeeperInsideOldDownloadsFolder();

        var outcome = tree.SelectAll();

        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
        Assert.IsFalse(
            Row(tree, @"C:\u\Downloads\Old").State == CheckState.Checked && Row(tree, @"C:\u\Documents\copy1.bin").State == CheckState.Checked,
            "The folder holding the kept copy and the only other copy must not both be selected.");
    }

    [TestMethod]
    public void Duplicate_set_with_an_unlisted_keeper_can_have_every_listed_copy_checked()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.DuplicateFiles,
            Item(@"C:\u\copy1.bin", 2_000, group: "Set 1", groupId: "g1", keeper: @"C:\u\original.bin"),
            Item(@"C:\u\copy2.bin", 2_000, group: "Set 1", groupId: "g1", keeper: @"C:\u\original.bin"))));

        var outcome = tree.SetChecked(CategoryRow(tree, CategoryId.DuplicateFiles), true);

        Assert.AreEqual(2, outcome.Changed);
        Assert.AreEqual(0, outcome.KeptForDuplicateSafety);
        Assert.AreEqual(CheckState.Checked, CategoryRow(tree, CategoryId.DuplicateFiles).State);
    }

    [TestMethod]
    public void Keeper_checked_under_large_files_stops_the_last_copy_being_checked()
    {
        var tree = DuplicateTreeWithKeeperUnderLargeFiles(copies: 2);
        Assert.AreEqual(1, tree.SetChecked(Row(tree, @"C:\u\original.bin"), true).Changed);

        Assert.AreEqual(0, tree.SetChecked(Row(tree, @"C:\u\copy1.bin"), true).KeptForDuplicateSafety);
        var outcome = tree.SetChecked(Row(tree, @"C:\u\copy2.bin"), true);

        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
        Assert.AreEqual(0, outcome.Changed);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\u\copy2.bin").State);
    }

    [TestMethod]
    public void Checking_the_keeper_row_is_refused_when_every_listed_copy_is_already_checked()
    {
        var tree = DuplicateTreeWithKeeperUnderLargeFiles(copies: 2);
        tree.SetChecked(GroupRow(tree, "Set 1"), true);
        Assert.AreEqual(CheckState.Checked, GroupRow(tree, "Set 1").State);

        var outcome = tree.SetChecked(Row(tree, @"C:\u\original.bin"), true);

        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\u\original.bin").State);
    }

    [TestMethod]
    public void Ticking_a_parent_never_completes_a_set_whose_keeper_is_checked_elsewhere()
    {
        var tree = DuplicateTreeWithKeeperUnderLargeFiles(copies: 3);
        tree.SetChecked(Row(tree, @"C:\u\original.bin"), true);

        var outcome = tree.SetChecked(CategoryRow(tree, CategoryId.DuplicateFiles), true);

        Assert.AreEqual(2, outcome.Changed);
        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
        Assert.AreEqual(CheckState.Mixed, GroupRow(tree, "Set 1").State, "One copy has to stay unchecked, so the set is only partly ticked.");
        Assert.AreEqual(CheckState.Mixed, CategoryRow(tree, CategoryId.DuplicateFiles).State);
    }

    [TestMethod]
    public void Select_all_never_checks_every_copy_of_a_duplicate_set()
    {
        var tree = DuplicateTreeWithKeeperUnderLargeFiles(copies: 2);

        var outcome = tree.SelectAll();

        var checkedCopies = new[] { @"C:\u\copy1.bin", @"C:\u\copy2.bin", @"C:\u\original.bin" }.Count(p => Row(tree, p).State == CheckState.Checked);
        Assert.AreEqual(2, checkedCopies, "Exactly one of the three copies must stay unchecked.");
        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
        Assert.AreEqual(2, outcome.Changed);
    }

    [TestMethod]
    public void Select_all_respects_the_rule_whichever_row_is_reached_last()
    {
        // Large files comes first in this report, so the keeper row is ticked before the copies.
        var tree = ResultTree.Build(Report(
            Category(CategoryId.LargeFiles, Item(@"C:\u\original.bin", 2_000, group: "Big")),
            Category(CategoryId.DuplicateFiles,
                Item(@"C:\u\copy1.bin", 2_000, group: "Set 1", groupId: "g1", keeper: @"C:\u\original.bin"),
                Item(@"C:\u\copy2.bin", 2_000, group: "Set 1", groupId: "g1", keeper: @"C:\u\original.bin"))));

        var outcome = tree.SelectAll();

        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\u\copy2.bin").State);
        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\u\original.bin").State);
    }

    [TestMethod]
    public void Without_a_named_keeper_one_listed_copy_always_stays_unchecked()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.DuplicateFiles,
            Item(@"C:\u\a.bin", 5, group: "Set 1", groupId: "g1"),
            Item(@"C:\u\b.bin", 5, group: "Set 1", groupId: "g1"))));

        var outcome = tree.SelectAll();

        Assert.AreEqual(1, outcome.Changed);
        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
    }

    [TestMethod]
    public void Separate_duplicate_sets_do_not_affect_each_other()
    {
        var tree = ResultTree.Build(Report(
            Category(CategoryId.DuplicateFiles,
                Item(@"C:\u\a1.bin", 5, group: "Set 1", groupId: "g1", keeper: @"C:\u\a0.bin"),
                Item(@"C:\u\b1.bin", 5, group: "Set 2", groupId: "g2", keeper: @"C:\u\b0.bin")),
            Category(CategoryId.LargeFiles, Item(@"C:\u\a0.bin", 5, group: "Big"))));
        tree.SetChecked(Row(tree, @"C:\u\a0.bin"), true);

        var outcome = tree.SetChecked(CategoryRow(tree, CategoryId.DuplicateFiles), true);

        Assert.AreEqual(1, outcome.KeptForDuplicateSafety);
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\u\a1.bin").State);
        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\u\b1.bin").State);
    }

    [TestMethod]
    public void Unchecking_the_keeper_row_lets_the_last_copy_be_checked_again()
    {
        var tree = DuplicateTreeWithKeeperUnderLargeFiles(copies: 1);
        tree.SetChecked(Row(tree, @"C:\u\original.bin"), true);
        Assert.AreEqual(1, tree.SetChecked(Row(tree, @"C:\u\copy1.bin"), true).KeptForDuplicateSafety);

        tree.SetChecked(Row(tree, @"C:\u\original.bin"), false);

        Assert.AreEqual(1, tree.SetChecked(Row(tree, @"C:\u\copy1.bin"), true).Changed);
    }

    [TestMethod]
    public void Keeper_lookup_ignores_path_case()
    {
        var tree = ResultTree.Build(Report(
            Category(CategoryId.DuplicateFiles, Item(@"C:\u\copy.bin", 5, group: "Set 1", groupId: "g1", keeper: @"C:\U\ORIGINAL.BIN")),
            Category(CategoryId.LargeFiles, Item(@"C:\u\original.bin", 5, group: "Big"))));
        tree.SetChecked(Row(tree, @"C:\u\original.bin"), true);

        Assert.AreEqual(1, tree.SetChecked(Row(tree, @"C:\u\copy.bin"), true).KeptForDuplicateSafety);
    }

    // ---------------------------------------------------------------- events

    [TestMethod]
    public void Selection_changed_is_raised_once_per_operation_and_not_when_nothing_changed()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.LargeFiles,
            Enumerable.Range(0, 50).Select(i => Item($@"C:\f\{i}", 1)))));
        var raised = 0;
        tree.SelectionChanged += (_, _) => raised++;

        tree.SelectAll();
        Assert.AreEqual(1, raised, "Fifty items ticked by one operation raise one event.");

        tree.SelectAll();
        Assert.AreEqual(1, raised, "Nothing changed, so nothing was raised.");

        tree.ClearAll();
        Assert.AreEqual(2, raised);

        tree.SetChecked(CategoryRow(tree, CategoryId.LargeFiles), true);
        Assert.AreEqual(3, raised);
    }

    [TestMethod]
    public void Rows_raise_property_changed_for_state_and_selected_bytes_once_per_operation()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.LargeFiles,
            Item(@"C:\a", 10, group: "G"), Item(@"C:\b", 20, group: "G"))));
        var item = Row(tree, @"C:\a");
        var group = GroupRow(tree, "G");
        var itemEvents = new List<string?>();
        var groupEvents = new List<string?>();
        item.PropertyChanged += (_, e) => itemEvents.Add(e.PropertyName);
        group.PropertyChanged += (_, e) => groupEvents.Add(e.PropertyName);

        tree.SetChecked(group, true);

        CollectionAssert.AreEquivalent(new[] { nameof(ResultRow.State), nameof(ResultRow.SelectedBytes) }, itemEvents);
        CollectionAssert.AreEquivalent(new[] { nameof(ResultRow.State), nameof(ResultRow.SelectedBytes) }, groupEvents);

        itemEvents.Clear();
        groupEvents.Clear();
        tree.SetChecked(group, true);
        Assert.AreEqual(0, itemEvents.Count);
        Assert.AreEqual(0, groupEvents.Count);
    }

    [TestMethod]
    public void Select_safe_does_not_flicker_rows_that_stay_checked()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.UserTemp, Item(@"C:\a", 10), Item(@"C:\b", 10))));
        var item = Row(tree, @"C:\a");
        var events = 0;
        item.PropertyChanged += (_, _) => events++;

        tree.SelectSafe();

        Assert.AreEqual(0, events);
    }

    [TestMethod]
    public void Excluding_an_item_raises_is_excluded_and_is_selectable()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.UserTemp, Item(@"C:\t\a.psd", 10))));
        var item = Row(tree, @"C:\t\a.psd");
        var events = new List<string?>();
        item.PropertyChanged += (_, e) => events.Add(e.PropertyName);

        tree.ApplyExclusions(new ExclusionList(["*.psd"]));

        CollectionAssert.AreEquivalent(
            new[] { nameof(ResultRow.State), nameof(ResultRow.SelectedBytes), nameof(ResultRow.IsExcluded), nameof(ResultRow.IsSelectable) },
            events);
    }

    // ---------------------------------------------------------------- selection survives view changes

    [TestMethod]
    public void Sorting_filtering_and_expanding_never_change_the_selection()
    {
        var tree = ResultTree.Build(Report(
            Category(CategoryId.UserTemp, Enumerable.Range(0, 20).Select(i => Item($@"C:\t\file{i}", 100 + i, group: "G" + (i % 3)))),
            Category(CategoryId.LargeFiles, Enumerable.Range(0, 5).Select(i => Item($@"C:\big{i}", 1000 * (i + 1))))));
        tree.SetChecked(Row(tree, @"C:\big2"), true);
        var before = tree.AllRows.Select(r => (r, r.State, r.SelectedBytes)).ToList();
        var selectedBytes = tree.SelectedBytes;
        var selectedCount = tree.SelectedItemCount;

        tree.SetExpanded(CategoryRow(tree, CategoryId.UserTemp), true);
        tree.Sort(SortKey.Size, true);
        tree.SetFilter("file1");
        tree.Sort(SortKey.Name, false);
        tree.SetFilter("big");
        tree.SetFilter(null);
        tree.SetExpanded(CategoryRow(tree, CategoryId.UserTemp), false);

        foreach (var (row, state, bytes) in before)
        {
            Assert.AreEqual(state, row.State, row.Name);
            Assert.AreEqual(bytes, row.SelectedBytes, row.Name);
        }

        Assert.AreEqual(selectedBytes, tree.SelectedBytes);
        Assert.AreEqual(selectedCount, tree.SelectedItemCount);
    }

    [TestMethod]
    public void Parent_state_and_selected_bytes_cover_children_hidden_by_the_filter()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.LargeFiles,
            Item(@"C:\x\apple.bin", 10, group: "G"), Item(@"C:\x\banana.bin", 20, group: "G"))));
        tree.SetFilter("apple");
        var group = GroupRow(tree, "G");

        tree.SetChecked(Row(tree, @"C:\x\apple.bin"), true);

        Assert.AreEqual(CheckState.Mixed, group.State, "The hidden banana is unchecked, so the group is only partly ticked.");
        tree.SetChecked(group, true);
        Assert.AreEqual(30, group.SelectedBytes, "Ticking a parent also ticks the children the filter hides.");
        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\x\banana.bin").State);
        Assert.AreEqual(30, tree.SelectedBytes);
    }

    [TestMethod]
    public void Select_all_with_a_filter_checks_only_matching_items_and_leaves_hidden_ones_alone()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.LargeFiles,
            Item(@"C:\x\apple.bin", 10, group: "G"), Item(@"C:\x\banana.bin", 20, group: "G"), Item(@"C:\x\apple2.bin", 40, group: "H"))));
        tree.SetChecked(Row(tree, @"C:\x\banana.bin"), true);
        tree.SetFilter("apple");

        var outcome = tree.SelectAll();

        Assert.AreEqual(2, outcome.Changed);
        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\x\apple.bin").State);
        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\x\banana.bin").State, "Hidden items keep their state, here still checked.");
        tree.SetFilter(null);
        tree.ClearAll();
        tree.SetChecked(Row(tree, @"C:\x\banana.bin"), false);
        tree.SetFilter("banana");
        tree.SelectAll();
        Assert.AreEqual(CheckState.Unchecked, Row(tree, @"C:\x\apple.bin").State);
        Assert.AreEqual(CheckState.Checked, Row(tree, @"C:\x\banana.bin").State);
    }
}
