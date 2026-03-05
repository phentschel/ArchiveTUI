using ArchiveTUI.Application.Ports;
using ArchiveTUI.Domain.Models;

namespace ArchiveTUI.Application.Services;

public sealed class MetadataService
{
    private readonly IFilePort _filePort;
    private readonly IDatabasePort _databasePort;
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromDays(7);

    public MetadataService(IFilePort filePort, IDatabasePort databasePort)
    {
        _filePort = filePort;
        _databasePort = databasePort;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return _databasePort.InitializeAsync(cancellationToken);
    }

    public FileSnapshot ExtractFileMetadata(string filePath)
    {
        return _filePort.ReadSnapshot(filePath);
    }

    public async Task<bool> ShouldBackupDestinationAsync(
        string sourceRoot,
        string relativePath,
        string destinationRoot,
        FileSnapshot sourceSnapshot,
        bool ageVerificationEnabled,
        bool forceVerify,
        CancellationToken cancellationToken = default)
    {
        var metadata = await _databasePort
            .GetDestinationMetadataAsync(sourceRoot, relativePath, destinationRoot, cancellationToken)
            .ConfigureAwait(false);

        if (metadata is null)
        {
            return true;
        }

        var age = DateTime.UtcNow - metadata.BackupTimestampUtc;
        var verifyDest = (ageVerificationEnabled && age > VerificationInterval) || forceVerify;

        if (!verifyDest)
        {
            return sourceSnapshot.FileSize != metadata.FileSize
                || sourceSnapshot.LastModifiedUtc != metadata.LastModifiedUtc
                || !string.Equals(sourceSnapshot.ChecksumSha256, metadata.ChecksumSha256, StringComparison.OrdinalIgnoreCase);
        }

        var destPath = Path.Combine(destinationRoot, relativePath);
        FileSnapshot? destinationSnapshot = null;

        try
        {
            destinationSnapshot = _filePort.ReadSnapshot(destPath);
        }
        catch (Exception)
        {
            // Missing or unreadable destination, force re-copy
            return true;
        }

        var destMatchesSource = destinationSnapshot.FileSize == sourceSnapshot.FileSize
            && destinationSnapshot.LastModifiedUtc == sourceSnapshot.LastModifiedUtc
            && string.Equals(destinationSnapshot.ChecksumSha256, sourceSnapshot.ChecksumSha256, StringComparison.OrdinalIgnoreCase);

        if (!destMatchesSource)
        {
            return true;
        }

        await StoreDestinationMetadataAsync(
            sourceRoot,
            relativePath,
            destinationRoot,
            destinationSnapshot,
            metadata.BackupTimestampUtc,
            DateTime.UtcNow,
            cancellationToken).ConfigureAwait(false);

        return false;
    }

    public Task StoreDestinationMetadataAsync(
        string sourceRoot,
        string relativePath,
        string destinationRoot,
        FileSnapshot snapshot,
        DateTime? backupTimestampUtc = null,
        DateTime? lastVerifiedUtc = null,
        CancellationToken cancellationToken = default)
    {
        var backupTimestamp = backupTimestampUtc ?? DateTime.UtcNow;
        var verifiedTimestamp = lastVerifiedUtc ?? backupTimestamp;

        var metadata = new DestinationMetadataRecord(
            sourceRoot,
            relativePath,
            destinationRoot,
            snapshot.FileSize,
            snapshot.LastModifiedUtc,
            snapshot.ChecksumSha256,
            backupTimestamp,
            verifiedTimestamp);

        return _databasePort.StoreDestinationMetadataAsync(metadata, cancellationToken);
    }

    public IAsyncEnumerable<DestinationMetadataRecord> ListDestinationMetadataAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        return _databasePort.ListDestinationMetadataAsync(sourceRoot, destinationRoot, cancellationToken);
    }

    public Task DeleteDestinationMetadataAsync(
        string sourceRoot,
        string relativePath,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        return _databasePort.DeleteDestinationMetadataAsync(sourceRoot, relativePath, destinationRoot, cancellationToken);
    }
}
