namespace ArchiveTUI.Infrastructure.Adapters.Watchers;

internal interface IFileSystemWatcherFactory
{
    IFileSystemWatcherWrapper Create(string path);
}

