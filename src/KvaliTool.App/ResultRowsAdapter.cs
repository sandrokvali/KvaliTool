using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using KvaliTool.Core.Selection;

namespace KvaliTool.App;

/// <summary>
/// Presents <see cref="ResultTree.VisibleRows"/> to a ListView as an observable list. The ListView can't take
/// "these 5,000 rows were inserted" in one notification, so small changes are replayed one row at a time and large
/// ones become a single Reset. The adapter keeps its own copy of the rows so that every notification it raises is
/// consistent with what the ListView can read at that moment, even though the tree has already moved on.
/// </summary>
internal sealed class ResultRowsAdapter : ObservableCollection<ResultRow>
{
    /// <summary>Changes bigger than this are sent as a single Reset.</summary>
    public const int MaxRowByRowChange = 200;

    private readonly ResultTree _tree;

    public ResultRowsAdapter(ResultTree tree)
    {
        _tree = tree;
        foreach (var row in tree.VisibleRows)
        {
            Items.Add(row);
        }

        _tree.VisibleRowsChanged += OnVisibleRowsChanged;
    }

    /// <summary>
    /// Raised just before and just after an expand or collapse that was too big to replay row by row and was
    /// sent as a Reset instead, so the page can put the scroll position back.
    /// </summary>
    public event EventHandler? LargeStructureChangeStarting;

    public event EventHandler? LargeStructureChangeFinished;

    /// <summary>Stops listening to the tree. Call when the tree is replaced.</summary>
    public void Detach() => _tree.VisibleRowsChanged -= OnVisibleRowsChanged;

    private void OnVisibleRowsChanged(object? sender, VisibleRowsChange change)
    {
        var visible = _tree.VisibleRows;

        switch (change.Kind)
        {
            case VisibleRowsChangeKind.Insert
                when change.Count <= MaxRowByRowChange
                    && change.Index >= 0
                    && change.Index <= Count
                    && Count + change.Count == visible.Count:
                for (var i = 0; i < change.Count; i++)
                {
                    Insert(change.Index + i, visible[change.Index + i]);
                }

                break;

            case VisibleRowsChangeKind.Remove
                when change.Count <= MaxRowByRowChange
                    && change.Index >= 0
                    && change.Index + change.Count <= Count
                    && Count - change.Count == visible.Count:
                for (var i = 0; i < change.Count; i++)
                {
                    RemoveAt(change.Index);
                }

                break;

            case VisibleRowsChangeKind.Reset:
                ResetFromTree(structural: false);
                break;

            default:
                // A big insert/remove, or one that doesn't match what we hold: start over from the tree.
                ResetFromTree(structural: true);
                break;
        }

        if (Count != visible.Count)
        {
            ResetFromTree(structural: false);
        }
    }

    private void ResetFromTree(bool structural)
    {
        if (structural)
        {
            LargeStructureChangeStarting?.Invoke(this, EventArgs.Empty);
        }

        // Replace the contents without per-row notifications, then tell the list once.
        Items.Clear();
        foreach (var row in _tree.VisibleRows)
        {
            Items.Add(row);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        if (structural)
        {
            LargeStructureChangeFinished?.Invoke(this, EventArgs.Empty);
        }
    }
}
