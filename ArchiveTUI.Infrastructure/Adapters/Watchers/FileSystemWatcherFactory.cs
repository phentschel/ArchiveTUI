namespace ArchiveTUI.Infrastructure.Adapters.Watchers;

internal sealed class FileSystemWatcherFactory : IFileSystemWatcherFactory
{
    public IFileSystemWatcherWrapper Create(string path) => new FileSystemWatcherWrapper(path);
}
