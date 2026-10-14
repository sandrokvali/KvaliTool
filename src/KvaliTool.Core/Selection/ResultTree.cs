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

    internal void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
/// UI-agnostic and single-threaded: call it from one thread (the UI thread).
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
    private ResultTree()
    {
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
    public static ResultTree Build(ScanReport report) => throw new NotImplementedException();

    public ScanReport Report => throw new NotImplementedException();

    /// <summary>Every row in tree order, regardless of expansion or filter.</summary>
    public IReadOnlyList<ResultRow> AllRows => throw new NotImplementedException();

    public IReadOnlyList<ResultRow> CategoryRows => throw new NotImplementedException();

    /// <summary>
    /// The rows to display, in display order: categories, plus the children of expanded rows. While a filter is
    /// active, only matching items and their parents are visible, and parents of matches are shown expanded
    /// regardless of <see cref="ResultRow.IsExpanded"/>.
    /// </summary>
    public IReadOnlyList<ResultRow> VisibleRows => throw new NotImplementedException();

    /// <summary>Same as <see cref="ScanReport.TotalReclaimableBytes"/>.</summary>
    public long ReclaimableBytes => throw new NotImplementedException();

    public long SelectedBytes => throw new NotImplementedException();

    public int SelectedItemCount => throw new NotImplementedException();

    /// <summary>True when a checked item has an unknown size, so <see cref="SelectedBytes"/> is a lower bound.</summary>
    public bool SelectionHasUnknownSizes => throw new NotImplementedException();

    public SortKey SortKey => throw new NotImplementedException();

    public bool SortDescending => throw new NotImplementedException();

    /// <summary>Current filter text; empty when no filter.</summary>
    public string Filter => throw new NotImplementedException();

    /// <summary>
    /// Ticks (<paramref name="isChecked"/> true) or clears a row. On a parent it applies to all selectable
    /// children, hidden ones included. Does nothing to a row that isn't selectable when ticking.
    /// </summary>
    public SelectionOutcome SetChecked(ResultRow row, bool isChecked) => throw new NotImplementedException();

    /// <summary>What a click or Space on the checkbox does: Unchecked or Mixed becomes Checked; Checked becomes Unchecked.</summary>
    public SelectionOutcome Toggle(ResultRow row) => throw new NotImplementedException();

    /// <summary>Clears everything, then checks every safe item (see the class rules). Ignores the filter.</summary>
    public SelectionOutcome SelectSafe() => throw new NotImplementedException();

    /// <summary>
    /// Checks every selectable item that matches the current filter (all of them when there's no filter),
    /// keeping one copy of every duplicate set unchecked. Items hidden by the filter keep their state.
    /// </summary>
    public SelectionOutcome SelectAll() => throw new NotImplementedException();

    /// <summary>Unchecks everything, hidden rows included.</summary>
    public SelectionOutcome ClearAll() => throw new NotImplementedException();

    /// <summary>Selected bytes of one category (same as the category row's <see cref="ResultRow.SelectedBytes"/>).</summary>
    public long SelectedBytesFor(CategoryId category) => throw new NotImplementedException();

    /// <summary>Expands or collapses a category or group row. Item rows ignore it. Raises an Insert or Remove change.</summary>
    public void SetExpanded(ResultRow row, bool expanded) => throw new NotImplementedException();

    /// <summary>
    /// Sorts groups within each category and items within each group (categories keep catalog order).
    /// Size: parents by total size; unknown sizes sort last. Name: ordinal-ignore-case. Modified: missing dates last.
    /// Stable, so equal keys keep scan order. Raises Reset.
    /// </summary>
    public void Sort(SortKey key, bool descending) => throw new NotImplementedException();

    /// <summary>
    /// Shows only items whose name, path, reason or group contains <paramref name="text"/> (case-insensitive),
    /// plus their parents. Null or whitespace clears the filter. Never changes the selection. Raises Reset.
    /// </summary>
    public void SetFilter(string? text) => throw new NotImplementedException();

    /// <summary>
    /// Marks every item the list matches as excluded (unchecked, not selectable) after the user adds an
    /// "Always exclude" entry. Returns how many items became excluded. Rows stay visible with an "Excluded" badge;
    /// the next scan won't list them at all.
    /// </summary>
    public int ApplyExclusions(ExclusionList exclusions) => throw new NotImplementedException();

    private void OnVisibleRowsChanged(VisibleRowsChange change) => VisibleRowsChanged?.Invoke(this, change);

    private void OnSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);
}
