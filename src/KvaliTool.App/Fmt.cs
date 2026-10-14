using KvaliTool.Core;
using KvaliTool.Core.Models;
using KvaliTool.Core.Selection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;

namespace KvaliTool.App;

/// <summary>
/// Small pure functions used by x:Bind function bindings and by the pages: text for sizes, dates, badges and so on.
/// Keeping them here means the XAML contains no logic and the colour choices stay in theme resources.
/// </summary>
public static class Fmt
{
    // ---- visibility helpers ----

    public static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisText(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisParent(RowKind kind) => kind == RowKind.Item ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisCategoryStatus(RowKind kind, CategoryStatus status) =>
        kind == RowKind.Category && status != CategoryStatus.Scanned ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisReportOnly(ScanItem? item) => Vis(item?.ReportOnly == true);

    public static Visibility VisSelectedLine(RowKind kind, long selectedBytes) =>
        kind != RowKind.Item && selectedBytes > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---- row layout and state ----

    public static Thickness Indent(int depth) => new(depth * 20, 0, 0, 0);

    /// <summary>Mixed is shown as an indeterminate box (null).</summary>
    public static bool? Check(CheckState state) => state switch
    {
        CheckState.Checked => true,
        CheckState.Unchecked => false,
        _ => null,
    };

    public static bool CanSelect(bool selectable, bool excluded) => selectable && !excluded;

    public static string Chevron(bool expanded) => expanded ? "" : "";

    public static string ExpandName(string name, bool expanded) => (expanded ? "Collapse " : "Expand ") + name;

    public static string SelectName(string name) => "Select " + name;

    public static Windows.UI.Text.FontWeight NameWeight(RowKind kind) => kind == RowKind.Item ? FontWeights.Normal : FontWeights.SemiBold;

    /// <summary>Parents show how many items they hold: "npm cache (12 items)".</summary>
    public static string NameWithCount(string name, RowKind kind, int itemCount) =>
        kind == RowKind.Item ? name : $"{name} ({itemCount:N0} {(itemCount == 1 ? "item" : "items")})";

    // ---- text columns ----

    public static string SizeText(long? size, bool hasUnknown, RowKind kind)
    {
        if (size is null)
        {
            return "Unknown";
        }

        var text = Format.Bytes(size.Value);
        return hasUnknown && kind != RowKind.Item ? text + " + unknown" : text;
    }

    public static string SelectedText(long selectedBytes) => Format.Bytes(selectedBytes) + " selected";

    public static string DateText(DateTime? utc) => utc is { } value ? value.ToLocalTime().ToString("g") : string.Empty;

    public static string OrEmpty(string? text) => text ?? string.Empty;

    // ---- category status badge ----

    public static string StatusText(CategoryStatus status) => status switch
    {
        CategoryStatus.NeedsAdmin => "Needs admin",
        CategoryStatus.Skipped => "Skipped",
        CategoryStatus.Partial => "Partial",
        CategoryStatus.Failed => "Failed",
        CategoryStatus.NotFound => "Not found",
        _ => string.Empty,
    };

    /// <summary>Segoe Fluent Icons glyph, so the badge never relies on colour alone.</summary>
    public static string StatusGlyph(CategoryStatus status) => status switch
    {
        CategoryStatus.NeedsAdmin => "", // lock
        CategoryStatus.Skipped => "", // pause
        CategoryStatus.Partial => "", // warning
        CategoryStatus.Failed => "", // error
        CategoryStatus.NotFound => "", // search
        _ => string.Empty,
    };

    // ---- descriptions of category properties ----

    public static string TierText(RiskTier tier) => tier == RiskTier.Safe ? "SAFE" : "REVIEW";

    public static string CleanMethodText(CleanMethod method) => method switch
    {
        CleanMethod.QuarantineFiles => "Moved to quarantine (restorable)",
        CleanMethod.RecycleBinEntries => "Recycle Bin entries",
        CleanMethod.WindowsTool => "Windows' own tool (permanent)",
        CleanMethod.ExternalTool => "The program's own command (permanent)",
        _ => method.ToString(),
    };

    public static string StatusLongText(CategoryStatus status) => status switch
    {
        CategoryStatus.Scanned => "Scanned",
        CategoryStatus.Partial => "Partial (some folders could not be read)",
        CategoryStatus.NeedsAdmin => "Needs administrator rights (not scanned)",
        CategoryStatus.Skipped => "Skipped",
        CategoryStatus.NotFound => "Not found on this PC",
        CategoryStatus.Failed => "Failed",
        _ => status.ToString(),
    };

    /// <summary>"1.2 GB (1,288,490,188 bytes)" so the exact figure can be copied from the details pane.</summary>
    public static string SizeLong(long? size, bool hasUnknown) =>
        size is null ? "Unknown" : $"{Format.Bytes(size.Value)} ({size.Value:N0} bytes)" + (hasUnknown ? " + items of unknown size" : string.Empty);
}
