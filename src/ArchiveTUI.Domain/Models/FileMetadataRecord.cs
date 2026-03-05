namespace ArchiveTUI.Domain.Models;

public sealed record FileMetadataRecord(
    string SourceRoot,
    string RelativePath,
    long FileSize,
    DateTime LastModifiedUtc,
    string ChecksumSha256,
    DateTime BackupTimestampUtc);
