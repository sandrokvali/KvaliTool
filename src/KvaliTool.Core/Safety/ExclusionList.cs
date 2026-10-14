using System.IO.Enumeration;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Safety;

/// <summary>
/// Your "always exclude" list. A plain path excludes that file or folder and everything inside it.
/// A pattern with * or ? is matched against the full path ("*.psd", "D:\Projects\*\build").
/// A bare name without a folder ("node_modules") matches any file or folder with that name.
/// </summary>
public sealed class ExclusionList
{
    private readonly List<string> _paths = [];
    private readonly List<string> _patterns = [];

    public ExclusionList(IEnumerable<string> entries)
    {
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var entry = Environment.ExpandEnvironmentVariables(raw.Trim());
            if (entry.IndexOfAny(['*', '?']) >= 0)
            {
                _patterns.Add(entry);
            }
            else if (Path.IsPathFullyQualified(entry))
            {
                _paths.Add(PathUtil.Normalize(entry));
            }
            else
            {
                var name = entry.Trim('\\', '/');
                _patterns.Add(@"*\" + name);
                _patterns.Add(@"*\" + name + @"\*");
            }
        }
    }

    public bool IsEmpty => _paths.Count == 0 && _patterns.Count == 0;

    public bool IsExcluded(string path) =>
        _paths.Any(excluded => PathUtil.IsSameOrUnder(path, excluded))
        || _patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, path, ignoreCase: true));
}
