using ArchiveTUI.Domain.Models;

namespace ArchiveTUI.Application.Ports;

public interface IFilePort
{
    bool DirectoryExists(string path);

    IReadOnlyCollection<string> EnumerateFiles(string sourceRoot);

    FileSnapshot ReadSnapshot(string fullPath);

    void CreateDirectory(string path);

    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    bool FileExists(string path);

    void DeleteFile(string path);

    void MoveFile(string sourcePath, string destinationPath, bool overwrite);
}
