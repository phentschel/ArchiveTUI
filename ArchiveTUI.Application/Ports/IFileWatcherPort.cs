namespace ArchiveTUI.Application.Ports;

public interface IFileWatcherPort
{
    IDisposable Watch(string path, IReadOnlyCollection<string> ignorePrefixes, Action onChange, Action<Exception?> onError);
}
