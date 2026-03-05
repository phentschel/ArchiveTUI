namespace ArchiveTUI.Domain.Models;

public sealed record FileSnapshot(
    string FullPath,
    long FileSize,
    DateTime LastModifiedUtc,
    string ChecksumSha256);
