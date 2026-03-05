using ArchiveTUI.Application.Ports;
using ArchiveTUI.Domain.Models;

namespace ArchiveTUI.Application.Services;

public sealed class FileService
{
    private readonly IFilePort _filePort;

    public FileService(IFilePort filePort)
    {
        _filePort = filePort;
    }

    public bool SourceExists(string sourceRoot)
    {
        return _filePort.DirectoryExists(sourceRoot);
    }

    public IReadOnlyCollection<string> EnumerateSourceFiles(string sourceRoot)
    {
        return _filePort.EnumerateFiles(sourceRoot);
    }

    public bool FileExists(string path)
    {
        return _filePort.FileExists(path);
    }

    public void DeleteFile(string path)
    {
        _filePort.DeleteFile(path);
    }

    public IReadOnlyCollection<DestinationCopyResult> ReplicateToDestinations(
        FileSnapshot sourceSnapshot,
        string relativePath,
        IReadOnlyCollection<string> destinationRoots)
    {
        var results = new List<DestinationCopyResult>();

        foreach (var destinationRoot in destinationRoots)
        {
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                _filePort.CreateDirectory(destinationDirectory);
            }

            var tempPath = Path.Combine(destinationDirectory ?? destinationRoot, $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                _filePort.CopyFile(sourceSnapshot.FullPath, tempPath, overwrite: true);

                var tempSnapshot = _filePort.ReadSnapshot(tempPath);

                if (!string.Equals(tempSnapshot.ChecksumSha256, sourceSnapshot.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    _filePort.DeleteFile(tempPath);
                    results.Add(new DestinationCopyResult(
                        destinationRoot,
                        destinationPath,
                        false,
                        $"Checksum mismatch after copy: '{destinationPath}'",
                        null));
                    continue;
                }

                _filePort.MoveFile(tempPath, destinationPath, overwrite: true);

                var destinationSnapshot = _filePort.ReadSnapshot(destinationPath);

                results.Add(new DestinationCopyResult(
                    destinationRoot,
                    destinationPath,
                    true,
                    null,
                    destinationSnapshot));
            }
            catch (Exception ex)
            {
                if (_filePort.FileExists(tempPath))
                {
                    _filePort.DeleteFile(tempPath);
                }

                results.Add(new DestinationCopyResult(
                    destinationRoot,
                    destinationPath,
                    false,
                    $"Copy failed: '{sourceSnapshot.FullPath}' -> '{destinationPath}'. {ex.Message}",
                    null));
            }
        }

        return results;
    }
}

public sealed record DestinationCopyResult(
    string DestinationRoot,
    string DestinationPath,
    bool Success,
    string? Error,
    FileSnapshot? DestinationSnapshot);
