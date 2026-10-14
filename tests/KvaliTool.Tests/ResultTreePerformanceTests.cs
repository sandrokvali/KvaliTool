using System.Diagnostics;
using KvaliTool.Core.Models;
using KvaliTool.Core.Safety;
using KvaliTool.Core.Selection;
using static KvaliTool.Tests.TestReports;

namespace KvaliTool.Tests;

/// <summary>
/// 100,000 item rows: Build, Sort and SetFilter must each finish in under a second. The limit is for Release
/// builds (that's what ships); a Debug build is only reported, not judged.
/// </summary>
[TestClass]
public sealed class ResultTreePerformanceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    private readonly TestContext _testContext;

    public ResultTreePerformanceTests(TestContext testContext) => _testContext = testContext;

    /// <summary>
    /// A few categories, many groups, plus one category whose 12,000 items all sit in a single group, and
    /// 10,000 duplicate sets (two copies each, kept copy not listed) so the duplicate bookkeeping is exercised too.
    /// </summary>
    private static ScanReport BuildReport(int totalItems)
    {
        var random = new Random(12345);
        var scale = totalItems / 100_000.0;

        IEnumerable<ScanItem> Files(string folder, int count, int perGroup, string groupPrefix, Func<int, string?>? groupId = null)
        {
            for (var i = 0; i < count; i++)
            {
                var group = $"{groupPrefix} {i / perGroup}";
                var name = $"file-{random.Next():x8}-{i}.dat";
                var set = groupId?.Invoke(i);
                yield return Item(
                    $@"C:\Users\Test\{folder}\{group}\{name}",
                    size: i % 97 == 0 ? null : random.Next(1, 5_000_000),
                    group: group,
                    reason: $"Not changed for {random.Next(30, 900)} days (your threshold: 30 days)",
                    modified: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(random.Next(0, 2_000_000)),
                    groupId: set,
                    keeper: set is null ? null : $@"C:\Users\Test\Keepers\{set}.dat");
            }
        }

        var duplicates = (int)(20_000 * scale) / 2 * 2;
        return Report(
            Category(CategoryId.UserTemp, Files("AppData\\Temp", (int)(30_000 * scale), 10, "Temp folder")),
            Category(CategoryId.ChromeCache, Files("Chrome", (int)(20_000 * scale), 8, "Profile")),
            Category(CategoryId.DeveloperCaches, Files("npm", (int)(12_000 * scale), int.MaxValue, "npm cache")),
            Category(CategoryId.DuplicateFiles, Files("Dups", duplicates, 2, "Set", i => $"set{i / 2}")),
            Category(CategoryId.LargeFiles, Files("Big", (int)(18_000 * scale), 20, "Folder")));
    }

    private static TimeSpan Time(Action action)
    {
        var timer = Stopwatch.StartNew();
        action();
        return timer.Elapsed;
    }

    [TestMethod]
    public void Build_sort_and_filter_with_100000_items_each_take_under_a_second()
    {
        // Warm up once on a small report so JIT time isn't charged to the measurements.
        var warm = ResultTree.Build(BuildReport(5_000));
        warm.Sort(SortKey.Size, true);
        warm.Sort(SortKey.Name, false);
        warm.Sort(SortKey.Modified, true);
        warm.SetFilter("file-0");
        warm.SelectAll();
        warm.ApplyExclusions(new ExclusionList(["*.zzz"]));

        var report = BuildReport(100_000);
        var itemCount = report.Categories.Sum(c => c.Items.Count);
        Assert.IsGreaterThanOrEqualTo(99_000, itemCount, "The test needs about 100,000 items.");
        Assert.IsGreaterThan(10_000, report.Categories.Single(c => c.Definition.Id == CategoryId.DeveloperCaches).Items.Count, "One category must hold >10k items in one group.");

        ResultTree tree = null!;
        var build = Time(() => tree = ResultTree.Build(report));
        var sortSize = Time(() => tree.Sort(SortKey.Size, true));
        var sortName = Time(() => tree.Sort(SortKey.Name, false));
        var sortModified = Time(() => tree.Sort(SortKey.Modified, true));
        var filter = Time(() => tree.SetFilter("file-0a"));
        var clearFilter = Time(() => tree.SetFilter(null));
        var selectAll = Time(() => tree.SelectAll());
        var toggleParent = Time(() => tree.Toggle(tree.CategoryRows[0]));
        var expand = Time(() => tree.SetExpanded(tree.CategoryRows[2], true));

        _testContext.WriteLine($"Items: {itemCount:N0}, rows: {tree.AllRows.Count:N0}");
        _testContext.WriteLine($"PERF Build           {build.TotalMilliseconds,8:N1} ms");
        _testContext.WriteLine($"PERF Sort size       {sortSize.TotalMilliseconds,8:N1} ms");
        _testContext.WriteLine($"PERF Sort name       {sortName.TotalMilliseconds,8:N1} ms");
        _testContext.WriteLine($"PERF Sort modified   {sortModified.TotalMilliseconds,8:N1} ms");
        _testContext.WriteLine($"PERF SetFilter       {filter.TotalMilliseconds,8:N1} ms");
        _testContext.WriteLine($"PERF Clear filter    {clearFilter.TotalMilliseconds,8:N1} ms");
        _testContext.WriteLine($"PERF SelectAll       {selectAll.TotalMilliseconds,8:N1} ms");
        _testContext.WriteLine($"PERF Toggle category {toggleParent.TotalMilliseconds,8:N1} ms");
        _testContext.WriteLine($"PERF Expand 12k group{expand.TotalMilliseconds,8:N1} ms");

#if DEBUG
        Assert.Inconclusive("Performance limits apply to Release builds. Timings were written to the test output.");
#else
        Assert.IsLessThan(Limit, build, $"Build took {build.TotalMilliseconds:N0} ms");
        Assert.IsLessThan(Limit, sortSize, $"Sort(size) took {sortSize.TotalMilliseconds:N0} ms");
        Assert.IsLessThan(Limit, sortName, $"Sort(name) took {sortName.TotalMilliseconds:N0} ms");
        Assert.IsLessThan(Limit, sortModified, $"Sort(modified) took {sortModified.TotalMilliseconds:N0} ms");
        Assert.IsLessThan(Limit, filter, $"SetFilter took {filter.TotalMilliseconds:N0} ms");
        Assert.IsLessThan(Limit, clearFilter, $"SetFilter(null) took {clearFilter.TotalMilliseconds:N0} ms");
#endif
    }

    [TestMethod]
    public void A_large_tree_keeps_its_selection_totals_consistent_with_its_rows()
    {
        var tree = ResultTree.Build(BuildReport(20_000));
        tree.SelectAll();
        tree.Sort(SortKey.Size, true);
        tree.SetFilter("file-0");
        tree.ClearAll();
        tree.SelectSafe();
        tree.Toggle(tree.CategoryRows[3]);

        var items = tree.AllRows.Where(r => r.Kind == RowKind.Item).ToList();
        Assert.AreEqual(items.Count(r => r.State == CheckState.Checked), tree.SelectedItemCount);
        Assert.AreEqual(items.Where(r => r.State == CheckState.Checked).Sum(r => r.SizeBytes ?? 0), tree.SelectedBytes);
        foreach (var category in tree.CategoryRows)
        {
            var mine = items.Where(r => r.Category == category.Category).ToList();
            Assert.AreEqual(mine.Where(r => r.State == CheckState.Checked).Sum(r => r.SizeBytes ?? 0), category.SelectedBytes, category.Name);
        }

        foreach (var children in items.GroupBy(r => r.Parent))
        {
            var expected = children.All(c => c.State == CheckState.Checked) ? CheckState.Checked
                : children.All(c => c.State == CheckState.Unchecked) ? CheckState.Unchecked
                : CheckState.Mixed;
            Assert.AreEqual(expected, children.Key?.State, children.Key?.Name);
        }
    }
}
