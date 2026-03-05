using ArchiveTUI.Application.Ports;
using ArchiveTUI.Application.Services;

namespace ArchiveTUI.Application.UseCases;

public sealed class BackupManager : IBackupPort
{
    private readonly FileService _fileService;
    private readonly MetadataService _metadataService;

    public BackupManager(FileService fileService, MetadataService metadataService)
    {
        _fileService = fileService;
        _metadataService = metadataService;
    }

    public async Task<BackupResult> RunAsync(BackupRequest request, CancellationToken cancellationToken = default)
    {
        var (sourceRoot, destinationRoots, _) = ValidateRequest(request);
        var samplePercent = Math.Clamp(request.VerifySamplePercent, 0, 100);
        var rng = new Random(unchecked((int)DateTime.UtcNow.Ticks));

        await _metadataService.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var result = new BackupResult();

        await ReconcileAsync(sourceRoot, destinationRoots, result, cancellationToken).ConfigureAwait(false);

        var files = _fileService.EnumerateSourceFiles(sourceRoot);

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.ScannedFiles++;

            var relativePath = Path.GetRelativePath(sourceRoot, filePath);
            var snapshot = _metadataService.ExtractFileMetadata(filePath);

            var anyCopied = false;
            var anyFailed = false;
            var allUpToDate = true;

            foreach (var destinationRoot in destinationRoots)
            {
                var needsBackup = await _metadataService
                    .ShouldBackupDestinationAsync(sourceRoot, relativePath, destinationRoot, snapshot, request.AgeVerificationEnabled, forceVerify: false, cancellationToken)
                    .ConfigureAwait(false);

                if (!needsBackup && samplePercent > 0)
                {
                    var roll = rng.Next(0, 100);
                    if (roll < samplePercent)
                    {
                        needsBackup = await _metadataService
                            .ShouldBackupDestinationAsync(sourceRoot, relativePath, destinationRoot, snapshot, request.AgeVerificationEnabled, forceVerify: true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (!needsBackup)
                {
                    continue;
                }

                allUpToDate = false;

                var copyResults = _fileService.ReplicateToDestinations(snapshot, relativePath, new[] { destinationRoot });
                var copyResult = copyResults.Single();

                if (!copyResult.Success || copyResult.DestinationSnapshot is null)
                {
                    anyFailed = true;
                    result.Errors.Add(copyResult.Error ?? $"Unknown copy error for '{destinationRoot}'.");
                    continue;
                }

                await _metadataService
                    .StoreDestinationMetadataAsync(sourceRoot, relativePath, destinationRoot, copyResult.DestinationSnapshot, null, null, cancellationToken)
                    .ConfigureAwait(false);

                anyCopied = true;
            }

            if (anyFailed)
            {
                result.FailedFiles++;
            }
            else if (allUpToDate)
            {
                result.SkippedFiles++;
            }
            else if (anyCopied)
            {
                result.CopiedFiles++;
            }
        }

        return result;
    }

    private (string SourceRoot, string[] DestinationRoots, StringComparison Comparison) ValidateRequest(BackupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceRoot))
        {
            throw new ArgumentException("Source root is required.", nameof(request));
        }

        if (!_fileService.SourceExists(request.SourceRoot))
        {
            throw new DirectoryNotFoundException($"Source root does not exist: {request.SourceRoot}");
        }

        if (request.DestinationRoots is null || request.DestinationRoots.Count == 0)
        {
            throw new ArgumentException("At least one destination root is required.", nameof(request));
        }

        if (request.DestinationRoots.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Destination roots cannot contain empty values.", nameof(request));
        }

        var sourceRoot = Path.GetFullPath(request.SourceRoot);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var destinationRoots = request.DestinationRoots
            .Select(Path.GetFullPath)
            .Distinct(comparer)
            .ToArray();

        foreach (var destination in destinationRoots)
        {
            if (string.Equals(sourceRoot, destination, comparison))
            {
                throw new ArgumentException($"Destination '{destination}' must not be the same as source '{sourceRoot}'.", nameof(request));
            }

            if (IsSubPath(sourceRoot, destination, comparison))
            {
                throw new ArgumentException($"Destination '{destination}' must not be inside source '{sourceRoot}'.", nameof(request));
            }

            if (IsSubPath(destination, sourceRoot, comparison))
            {
                throw new ArgumentException($"Source '{sourceRoot}' must not be inside destination '{destination}'.", nameof(request));
            }
        }

        return (sourceRoot, destinationRoots, comparison);
    }

    private static bool IsSubPath(string parent, string candidate, StringComparison comparison)
    {
        if (string.Equals(parent, candidate, comparison))
        {
            return true;
        }

        var parentWithSeparator = EnsureTrailingSeparator(parent);
        return candidate.StartsWith(parentWithSeparator, comparison);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }

    private async Task ReconcileAsync(
        string sourceRoot,
        IReadOnlyCollection<string> destinationRoots,
        BackupResult result,
        CancellationToken cancellationToken)
    {
        foreach (var destinationRoot in destinationRoots)
        {
            await foreach (var metadata in _metadataService.ListDestinationMetadataAsync(sourceRoot, destinationRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePath = Path.Combine(sourceRoot, metadata.RelativePath);
                var destinationPath = Path.Combine(destinationRoot, metadata.RelativePath);

                var sourceExists = _fileService.FileExists(sourcePath);
                var destExists = _fileService.FileExists(destinationPath);

                if (!sourceExists)
                {
                    if (destExists)
                    {
                        _fileService.DeleteFile(destinationPath);
                        result.Infos.Add($"Pruned destination '{destinationPath}' because source is missing.");
                    }

                    await _metadataService.DeleteDestinationMetadataAsync(
                        metadata.SourceRoot,
                        metadata.RelativePath,
                        metadata.DestinationRoot,
                        cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if (!destExists)
                {
                    await _metadataService.DeleteDestinationMetadataAsync(
                        metadata.SourceRoot,
                        metadata.RelativePath,
                        metadata.DestinationRoot,
                        cancellationToken).ConfigureAwait(false);
                    result.Infos.Add($"Pruned metadata for missing destination '{destinationPath}'.");
                }
            }

            // Remove files in destination that have no source counterpart, even if no metadata exists.
            var destFiles = _fileService.EnumerateSourceFiles(destinationRoot).ToArray();
            foreach (var destFile in destFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(destinationRoot, destFile);
                var sourcePath = Path.Combine(sourceRoot, relative);
                if (_fileService.FileExists(sourcePath))
                {
                    continue;
                }

                _fileService.DeleteFile(destFile);
                await _metadataService.DeleteDestinationMetadataAsync(sourceRoot, relative, destinationRoot, cancellationToken)
                    .ConfigureAwait(false);
                result.Infos.Add($"Pruned destination '{destFile}' because source is missing (no metadata).");
            }
        }
    }
}
