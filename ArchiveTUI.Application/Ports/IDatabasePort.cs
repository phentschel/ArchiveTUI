using ArchiveTUI.Domain.Models;

namespace ArchiveTUI.Application.Ports;

public interface IDatabasePort
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<FileMetadataRecord?> GetMetadataAsync(
        string sourceRoot,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task StoreMetadataAsync(FileMetadataRecord metadata, CancellationToken cancellationToken = default);

    Task<DestinationMetadataRecord?> GetDestinationMetadataAsync(
        string sourceRoot,
        string relativePath,
        string destinationRoot,
        CancellationToken cancellationToken = default);

    Task StoreDestinationMetadataAsync(
        DestinationMetadataRecord metadata,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<DestinationMetadataRecord> ListDestinationMetadataAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken = default);

    Task DeleteDestinationMetadataAsync(
        string sourceRoot,
        string relativePath,
        string destinationRoot,
        CancellationToken cancellationToken = default);
}
