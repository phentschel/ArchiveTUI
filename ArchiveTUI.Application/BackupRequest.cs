namespace ArchiveTUI.Application;

public sealed record BackupRequest(
    string SourceRoot,
    IReadOnlyCollection<string> DestinationRoots,
    int VerifySamplePercent = 2,
    bool AgeVerificationEnabled = true);
