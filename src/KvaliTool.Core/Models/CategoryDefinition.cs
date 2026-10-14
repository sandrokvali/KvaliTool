namespace KvaliTool.Core.Models;

/// <summary>Static description of a category: what it is, how risky it is and how it would be cleaned.</summary>
/// <param name="AllowsUserContent">
/// Only Large files and Duplicate files may list files from Documents, Desktop, Pictures, Videos, Music
/// or cloud-sync folders. Every other category is blocked from those folders by <see cref="Safety.ProtectedPathPolicy"/>.
/// </param>
/// <param name="Caveat">Something you should know before selecting it. Shown in the UI and the confirmation dialog.</param>
public sealed record CategoryDefinition(
    CategoryId Id,
    string DisplayName,
    string Description,
    RiskTier Tier,
    CleanMethod CleanMethod,
    bool RequiresElevation,
    bool AllowsUserContent,
    string? Caveat)
{
    public bool CheckedByDefault => Tier == RiskTier.Safe;

    public bool Restorable => CleanMethod is CleanMethod.QuarantineFiles or CleanMethod.RecycleBinEntries;
}
