using KvaliTool.Core.Models;

namespace KvaliTool.Core.Scanning;

public interface ICategoryScanner
{
    CategoryDefinition Definition { get; }

    /// <summary>Slow scanners (whole-profile walks, DISM) are started first so they overlap with the quick ones.</summary>
    bool IsSlow => false;

    /// <summary>Reports findings into <paramref name="result"/>. Must not change anything on disk.</summary>
    Task ScanAsync(ScanContext context, CategoryResultBuilder result);
}

internal abstract class ScannerBase(CategoryId id) : ICategoryScanner
{
    public CategoryDefinition Definition { get; } = CategoryCatalog.Get(id);

    public virtual bool IsSlow => false;

    public virtual Task ScanAsync(ScanContext context, CategoryResultBuilder result)
    {
        Scan(context, result);
        return Task.CompletedTask;
    }

    protected virtual void Scan(ScanContext context, CategoryResultBuilder result) =>
        throw new NotImplementedException();
}
