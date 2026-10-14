using KvaliTool.Core.Models;
using KvaliTool.Core.Selection;
using static KvaliTool.Tests.TestReports;

namespace KvaliTool.Tests;

/// <summary>Expand/collapse, sort and filter: what is visible and in what order. None of it may touch the selection.</summary>
[TestClass]
public sealed class ResultTreeViewTests
{
    /// <summary>A UI's copy of the visible list, kept up to date only from the change notifications.</summary>
    private sealed class UiMirror
    {
        private readonly ResultTree _tree;

        public UiMirror(ResultTree tree)
        {
            _tree = tree;
            Rows = [.. tree.VisibleRows];
            tree.VisibleRowsChanged += (_, change) =>
            {
                Changes.Add(change);
                switch (change.Kind)
                {
                    case VisibleRowsChangeKind.Reset:
                        Rows = [.. _tree.VisibleRows];
                        break;
                    case VisibleRowsChangeKind.Insert:
                        Rows.InsertRange(change.Index, _tree.VisibleRows.Skip(change.Index).Take(change.Count));
                        break;
                    case VisibleRowsChangeKind.Remove:
                        Rows.RemoveRange(change.Index, change.Count);
                        break;
                }
            };
        }

        public List<ResultRow> Rows { get; private set; }

        public List<VisibleRowsChange> Changes { get; } = [];

        public void AssertMatchesTree() => CollectionAssert.AreEqual(_tree.VisibleRows.ToList(), Rows);
    }

    private static ResultRow CategoryRow(ResultTree tree, CategoryId id) => tree.CategoryRows.Single(c => c.Category.Definition.Id == id);

    private static ResultRow GroupRow(ResultTree tree, string name) => tree.AllRows.Single(r => r.Kind == RowKind.Group && r.Name == name);

    private static List<string> Names(IEnumerable<ResultRow> rows) => [.. rows.Select(r => r.Name)];

    private static ResultTree TwoCategoryTree() => ResultTree.Build(Report(
        Category(CategoryId.UserTemp,
            Item(@"C:\t\a1", 10, group: "A"), Item(@"C:\t\a2", 20, group: "A"), Item(@"C:\t\b1", 30, group: "B")),
        Category(CategoryId.LargeFiles,
            Item(@"C:\big1", 1000, group: "Big"))));

    // ---------------------------------------------------------------- build

    [TestMethod]
    public void Everything_starts_collapsed_with_only_category_rows_visible()
    {
        var tree = TwoCategoryTree();

        Assert.AreEqual(2, tree.VisibleRows.Count);
        Assert.IsTrue(tree.VisibleRows.All(r => r.Kind == RowKind.Category && !r.IsExpanded));
        Assert.AreEqual(2 + 3 + 4, tree.AllRows.Count, "AllRows has every row regardless of expansion.");
        CollectionAssert.AreEqual(
            new[] { "Temporary files (your account)", "A", "a1", "a2", "B", "b1", "Large files", "Big", "big1" },
            Names(tree.AllRows).ToArray());
    }

    [TestMethod]
    public void Rows_keep_their_identity_through_expand_sort_and_filter()
    {
        var tree = TwoCategoryTree();
        var rows = tree.AllRows.ToList();

        tree.SetExpanded(CategoryRow(tree, CategoryId.UserTemp), true);
        tree.Sort(SortKey.Size, true);
        tree.SetFilter("a");
        tree.SetFilter(null);

        Assert.AreEqual(rows.Count, tree.AllRows.Count);
        CollectionAssert.AreEquivalent(rows, tree.AllRows.ToList());
    }

    // ---------------------------------------------------------------- expand and collapse

    [TestMethod]
    public void Expanding_a_category_inserts_its_groups_right_below_it()
    {
        var tree = TwoCategoryTree();
        var ui = new UiMirror(tree);

        tree.SetExpanded(CategoryRow(tree, CategoryId.UserTemp), true);

        Assert.AreEqual(1, ui.Changes.Count);
        Assert.AreEqual(new VisibleRowsChange(VisibleRowsChangeKind.Insert, 1, 2), ui.Changes[0]);
        CollectionAssert.AreEqual(new[] { "Temporary files (your account)", "A", "B", "Large files" }, Names(tree.VisibleRows).ToArray());
        ui.AssertMatchesTree();
    }

    [TestMethod]
    public void Expanding_a_group_inserts_its_items_and_a_later_category_shifts_down()
    {
        var tree = TwoCategoryTree();
        var ui = new UiMirror(tree);
        tree.SetExpanded(CategoryRow(tree, CategoryId.LargeFiles), true);
        tree.SetExpanded(CategoryRow(tree, CategoryId.UserTemp), true);
        ui.Changes.Clear();

        tree.SetExpanded(GroupRow(tree, "A"), true);

        Assert.AreEqual(new VisibleRowsChange(VisibleRowsChangeKind.Insert, 2, 2), ui.Changes.Single());
        ui.AssertMatchesTree();
        CollectionAssert.AreEqual(
            new[] { "Temporary files (your account)", "A", "a1", "a2", "B", "Large files", "Big" },
            Names(tree.VisibleRows).ToArray());
    }

    [TestMethod]
    public void Collapsing_a_category_removes_its_groups_and_items_and_remembers_which_groups_were_open()
    {
        var tree = TwoCategoryTree();
        var temp = CategoryRow(tree, CategoryId.UserTemp);
        tree.SetExpanded(temp, true);
        tree.SetExpanded(GroupRow(tree, "A"), true);
        var ui = new UiMirror(tree);
        CollectionAssert.AreEqual(
            new[] { "Temporary files (your account)", "A", "a1", "a2", "B", "Large files" },
            Names(tree.VisibleRows).ToArray());

        tree.SetExpanded(temp, false);

        Assert.AreEqual(new VisibleRowsChange(VisibleRowsChangeKind.Remove, 1, 4), ui.Changes.Single());
        ui.AssertMatchesTree();
        Assert.AreEqual(2, tree.VisibleRows.Count);

        ui.Changes.Clear();
        tree.SetExpanded(temp, true);
        Assert.AreEqual(new VisibleRowsChange(VisibleRowsChangeKind.Insert, 1, 4), ui.Changes.Single());
        ui.AssertMatchesTree();
        CollectionAssert.AreEqual(new[] { "Temporary files (your account)", "A", "a1", "a2", "B", "Large files" }, Names(tree.VisibleRows).ToArray());
    }

    [TestMethod]
    public void Expanding_a_group_under_a_collapsed_category_changes_nothing_visible_until_the_category_opens()
    {
        var tree = TwoCategoryTree();
        var ui = new UiMirror(tree);

        tree.SetExpanded(GroupRow(tree, "A"), true);

        Assert.AreEqual(0, ui.Changes.Count);
        Assert.IsTrue(GroupRow(tree, "A").IsExpanded);

        tree.SetExpanded(CategoryRow(tree, CategoryId.UserTemp), true);
        Assert.AreEqual(new VisibleRowsChange(VisibleRowsChangeKind.Insert, 1, 4), ui.Changes.Single());
        ui.AssertMatchesTree();
    }

    [TestMethod]
    public void Expanding_twice_or_expanding_an_item_does_nothing()
    {
        var tree = TwoCategoryTree();
        var ui = new UiMirror(tree);
        var temp = CategoryRow(tree, CategoryId.UserTemp);
        tree.SetExpanded(temp, true);
        ui.Changes.Clear();

        tree.SetExpanded(temp, true);
        tree.SetExpanded(tree.AllRows.First(r => r.Kind == RowKind.Item), true);

        Assert.AreEqual(0, ui.Changes.Count);
        Assert.IsFalse(tree.AllRows.First(r => r.Kind == RowKind.Item).IsExpanded);
    }

    [TestMethod]
    public void Expanding_raises_is_expanded_on_the_row()
    {
        var tree = TwoCategoryTree();
        var temp = CategoryRow(tree, CategoryId.UserTemp);
        var events = new List<string?>();
        temp.PropertyChanged += (_, e) => events.Add(e.PropertyName);

        tree.SetExpanded(temp, true);
        tree.SetExpanded(temp, false);

        CollectionAssert.AreEqual(new[] { nameof(ResultRow.IsExpanded), nameof(ResultRow.IsExpanded) }, events);
    }

    [TestMethod]
    public void Expanding_a_category_with_no_items_raises_no_change()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.EdgeCache, [], CategoryStatus.Skipped)));
        var ui = new UiMirror(tree);

        tree.SetExpanded(tree.CategoryRows[0], true);

        Assert.AreEqual(0, ui.Changes.Count);
    }

    // ---------------------------------------------------------------- sorting

    private static ResultTree SortableTree() => ResultTree.Build(Report(
        Category(CategoryId.LargeFiles,
            Item(@"C:\x\mid", 50, group: "G1", modified: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Item(@"C:\x\Zed", 10, group: "G1", modified: new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Item(@"C:\x\unknown", null, group: "G1"),
            Item(@"C:\x\alpha", 90, group: "G1", modified: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Item(@"C:\x\tiny", 1, group: "G2"),
            Item(@"C:\x\huge", 500, group: "G2")),
        Category(CategoryId.OldDownloads, Item(@"C:\d\one", 5, group: "D"))));

    private static List<string> ItemNames(ResultTree tree, string group) =>
        Names(tree.AllRows.Where(r => r.Kind == RowKind.Item && r.Parent?.Name == group));

    private static List<string> LargeFileGroupNames(ResultTree tree) =>
        Names(tree.AllRows.Where(r => r.Kind == RowKind.Group && r.Category.Definition.Id == CategoryId.LargeFiles));

    [TestMethod]
    public void Sort_by_size_puts_unknown_sizes_last_in_both_directions()
    {
        var tree = SortableTree();

        tree.Sort(SortKey.Size, descending: true);
        CollectionAssert.AreEqual(new[] { "alpha", "mid", "Zed", "unknown" }, ItemNames(tree, "G1").ToArray());

        tree.Sort(SortKey.Size, descending: false);
        CollectionAssert.AreEqual(new[] { "Zed", "mid", "alpha", "unknown" }, ItemNames(tree, "G1").ToArray());
    }

    [TestMethod]
    public void Sort_by_size_orders_groups_by_their_total()
    {
        var tree = SortableTree();

        tree.Sort(SortKey.Size, descending: true);
        CollectionAssert.AreEqual(new[] { "G2", "G1" }, LargeFileGroupNames(tree).ToArray());

        tree.Sort(SortKey.Size, descending: false);
        CollectionAssert.AreEqual(new[] { "G1", "G2" }, LargeFileGroupNames(tree).ToArray());
    }

    [TestMethod]
    public void Sort_by_name_ignores_case_and_can_be_reversed()
    {
        var tree = SortableTree();

        tree.Sort(SortKey.Name, descending: false);
        CollectionAssert.AreEqual(new[] { "alpha", "mid", "unknown", "Zed" }, ItemNames(tree, "G1").ToArray());

        tree.Sort(SortKey.Name, descending: true);
        CollectionAssert.AreEqual(new[] { "Zed", "unknown", "mid", "alpha" }, ItemNames(tree, "G1").ToArray());
    }

    [TestMethod]
    public void Sort_by_modified_puts_missing_dates_last_in_both_directions()
    {
        var tree = SortableTree();

        tree.Sort(SortKey.Modified, descending: false);
        CollectionAssert.AreEqual(new[] { "Zed", "mid", "alpha", "unknown" }, ItemNames(tree, "G1").ToArray());

        tree.Sort(SortKey.Modified, descending: true);
        CollectionAssert.AreEqual(new[] { "alpha", "mid", "Zed", "unknown" }, ItemNames(tree, "G1").ToArray());
    }

    [TestMethod]
    public void Sorting_is_stable_equal_keys_keep_scan_order_in_both_directions()
    {
        var tree = ResultTree.Build(Report(Category(CategoryId.LargeFiles,
            Item(@"C:\x\c", 7), Item(@"C:\x\a", 7), Item(@"C:\x\b", 7), Item(@"C:\x\d", 9))));

        tree.Sort(SortKey.Size, descending: false);
        CollectionAssert.AreEqual(new[] { "c", "a", "b", "d" }, ItemNames(tree, "Group").ToArray());

        tree.Sort(SortKey.Size, descending: true);
        CollectionAssert.AreEqual(new[] { "d", "c", "a", "b" }, ItemNames(tree, "Group").ToArray());
    }

    [TestMethod]
    public void Scan_order_restores_the_original_order_and_can_be_reversed()
    {
        var tree = SortableTree();
        tree.Sort(SortKey.Name, false);

        tree.Sort(SortKey.ScanOrder, false);
        CollectionAssert.AreEqual(new[] { "mid", "Zed", "unknown", "alpha" }, ItemNames(tree, "G1").ToArray());

        tree.Sort(SortKey.ScanOrder, true);
        CollectionAssert.AreEqual(new[] { "alpha", "unknown", "Zed", "mid" }, ItemNames(tree, "G1").ToArray());
    }

    [TestMethod]
    public void Sorting_keeps_categories_in_catalog_order_and_reports_the_sort_and_raises_one_reset()
    {
        var tree = SortableTree();
        var ui = new UiMirror(tree);

        tree.Sort(SortKey.Name, descending: true);

        CollectionAssert.AreEqual(new[] { CategoryId.LargeFiles, CategoryId.OldDownloads }, tree.CategoryRows.Select(c => c.Category.Definition.Id).ToArray());
        Assert.AreEqual(SortKey.Name, tree.SortKey);
        Assert.IsTrue(tree.SortDescending);
        Assert.AreEqual(VisibleRowsChangeKind.Reset, ui.Changes.Single().Kind);
    }

    [TestMethod]
    public void Sorting_reorders_the_visible_rows_of_expanded_parents()
    {
        var tree = SortableTree();
        var large = CategoryRow(tree, CategoryId.LargeFiles);
        tree.SetExpanded(large, true);
        tree.SetExpanded(GroupRow(tree, "G1"), true);
        var ui = new UiMirror(tree);

        tree.Sort(SortKey.Name, descending: false);

        ui.AssertMatchesTree();
        var g1Items = Names(tree.VisibleRows.Where(r => r.Kind == RowKind.Item));
        CollectionAssert.AreEqual(new[] { "alpha", "mid", "unknown", "Zed" }, g1Items.ToArray());
        CollectionAssert.AreEqual(Names(tree.AllRows).Where(n => n is "alpha" or "mid" or "unknown" or "Zed").ToArray(), g1Items.ToArray());
    }

    // ---------------------------------------------------------------- filtering

    private static ResultTree FilterableTree() => ResultTree.Build(Report(
        Category(CategoryId.LargeFiles,
            Item(@"C:\movies\holiday.mp4", 100, group: "Videos folder", reason: "Over 1 GB"),
            Item(@"C:\movies\wedding.mkv", 200, group: "Videos folder", reason: "Over 1 GB"),
            Item(@"C:\isos\Ubuntu.iso", 300, group: "ISO files", reason: "Disk image")),
        Category(CategoryId.OldDownloads, Item(@"C:\dl\setup.exe", 400, group: "Files in Downloads", reason: "Not changed for 200 days"))));

    [TestMethod]
    public void Filter_matches_name_path_reason_or_group_ignoring_case()
    {
        var tree = FilterableTree();

        tree.SetFilter("HOLIDAY");
        CollectionAssert.AreEqual(new[] { "holiday.mp4" }, Names(tree.VisibleRows.Where(r => r.Kind == RowKind.Item)).ToArray());

        tree.SetFilter(@"c:\isos");
        CollectionAssert.AreEqual(new[] { "Ubuntu.iso" }, Names(tree.VisibleRows.Where(r => r.Kind == RowKind.Item)).ToArray());

        tree.SetFilter("200 days");
        CollectionAssert.AreEqual(new[] { "setup.exe" }, Names(tree.VisibleRows.Where(r => r.Kind == RowKind.Item)).ToArray());

        tree.SetFilter("videos FOLDER");
        CollectionAssert.AreEqual(new[] { "holiday.mp4", "wedding.mkv" }, Names(tree.VisibleRows.Where(r => r.Kind == RowKind.Item)).ToArray());
    }

    [TestMethod]
    public void Filter_shows_matches_with_their_parents_expanded_and_hides_categories_without_matches()
    {
        var tree = FilterableTree();
        Assert.IsFalse(CategoryRow(tree, CategoryId.LargeFiles).IsExpanded);

        tree.SetFilter("ubuntu");

        CollectionAssert.AreEqual(new[] { "Large files", "ISO files", "Ubuntu.iso" }, Names(tree.VisibleRows).ToArray());
        Assert.IsFalse(CategoryRow(tree, CategoryId.LargeFiles).IsExpanded, "The filter does not change the expanded flags.");
    }

    [TestMethod]
    public void Clearing_the_filter_returns_to_the_expansion_the_user_had()
    {
        var tree = FilterableTree();
        tree.SetExpanded(CategoryRow(tree, CategoryId.OldDownloads), true);
        var before = Names(tree.VisibleRows).ToArray();

        tree.SetFilter("ubuntu");
        tree.SetFilter("   ");

        Assert.AreEqual(string.Empty, tree.Filter);
        CollectionAssert.AreEqual(before, Names(tree.VisibleRows).ToArray());
    }

    [TestMethod]
    public void Filter_raises_one_reset_and_the_ui_mirror_follows()
    {
        var tree = FilterableTree();
        var ui = new UiMirror(tree);

        tree.SetFilter("mkv");

        Assert.AreEqual(VisibleRowsChangeKind.Reset, ui.Changes.Single().Kind);
        ui.AssertMatchesTree();
        Assert.AreEqual("mkv", tree.Filter);
    }

    [TestMethod]
    public void Filter_with_no_matches_shows_nothing()
    {
        var tree = FilterableTree();

        tree.SetFilter("zzz-no-such-thing");

        Assert.AreEqual(0, tree.VisibleRows.Count);
        Assert.AreEqual(9, tree.AllRows.Count, "Every row still exists, the filter only hides them.");
    }

    [TestMethod]
    public void Expanding_while_a_filter_is_active_only_remembers_the_flag()
    {
        var tree = FilterableTree();
        tree.SetFilter("ubuntu");
        var ui = new UiMirror(tree);

        tree.SetExpanded(CategoryRow(tree, CategoryId.LargeFiles), true);
        tree.SetExpanded(CategoryRow(tree, CategoryId.LargeFiles), false);
        tree.SetExpanded(CategoryRow(tree, CategoryId.LargeFiles), true);

        Assert.AreEqual(0, ui.Changes.Count);
        tree.SetFilter(null);
        CollectionAssert.AreEqual(new[] { "Large files", "Videos folder", "ISO files", "Old downloads" }, Names(tree.VisibleRows).ToArray());
        Assert.IsTrue(CategoryRow(tree, CategoryId.LargeFiles).IsExpanded);
    }

    [TestMethod]
    public void Sorting_while_filtering_keeps_the_filter()
    {
        var tree = FilterableTree();
        tree.SetFilter("Videos folder");

        tree.Sort(SortKey.Name, descending: true);

        CollectionAssert.AreEqual(new[] { "Large files", "Videos folder", "wedding.mkv", "holiday.mp4" }, Names(tree.VisibleRows).ToArray());
    }
}
