namespace ArchiveTUI.Domain.Models;

public sealed record AppSettingsRecord(
    string SourceRoot,
    IReadOnlyCollection<string> DestinationRoots,
    int VerifySamplePercent,
    bool AgeVerificationEnabled,
    DateTime UpdatedUtc);
