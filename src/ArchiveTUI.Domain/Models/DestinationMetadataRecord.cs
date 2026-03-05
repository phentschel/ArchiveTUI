namespace ArchiveTUI.Domain.Models;

public sealed record DestinationMetadataRecord(
    string SourceRoot,
    string RelativePath,
    string DestinationRoot,
    long FileSize,
    DateTime LastModifiedUtc,
    string ChecksumSha256,
    DateTime BackupTimestampUtc,
    DateTime LastVerifiedUtc);
