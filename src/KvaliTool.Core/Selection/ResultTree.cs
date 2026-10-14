using System.ComponentModel;
using KvaliTool.Core.Models;
using KvaliTool.Core.Safety;

namespace KvaliTool.Core.Selection;

public enum RowKind
{
    Category,
    Group,
    Item,
}

/// <summary>Checkbox state. Only parents (category, group) can be <see cref="Mixed"/>.</summary>
public enum CheckState
{
    Unchecked,
    Checked,

    /// <summary>Some selectable children are checked and some aren't.</summary>
    Mixed,
}

public enum SortKey
{
    /// <summary>The order the scanner produced (categories always stay in catalog order).</summary>
    ScanOrder,
    Size,
    Name,
    Modified,
}

/// <summary>
/// One row of the flattened results list: a category, a group inside it, or an item inside a group.
/// Rows are created once by <see cref="ResultTree.Build"/> and never replaced; sort, filter and expand only
/// change which rows are visible and in what order. State changes raise <see cref="PropertyChanged"/>
/// (for <see cref="State"/>, <see cref="IsExpanded"/>, <see cref="SelectedBytes"/>, <see cref="IsExcluded"/>)
/// so a UI can bind to rows directly.
/// </summary>
public sealed class ResultRow : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs StateArgs = new(nameof(State));
    private static readonly PropertyChangedEventArgs IsExpandedArgs = new(nameof(IsExpanded));
    private static readonly PropertyChangedEventArgs SelectedBytesArgs = new(nameof(SelectedBytes));
    private static readonly PropertyChangedEventArgs IsExcludedArgs = new(nameof(IsExcluded));
    private static readonly PropertyChangedEventArgs IsSelectableArgs = new(nameof(IsSelectable));

    private List<ResultRow>? _children;

    internal ResultRow()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RowKind Kind { get; internal init; }

    /// <summary>0 = category, 1 = group, 2 = item. For indentation.</summary>
    public int Depth => (int)Kind;

    /// <summary>Null for category rows.</summary>
    public ResultRow? Parent { get; internal init; }

    /// <summary>The category this row belongs to (its own result for a category row).</summary>
    public required CategoryResult Category { get; init; }

    /// <summary>Set on item rows only.</summary>
    public ScanItem? Item { get; internal init; }

    /// <summary>Category display name, group name, or item name.</summary>
    public required string Name { get; init; }

    /// <summary>Item path, group root folder, or null for categories.</summary>
    public string? Path { get; internal init; }

    /// <summary>
    /// Item size; for parents, the total of their non-report-only children. Null when unknown
    /// (an item Windows can't size, or a parent whose children are all unknown).
    /// </summary>
    public long? SizeBytes { get; internal init; }

    /// <summary>True when this row or any child has an unknown size (shown as a "size unknown" badge).</summary>
    public bool HasUnknownSize { get; internal init; }

    public DateTime? LastModifiedUtc { get; internal init; }

    /// <summary>Item reason; null for parents.</summary>
    public string? Reason { get; internal init; }

    /// <summary>Item warning, or the category caveat on a category row.</summary>
    public string? Warning { get; internal init; }

    /// <summary>Number of item rows under a parent (1 for an item).</summary>
    public int ItemCount { get; internal init; }

    /// <summary>
    /// Can the user tick this row? False for report-only items, excluded items, and parents with no selectable
    /// children (e.g. a category that needs admin or was skipped).
    /// </summary>
    public bool IsSelectable { get; internal set; }

    public CheckState State { get; internal set; }

    /// <summary>Bytes of checked items in this row (the item itself, or all checked items under a parent).</summary>
    public long SelectedBytes { get; internal set; }

    public bool IsExpanded { get; internal set; }

    /// <summary>The user added an exclusion that matches this item after the scan. Excluded rows are unchecked and can't be checked.</summary>
    public bool IsExcluded { get; internal set; }

    /// <summary>Item is inside Documents, Desktop, Pictures, Videos, Music or a cloud-synced folder.</summary>
    public bool IsPersonal => Item is { } i && (i.InUserContentFolder || i.InCloudSyncedFolder);

    // ---- Bookkeeping used by ResultTree (not part of the public surface). ----

    /// <summary>Position among its siblings as the scanner produced them (the tie-breaker that makes sorting stable).</summary>
    internal int ScanIndex { get; init; }

    /// <summary>Child rows of a category or group, in the current sort order.</summary>
    internal List<ResultRow> Children => _children ??= [];

    /// <summary>The group this item belongs to.</summary>
    internal ResultRow GroupRow => Parent ?? throw new InvalidOperationException("An item row always has a group.");

    /// <summary>The category row of this row (itself for a category row).</summary>
    internal ResultRow CategoryRow => Kind == RowKind.Category ? this : (Parent ?? throw new InvalidOperationException("A group or item row always has a parent.")).CategoryRow;

    internal ScanItem ScanItem => Item ?? throw new InvalidOperationException("Only item rows carry a scan item.");

    /// <summary>Selectable items under a parent (1 or 0 for an item).</summary>
    internal int SelectableItems;

    /// <summary>Checked items under a parent (1 or 0 for an item).</summary>
    internal int CheckedItems;

    // Values before the current operation, so one operation raises one net change per row.
    internal bool Touched;
    internal CheckState PrevState;
    internal long PrevSelectedBytes;
    internal bool PrevSelectable;

    internal bool FilterMatch;
    internal int MatchCount;

    internal DuplicateSet? MemberOf;
    internal List<DuplicateSet>? KeeperOf;

    internal void RaiseState() => PropertyChanged?.Invoke(this, StateArgs);

    internal void RaiseIsExpanded() => PropertyChanged?.Invoke(this, IsExpandedArgs);

    internal void RaiseSelectedBytes() => PropertyChanged?.Invoke(this, SelectedBytesArgs);

    internal void RaiseIsExcluded() => PropertyChanged?.Invoke(this, IsExcludedArgs);

    internal void RaiseIsSelectable() => PropertyChanged?.Invoke(this, IsSelectableArgs);
}

/// <summary>
/// One set of identical files: the listed copies (rows) and the kept copy, which isn't listed in its own set but
/// may be listed as a row somewhere else (for example under Large files).
/// </summary>
internal sealed class DuplicateSet
{
    /// <summary>The kept copy's path(s). Empty when the scan didn't say which copy is kept.</summary>
    public List<string> KeeperPaths { get; } = [];

    public int MemberCount;

    public int CheckedMembers;

    /// <summary>Rows elsewhere in the tree whose path is the kept copy.</summary>
    public int KeeperRowCount;

    public int CheckedKeeperRows;

    /// <summary>
    /// Does the kept copy count as selected? It does when one of its rows is checked. When the scan didn't name
    /// a kept copy, nothing outside the listed copies protects the set, so the listed copies are the whole set.
    /// </summary>
    public bool KeeperCounts => KeeperPaths.Count == 0 || CheckedKeeperRows > 0;
}

/// <summary>What happened to the visible list. The UI applies it to its own collection.</summary>
/// <param name="Index">First visible index affected (Insert/Remove only).</param>
/// <param name="Count">Number of rows inserted or removed (Insert/Remove only).</param>
public readonly record struct VisibleRowsChange(VisibleRowsChangeKind Kind, int Index, int Count);

public enum VisibleRowsChangeKind
{
    /// <summary>The whole visible list changed (sort, filter, build). Re-read <see cref="ResultTree.VisibleRows"/>.</summary>
    Reset,
    Insert,
    Remove,
}

/// <summary>Outcome of a selection change, so the UI can explain anything it refused to do.</summary>
/// <param name="Changed">Item rows whose checked state changed.</param>
/// <param name="KeptForDuplicateSafety">
/// Items left unchecked because checking them would have selected every copy of a duplicate set
/// (including the kept copy if it is listed in another category, e.g. Large files).
/// </param>
/// <param name="NotSelectable">Items left unchecked because they are report-only or excluded.</param>
public readonly record struct SelectionOutcome(int Changed, int KeptForDuplicateSafety, int NotSelectable);

/// <summary>
/// The results of a scan as a flat list of category &gt; group &gt; item rows with tri-state checkboxes.
/// UI-agnostic and single-threaded. <see cref="Build"/> may run on a background thread; after that, use the tree
/// from one thread only (the UI thread).
///
/// Selection rules (enforced here, not in the UI):
/// <list type="bullet">
/// <item>A parent is Checked when all its selectable children are checked, Unchecked when none are, Mixed otherwise.
/// Ticking a parent ticks its selectable children (subject to the duplicate rule); unticking clears them.
/// Ticking a Mixed parent checks it.</item>
/// <item>Report-only and excluded items can never be checked.</item>
/// <item>A duplicate set can never have every copy checked. A set is the listed copies with the same
/// <see cref="ScanItem.DuplicateGroupId"/> plus the kept copy (<see cref="ScanItem.DuplicateKeeperPath"/>),
/// which counts as checked if a row with that path is checked anywhere (e.g. under Large files).</item>
/// <item>"Safe" means: SAFE-tier category, not report-only, not excluded, and not personal (<see cref="ResultRow.IsPersonal"/>).
/// REVIEW items are never checked by <see cref="SelectSafe"/> or by <see cref="Build"/>.</item>
/// <item>Sorting, filtering, expanding and collapsing never change the selection. Parent states and selected bytes
/// always describe ALL children, including ones the filter hides.</item>
/// </list>
/// Performance target: Build, Sort and SetFilter each under 1 s for 100,000 items.
/// </summary>
public sealed class ResultTree
{
    private readonly ScanReport _report;
    private readonly long _reclaimableBytes;

    /// <summary>Every row, in tree order.</summary>
    private readonly List<ResultRow> _all = [];
    private readonly List<ResultRow> _categories = [];

    /// <summary>Every item row, in tree order.</summary>
    private readonly List<ResultRow> _items = [];
    private readonly List<ResultRow> _visible = [];
    private readonly Dictionary<CategoryId, ResultRow> _categoryById = [];

    /// <summary>Rows changed by the operation in progress; <see cref="Flush"/> turns them into net notifications.</summary>
    private readonly List<ResultRow> _touched = [];

    private long _selectedBytes;
    private int _selectedItems;
    private int _selectedUnknownSize;
    private SortKey _sortKey;
    private bool _sortDescending;
    private string _filter = string.Empty;

    private ResultTree(ScanReport report)
    {
        _report = report;
        _reclaimableBytes = report.TotalReclaimableBytes;
    }

    /// <summary>Raised after <see cref="VisibleRows"/> changes.</summary>
    public event EventHandler<VisibleRowsChange>? VisibleRowsChanged;

    /// <summary>Raised after any checked state changes (once per operation, not once per row).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Builds the tree from a report. Rows are grouped by <see cref="ScanItem.Group"/> in first-seen order;
    /// categories follow report order and every category appears, even with no items (so its status shows).
    /// Initial selection = <see cref="SelectSafe"/>. Everything starts collapsed.
    /// </summary>
    public static ResultTree Build(ScanReport report)
    {
        var tree = new ResultTree(report);
        tree.CreateRows();
        tree.SelectSafeCore(out _, out _);
        tree.Flush();
        tree.RebuildVisible();
        return tree;
    }

    public ScanReport Report => _report;

    /// <summary>Every row in tree order, regardless of expansion or filter.</summary>
    public IReadOnlyList<ResultRow> AllRows => _all;

    public IReadOnlyList<ResultRow> CategoryRows => _categories;

    /// <summary>
    /// The rows to display, in display order: categories, plus the children of expanded rows. While a filter is
    /// active, only matching items and their parents are visible, and parents of matches are shown expanded
    /// regardless of <see cref="ResultRow.IsExpanded"/>.
    /// </summary>
    public IReadOnlyList<ResultRow> VisibleRows => _visible;

    /// <summary>Same as <see cref="ScanReport.TotalReclaimableBytes"/>.</summary>
    public long ReclaimableBytes => _reclaimableBytes;

    public long SelectedBytes => _selectedBytes;

    public int SelectedItemCount => _selectedItems;

    /// <summary>True when a checked item has an unknown size, so <see cref="SelectedBytes"/> is a lower bound.</summary>
    public bool SelectionHasUnknownSizes => _selectedUnknownSize > 0;

    public SortKey SortKey => _sortKey;

    public bool SortDescending => _sortDescending;

    /// <summary>Current filter text; empty when no filter.</summary>
    public string Filter => _filter;

    /// <summary>
    /// Ticks (<paramref name="isChecked"/> true) or clears a row. On a parent it applies to all selectable
    /// children, hidden ones included. Does nothing to a row that isn't selectable when ticking.
    /// </summary>
    public SelectionOutcome SetChecked(ResultRow row, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(row);
        int refusedForDuplicates = 0, notSelectable = 0;
        foreach (var item in ItemsUnder(row))
        {
            if (!isChecked)
            {
                if (item.State == CheckState.Checked)
                {
                    ApplyItem(item, false);
                }

                continue;
            }

            if (!item.IsSelectable)
            {
                notSelectable++;
            }
            else if (item.State != CheckState.Checked)
            {
                TryCheck(item, ref refusedForDuplicates);
            }
        }

        return Finish(refusedForDuplicates, notSelectable);
    }

    /// <summary>What a click or Space on the checkbox does: Unchecked or Mixed becomes Checked; Checked becomes Unchecked.</summary>
    public SelectionOutcome Toggle(ResultRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return SetChecked(row, row.State != CheckState.Checked);
    }

    /// <summary>Clears everything, then checks every safe item (see the class rules). Ignores the filter.</summary>
    public SelectionOutcome SelectSafe()
    {
        SelectSafeCore(out var refusedForDuplicates, out var notSelectable);
        return Finish(refusedForDuplicates, notSelectable);
    }

    /// <summary>
    /// Checks every selectable item that matches the current filter (all of them when there's no filter),
    /// keeping one copy of every duplicate set unchecked. Items hidden by the filter keep their state.
    /// </summary>
    public SelectionOutcome SelectAll()
    {
        var filtering = _filter.Length > 0;
        int refusedForDuplicates = 0, notSelectable = 0;
        foreach (var item in _items)
        {
            if (filtering && !item.FilterMatch)
            {
                continue;
            }

            if (!item.IsSelectable)
            {
                notSelectable++;
            }
            else if (item.State != CheckState.Checked)
            {
                TryCheck(item, ref refusedForDuplicates);
            }
        }

        return Finish(refusedForDuplicates, notSelectable);
    }

    /// <summary>Unchecks everything, hidden rows included.</summary>
    public SelectionOutcome ClearAll()
    {
        foreach (var item in _items)
        {
            if (item.State == CheckState.Checked)
            {
                ApplyItem(item, false);
            }
        }

        return Finish(0, 0);
    }

    /// <summary>Selected bytes of one category (same as the category row's <see cref="ResultRow.SelectedBytes"/>).</summary>
    public long SelectedBytesFor(CategoryId category) =>
        _categoryById.TryGetValue(category, out var row) ? row.SelectedBytes : 0;

    /// <summary>Expands or collapses a category or group row. Item rows ignore it. Raises an Insert or Remove change.</summary>
    public void SetExpanded(ResultRow row, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Kind == RowKind.Item || row.IsExpanded == expanded)
        {
            return;
        }

        row.IsExpanded = expanded;
        row.RaiseIsExpanded();

        // While a filter is active, parents of matches are shown open anyway; the flag only matters afterwards.
        if (_filter.Length > 0)
        {
            return;
        }

        // A row hidden under a collapsed parent isn't in the list, so there's nothing to show or hide yet.
        var index = _visible.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        if (expanded)
        {
            var shown = new List<ResultRow>();
            if (row.Kind == RowKind.Category)
            {
                foreach (var group in row.Children)
                {
                    shown.Add(group);
                    if (group.IsExpanded)
                    {
                        shown.AddRange(group.Children);
                    }
                }
            }
            else
            {
                shown.AddRange(row.Children);
            }

            if (shown.Count > 0)
            {
                _visible.InsertRange(index + 1, shown);
                OnVisibleRowsChanged(new VisibleRowsChange(VisibleRowsChangeKind.Insert, index + 1, shown.Count));
            }
        }
        else
        {
            var end = index + 1;
            while (end < _visible.Count && _visible[end].Depth > row.Depth)
            {
                end++;
            }

            var count = end - (index + 1);
            if (count > 0)
            {
                _visible.RemoveRange(index + 1, count);
                OnVisibleRowsChanged(new VisibleRowsChange(VisibleRowsChangeKind.Remove, index + 1, count));
            }
        }
    }

    /// <summary>
    /// Sorts groups within each category and items within each group (categories keep catalog order).
    /// Size: parents by total size; unknown sizes sort last. Name: ordinal-ignore-case. Modified: missing dates last.
    /// Stable, so equal keys keep scan order. Raises Reset.
    /// </summary>
    public void Sort(SortKey key, bool descending)
    {
        _sortKey = key;
        _sortDescending = descending;
        var comparison = CreateComparison(key, descending);
        foreach (var category in _categories)
        {
            category.Children.Sort(comparison);
            foreach (var group in category.Children)
            {
                group.Children.Sort(comparison);
            }
        }

        FlattenRows();
        RebuildVisible();
        OnVisibleRowsChanged(new VisibleRowsChange(VisibleRowsChangeKind.Reset, 0, 0));
    }

    /// <summary>
    /// Shows only items whose name, path, reason or group contains <paramref name="text"/> (case-insensitive),
    /// plus their parents. Null or whitespace clears the filter. Never changes the selection. Raises Reset.
    /// </summary>
    public void SetFilter(string? text)
    {
        _filter = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        if (_filter.Length > 0)
        {
            MarkFilterMatches(_filter);
        }

        RebuildVisible();
        OnVisibleRowsChanged(new VisibleRowsChange(VisibleRowsChangeKind.Reset, 0, 0));
    }

    /// <summary>
    /// Marks every item the list matches as excluded (unchecked, not selectable) after the user adds an
    /// "Always exclude" entry. Returns how many items became excluded. Rows stay visible with an "Excluded" badge;
    /// the next scan won't list them at all.
    /// </summary>
    public int ApplyExclusions(ExclusionList exclusions)
    {
        ArgumentNullException.ThrowIfNull(exclusions);
        var newlyExcluded = 0;
        foreach (var row in _items)
        {
            if (row.IsExcluded || row.ScanItem is not { Kind: not ItemKind.Virtual } item || !exclusions.IsExcluded(item.Path))
            {
                continue;
            }

            if (row.State == CheckState.Checked)
            {
                ApplyItem(row, false);
            }

            Touch(row);
            row.IsExcluded = true;
            row.RaiseIsExcluded();
            if (row.IsSelectable)
            {
                row.IsSelectable = false;
                var group = row.GroupRow;
                var category = group.CategoryRow;
                Touch(group);
                Touch(category);
                group.SelectableItems--;
                category.SelectableItems--;
            }

            newlyExcluded++;
        }

        if (Flush() > 0)
        {
            OnSelectionChanged();
        }

        return newlyExcluded;
    }

    private void OnVisibleRowsChanged(VisibleRowsChange change) => VisibleRowsChanged?.Invoke(this, change);

    private void OnSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------------------------------------
    // Building
    // ------------------------------------------------------------------------------------------------

    private sealed class GroupBucket(string name)
    {
        public string Name { get; } = name;

        public string? Root { get; set; }

        public List<ScanItem> Items { get; } = [];

        public Totals Totals;
    }

    private struct Totals
    {
        public long Bytes;
        public int KnownSizes;
        public bool AnyUnknown;
        public DateTime? Newest;

        public void Add(ScanItem item)
        {
            if (!item.ReportOnly)
            {
                if (item.SizeBytes is { } size)
                {
                    Bytes += size;
                    KnownSizes++;
                }
                else
                {
                    AnyUnknown = true;
                }
            }

            if (item.LastModifiedUtc is { } modified && (Newest is null || modified > Newest))
            {
                Newest = modified;
            }
        }

        public void Add(in Totals other)
        {
            Bytes += other.Bytes;
            KnownSizes += other.KnownSizes;
            AnyUnknown |= other.AnyUnknown;
            if (other.Newest is { } modified && (Newest is null || modified > Newest))
            {
                Newest = modified;
            }
        }

        /// <summary>Null when every sizable child is unknown.</summary>
        public readonly long? Size => KnownSizes == 0 && AnyUnknown ? null : Bytes;
    }

    /// <summary>The duplicate set an item belongs to: its group id, or (without one) its kept copy.</summary>
    private readonly record struct SetKey(string? GroupId, string? KeeperPath);

    private void CreateRows()
    {
        // Duplicate sets first: the keeper of a set may be a row in a later category.
        var sets = new Dictionary<SetKey, DuplicateSet>();
        var setsByKeeper = new Dictionary<string, List<DuplicateSet>>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in _report.Categories)
        {
            foreach (var item in result.Items)
            {
                if (KeyOf(item) is not { } key)
                {
                    continue;
                }

                if (!sets.TryGetValue(key, out var set))
                {
                    sets[key] = set = new DuplicateSet();
                }

                set.MemberCount++;
                if (item.DuplicateKeeperPath is { } keeper && !set.KeeperPaths.Contains(keeper, StringComparer.OrdinalIgnoreCase))
                {
                    set.KeeperPaths.Add(keeper);
                    if (!setsByKeeper.TryGetValue(keeper, out var list))
                    {
                        setsByKeeper[keeper] = list = [];
                    }

                    list.Add(set);
                }
            }
        }

        var categoryIndex = 0;
        foreach (var result in _report.Categories)
        {
            var buckets = new List<GroupBucket>();
            var bucketByName = new Dictionary<string, GroupBucket>(StringComparer.Ordinal);
            var categoryTotals = default(Totals);
            foreach (var item in result.Items)
            {
                if (!bucketByName.TryGetValue(item.Group, out var bucket))
                {
                    bucketByName[item.Group] = bucket = new GroupBucket(item.Group);
                    buckets.Add(bucket);
                }

                bucket.Root ??= item.GroupRoot;
                bucket.Items.Add(item);
                bucket.Totals.Add(item);
            }

            foreach (var bucket in buckets)
            {
                categoryTotals.Add(bucket.Totals);
            }

            var definition = result.Definition;
            var categoryRow = new ResultRow
            {
                Kind = RowKind.Category,
                Category = result,
                Name = definition.DisplayName,
                SizeBytes = categoryTotals.Size,
                HasUnknownSize = categoryTotals.AnyUnknown,
                LastModifiedUtc = categoryTotals.Newest,
                Warning = definition.Caveat,
                ItemCount = result.Items.Count,
                ScanIndex = categoryIndex++,
            };
            _categories.Add(categoryRow);
            _categoryById[definition.Id] = categoryRow;

            var groupIndex = 0;
            foreach (var bucket in buckets)
            {
                var groupRow = new ResultRow
                {
                    Kind = RowKind.Group,
                    Parent = categoryRow,
                    Category = result,
                    Name = bucket.Name,
                    Path = bucket.Root,
                    SizeBytes = bucket.Totals.Size,
                    HasUnknownSize = bucket.Totals.AnyUnknown,
                    LastModifiedUtc = bucket.Totals.Newest,
                    ItemCount = bucket.Items.Count,
                    ScanIndex = groupIndex++,
                };
                categoryRow.Children.Add(groupRow);
                groupRow.Children.Capacity = bucket.Items.Count;

                var itemIndex = 0;
                foreach (var item in bucket.Items)
                {
                    var selectable = !item.ReportOnly;
                    var itemRow = new ResultRow
                    {
                        Kind = RowKind.Item,
                        Parent = groupRow,
                        Category = result,
                        Item = item,
                        Name = item.Name,
                        Path = item.Path,
                        SizeBytes = item.SizeBytes,
                        HasUnknownSize = item.SizeBytes is null,
                        LastModifiedUtc = item.LastModifiedUtc,
                        Reason = item.Reason,
                        Warning = item.Warning,
                        ItemCount = 1,
                        IsSelectable = selectable,
                        ScanIndex = itemIndex++,
                    };

                    if (KeyOf(item) is { } key)
                    {
                        itemRow.MemberOf = sets[key];
                    }

                    if (setsByKeeper.Count > 0 && item.Kind != ItemKind.Virtual && setsByKeeper.TryGetValue(item.Path, out var keptSets))
                    {
                        itemRow.KeeperOf = keptSets;
                        foreach (var kept in keptSets)
                        {
                            kept.KeeperRowCount++;
                        }
                    }

                    if (selectable)
                    {
                        groupRow.SelectableItems++;
                        categoryRow.SelectableItems++;
                    }

                    groupRow.Children.Add(itemRow);
                }

                groupRow.IsSelectable = groupRow.SelectableItems > 0;
            }

            categoryRow.IsSelectable = categoryRow.SelectableItems > 0;
        }

        FlattenRows();
    }

    private static SetKey? KeyOf(ScanItem item) =>
        item.DuplicateGroupId is { } id ? new SetKey(id, null)
        : item.DuplicateKeeperPath is { } keeper ? new SetKey(null, keeper)
        : null;

    /// <summary>Rebuilds the tree-order lists after the children were reordered.</summary>
    private void FlattenRows()
    {
        _all.Clear();
        _items.Clear();
        foreach (var category in _categories)
        {
            _all.Add(category);
            foreach (var group in category.Children)
            {
                _all.Add(group);
                _all.AddRange(group.Children);
                _items.AddRange(group.Children);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Selection
    // ------------------------------------------------------------------------------------------------

    /// <summary>Is the item one of the things "Select safe items" may tick? Never review-tier, personal, report-only or excluded.</summary>
    private static bool IsSafe(ResultRow item) =>
        item.IsSelectable && item.Category.Definition.CheckedByDefault && !item.IsPersonal;

    private void SelectSafeCore(out int refusedForDuplicates, out int notSelectable)
    {
        foreach (var item in _items)
        {
            if (item.State == CheckState.Checked)
            {
                ApplyItem(item, false);
            }
        }

        refusedForDuplicates = 0;
        notSelectable = 0;
        foreach (var item in _items)
        {
            if (!item.Category.Definition.CheckedByDefault || item.IsPersonal)
            {
                continue;
            }

            if (!IsSafe(item))
            {
                notSelectable++;
                continue;
            }

            TryCheck(item, ref refusedForDuplicates);
        }
    }

    /// <summary>Checks an item unless that would leave a duplicate set with every copy checked.</summary>
    private void TryCheck(ResultRow item, ref int refusedForDuplicates)
    {
        if (WouldSelectEveryCopy(item))
        {
            refusedForDuplicates++;
            return;
        }

        ApplyItem(item, true);
    }

    /// <summary>
    /// Would ticking this item leave some duplicate set with every copy ticked? The item may be one of the listed
    /// copies, or the row of a set's kept copy somewhere else.
    /// </summary>
    private static bool WouldSelectEveryCopy(ResultRow item)
    {
        if (item.MemberOf is { } set && set.CheckedMembers + 1 == set.MemberCount && set.KeeperCounts)
        {
            return true;
        }

        if (item.KeeperOf is { } keptSets)
        {
            foreach (var kept in keptSets)
            {
                // Ticking the first keeper row makes the kept copy count as selected.
                if (kept.CheckedKeeperRows == 0 && kept.CheckedMembers == kept.MemberCount)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The one place an item's checked state changes. Keeps every count above it up to date.</summary>
    private void ApplyItem(ResultRow item, bool isChecked)
    {
        var group = item.GroupRow;
        var category = group.CategoryRow;
        Touch(item);
        Touch(group);
        Touch(category);

        var bytes = item.SizeBytes ?? 0;
        var delta = isChecked ? 1 : -1;
        item.State = isChecked ? CheckState.Checked : CheckState.Unchecked;
        item.SelectedBytes = isChecked ? bytes : 0;
        item.CheckedItems = isChecked ? 1 : 0;
        group.CheckedItems += delta;
        group.SelectedBytes += delta * bytes;
        category.CheckedItems += delta;
        category.SelectedBytes += delta * bytes;

        _selectedItems += delta;
        _selectedBytes += delta * bytes;
        if (item.SizeBytes is null)
        {
            _selectedUnknownSize += delta;
        }

        if (item.MemberOf is { } set)
        {
            set.CheckedMembers += delta;
        }

        if (item.KeeperOf is { } keptSets)
        {
            foreach (var kept in keptSets)
            {
                kept.CheckedKeeperRows += delta;
            }
        }
    }

    private void Touch(ResultRow row)
    {
        if (row.Touched)
        {
            return;
        }

        row.Touched = true;
        row.PrevState = row.State;
        row.PrevSelectedBytes = row.SelectedBytes;
        row.PrevSelectable = row.IsSelectable;
        _touched.Add(row);
    }

    /// <summary>
    /// Finishes an operation: recomputes the parents that were touched and raises one notification per property that
    /// really changed (a row that was unchecked and checked again within the operation raises nothing).
    /// </summary>
    /// <returns>Item rows whose checked state changed.</returns>
    private int Flush()
    {
        var changedItems = 0;
        foreach (var row in _touched)
        {
            row.Touched = false;
            if (row.Kind == RowKind.Item)
            {
                if (row.State != row.PrevState)
                {
                    changedItems++;
                }
            }
            else
            {
                row.IsSelectable = row.SelectableItems > 0;
                row.State = row.CheckedItems == 0 ? CheckState.Unchecked
                    : row.CheckedItems >= row.SelectableItems ? CheckState.Checked
                    : CheckState.Mixed;
            }

            if (row.State != row.PrevState)
            {
                row.RaiseState();
            }

            if (row.SelectedBytes != row.PrevSelectedBytes)
            {
                row.RaiseSelectedBytes();
            }

            if (row.IsSelectable != row.PrevSelectable)
            {
                row.RaiseIsSelectable();
            }
        }

        _touched.Clear();
        return changedItems;
    }

    private SelectionOutcome Finish(int refusedForDuplicates, int notSelectable)
    {
        var changed = Flush();
        if (changed > 0)
        {
            OnSelectionChanged();
        }

        return new SelectionOutcome(changed, refusedForDuplicates, notSelectable);
    }

    private static IEnumerable<ResultRow> ItemsUnder(ResultRow row)
    {
        switch (row.Kind)
        {
            case RowKind.Item:
                yield return row;
                break;
            case RowKind.Group:
                foreach (var item in row.Children)
                {
                    yield return item;
                }

                break;
            default:
                foreach (var group in row.Children)
                {
                    foreach (var item in group.Children)
                    {
                        yield return item;
                    }
                }

                break;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Sorting, filtering, visible rows
    // ------------------------------------------------------------------------------------------------

    private static Comparison<ResultRow> CreateComparison(SortKey key, bool descending)
    {
        // Equal keys fall back to scan order in both directions, which makes the sort stable.
        static int Tiebreak(ResultRow a, ResultRow b) => a.ScanIndex.CompareTo(b.ScanIndex);

        // Missing values always sort last, whichever way the sort goes.
        static int Compare<T>(T? a, T? b, bool descending) where T : struct, IComparable<T>
        {
            if (a is null)
            {
                return b is null ? 0 : 1;
            }

            if (b is null)
            {
                return -1;
            }

            var result = a.Value.CompareTo(b.Value);
            return descending ? -result : result;
        }

        return key switch
        {
            SortKey.Size => (a, b) =>
            {
                var result = Compare(a.SizeBytes, b.SizeBytes, descending);
                return result != 0 ? result : Tiebreak(a, b);
            },
            SortKey.Name => (a, b) =>
            {
                var result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                result = descending ? -result : result;
                return result != 0 ? result : Tiebreak(a, b);
            },
            SortKey.Modified => (a, b) =>
            {
                var result = Compare(a.LastModifiedUtc, b.LastModifiedUtc, descending);
                return result != 0 ? result : Tiebreak(a, b);
            },
            _ => (a, b) => descending ? Tiebreak(b, a) : Tiebreak(a, b),
        };
    }

    private void MarkFilterMatches(string filter)
    {
        foreach (var category in _categories)
        {
            category.MatchCount = 0;
            foreach (var group in category.Children)
            {
                group.MatchCount = 0;
                var groupMatches = group.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                foreach (var item in group.Children)
                {
                    item.FilterMatch = groupMatches
                        || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || (item.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (item.Reason?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
                    if (item.FilterMatch)
                    {
                        group.MatchCount++;
                    }
                }

                if (group.MatchCount > 0)
                {
                    category.MatchCount += group.MatchCount;
                }
            }
        }
    }

    private void RebuildVisible()
    {
        var filtering = _filter.Length > 0;
        _visible.Clear();
        foreach (var category in _categories)
        {
            if (filtering && category.MatchCount == 0)
            {
                continue;
            }

            _visible.Add(category);
            if (!filtering && !category.IsExpanded)
            {
                continue;
            }

            foreach (var group in category.Children)
            {
                if (filtering && group.MatchCount == 0)
                {
                    continue;
                }

                _visible.Add(group);
                if (!filtering && !group.IsExpanded)
                {
                    continue;
                }

                if (!filtering)
                {
                    _visible.AddRange(group.Children);
                    continue;
                }

                foreach (var item in group.Children)
                {
                    if (item.FilterMatch)
                    {
                        _visible.Add(item);
                    }
                }
            }
        }
    }
}
