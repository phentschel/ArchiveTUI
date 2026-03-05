using System.IO;

namespace ArchiveTUI.Infrastructure.Adapters.Watchers;

internal sealed class FileSystemWatcherWrapper : IFileSystemWatcherWrapper
{
    private readonly FileSystemWatcher _watcher;

    public FileSystemWatcherWrapper(string path)
    {
        _watcher = new FileSystemWatcher(path);
    }

    public event FileSystemEventHandler? Created
    {
        add => _watcher.Created += value;
        remove => _watcher.Created -= value;
    }

    public event FileSystemEventHandler? Changed
    {
        add => _watcher.Changed += value;
        remove => _watcher.Changed -= value;
    }

    public event FileSystemEventHandler? Deleted
    {
        add => _watcher.Deleted += value;
        remove => _watcher.Deleted -= value;
    }

    public event RenamedEventHandler? Renamed
    {
        add => _watcher.Renamed += value;
        remove => _watcher.Renamed -= value;
    }

    public event ErrorEventHandler? Error
    {
        add => _watcher.Error += value;
        remove => _watcher.Error -= value;
    }

    public bool IncludeSubdirectories { get => _watcher.IncludeSubdirectories; set => _watcher.IncludeSubdirectories = value; }
    public bool EnableRaisingEvents { get => _watcher.EnableRaisingEvents; set => _watcher.EnableRaisingEvents = value; }
    public string Filter { get => _watcher.Filter; set => _watcher.Filter = value; }
    public NotifyFilters NotifyFilter { get => _watcher.NotifyFilter; set => _watcher.NotifyFilter = value; }
    public int InternalBufferSize { get => _watcher.InternalBufferSize; set => _watcher.InternalBufferSize = value; }
    public string Path => _watcher.Path;

    public void Dispose()
    {
        _watcher.Dispose();
    }
}
