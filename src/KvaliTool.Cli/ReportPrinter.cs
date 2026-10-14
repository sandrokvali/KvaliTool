using KvaliTool.Core;
using KvaliTool.Core.Models;

namespace KvaliTool.Cli;

internal static class ReportPrinter
{
    public static void Print(ScanReport report, int top, bool showSkipped)
    {
        Console.WriteLine();
        WriteColored(ConsoleColor.Cyan, $"KvaliTool dry run - nothing was changed. ({report.Duration.TotalSeconds:0.0} s, {(report.IsElevated ? "elevated" : "not elevated")})");
        Console.WriteLine();

        foreach (var tier in new[] { RiskTier.Safe, RiskTier.Review })
        {
            WriteColored(tier == RiskTier.Safe ? ConsoleColor.Green : ConsoleColor.Yellow,
                tier == RiskTier.Safe ? "SAFE - checked by default" : "REVIEW - shown, never checked by default");

            foreach (var category in report.Categories.Where(c => c.Definition.Tier == tier))
            {
                PrintCategory(category, top, showSkipped);
            }

            Console.WriteLine();
        }

        Console.WriteLine($"Reclaimable (all categories): {Format.Bytes(report.TotalReclaimableBytes)}");
        Console.WriteLine($"Selected by default (SAFE):   {Format.Bytes(report.DefaultSelectedBytes)}");

        var unknown = report.Categories.Where(c => c.HasUnknownSizes).Select(c => c.Definition.DisplayName).ToList();
        if (unknown.Count > 0)
        {
            Console.WriteLine($"Plus items of unknown size in: {string.Join(", ", unknown)}");
        }

        var needAdmin = report.Categories.Where(c => c.Status == CategoryStatus.NeedsAdmin).Select(c => c.Definition.DisplayName).ToList();
        if (needAdmin.Count > 0)
        {
            Console.WriteLine($"Not scanned without administrator rights: {string.Join(", ", needAdmin)}");
            Console.WriteLine("  (Run from an elevated terminal to include them.)");
        }

        if (report.PolicyViolations.Count == 0)
        {
            WriteColored(ConsoleColor.Green, "Safety policy: 0 violations.");
        }
        else
        {
            WriteColored(ConsoleColor.Red, $"Safety policy: {report.PolicyViolations.Count} item(s) were BLOCKED. This is a scanner bug; they were dropped:");
            foreach (var v in report.PolicyViolations.Take(50))
            {
                Console.WriteLine($"  [{v.Category}] {v.Path}  ({v.Rule})");
            }
        }
    }

    private static void PrintCategory(CategoryResult c, int top, bool showSkipped)
    {
        var box = c.Definition.CheckedByDefault ? "[x]" : "[ ]";
        var count = c.Items.Count(i => !i.ReportOnly);
        var size = c.HasUnknownSizes && c.TotalBytes == 0 ? "size unknown" : Format.Bytes(c.TotalBytes);
        var status = c.Status == CategoryStatus.Scanned ? string.Empty : $"  [{c.Status}]";
        Console.WriteLine($"  {box} {c.Definition.DisplayName,-48} {count,8:N0} items {size,12}{status}");

        foreach (var note in c.Notes)
        {
            WriteColored(ConsoleColor.DarkGray, $"        note: {note}");
        }

        if (c.Skipped.Count > 0)
        {
            var summary = c.Skipped
                .GroupBy(s => s.Reason)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count():N0} {Describe(g.Key)}");
            WriteColored(ConsoleColor.DarkGray, $"        skipped: {string.Join(", ", summary)}");
        }

        foreach (var item in c.Items.OrderByDescending(i => i.SizeBytes ?? -1).Take(top))
        {
            var itemSize = item.SizeBytes is { } b ? Format.Bytes(b) : "?";
            var flag = item.ReportOnly ? " (report only)" : item.InUserContentFolder ? " (personal folder)" : string.Empty;
            Console.WriteLine($"        {itemSize,10}  {item.Path}{flag}");
            WriteColored(ConsoleColor.DarkGray, $"                    why: {item.Reason}");
            if (item.Warning is not null)
            {
                WriteColored(ConsoleColor.Yellow, $"                    warning: {item.Warning}");
            }
        }

        if (showSkipped)
        {
            foreach (var s in c.Skipped)
            {
                WriteColored(ConsoleColor.DarkGray, $"        skip [{s.Reason}] {s.Path} - {s.Detail}");
            }
        }
    }

    private static string Describe(SkipReason reason) => reason switch
    {
        SkipReason.RecentlyModified => "too recent",
        SkipReason.Locked => "in use",
        SkipReason.AccessDenied => "no permission",
        SkipReason.CloudOnlyPlaceholder => "cloud-only",
        SkipReason.LinkNotFollowed => "links not followed",
        SkipReason.ExcludedByUser => "excluded by you",
        SkipReason.Unreadable => "unreadable",
        SkipReason.AlreadyListed => "listed in another category",
        SkipReason.HardLinkOfSameFile => "hard links",
        SkipReason.OrphanedEntry => "orphaned",
        _ => reason.ToString(),
    };

    private static void WriteColored(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
