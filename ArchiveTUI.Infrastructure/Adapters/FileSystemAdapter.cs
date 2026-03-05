using System.Security.Cryptography;
using ArchiveTUI.Application.Ports;
using ArchiveTUI.Domain.Models;

namespace ArchiveTUI.Infrastructure.Adapters;

public sealed class FileSystemAdapter : IFilePort
{
    public bool DirectoryExists(string path)
    {
        return Directory.Exists(NormalizeForIo(path));
    }

    public IReadOnlyCollection<string> EnumerateFiles(string sourceRoot)
    {
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(NormalizeForIo(sourceRoot));

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                {
                    files.Add(DenormalizeFromIo(file));
                }
            }
            catch (Exception)
            {
                // Skip unreadable files/directories
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    stack.Push(dir);
                }
            }
            catch (Exception)
            {
                // Skip unreadable subdirectories
            }
        }

        return files;
    }

    public FileSnapshot ReadSnapshot(string fullPath)
    {
        var ioPath = NormalizeForIo(fullPath);
        var fileInfo = new FileInfo(ioPath);
        return new FileSnapshot(
            fullPath,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc,
            ComputeSha256(ioPath));
    }

    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(NormalizeForIo(path));
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        File.Copy(NormalizeForIo(sourcePath), NormalizeForIo(destinationPath), overwrite);
    }

    public bool FileExists(string path)
    {
        return File.Exists(NormalizeForIo(path));
    }

    public void DeleteFile(string path)
    {
        File.Delete(NormalizeForIo(path));
    }

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        var src = NormalizeForIo(sourcePath);
        var dst = NormalizeForIo(destinationPath);

        if (OperatingSystem.IsWindows())
        {
            if (overwrite && File.Exists(dst))
            {
                File.Replace(src, dst, null, ignoreMetadataErrors: true);
                return;
            }
        }

        File.Move(src, dst, overwrite);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(stream);
        return Convert.ToHexString(bytes);
    }

    private static string NormalizeForIo(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath.TrimStart('\\');
        }

        return @"\\?\" + fullPath;
    }

    private static string DenormalizeFromIo(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return path[4..];
        }

        return path;
    }
}
