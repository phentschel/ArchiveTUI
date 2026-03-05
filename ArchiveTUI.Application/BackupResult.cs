namespace ArchiveTUI.Application;

public sealed class BackupResult
{
    public int ScannedFiles { get; set; }

    public int CopiedFiles { get; set; }

    public int SkippedFiles { get; set; }

    public int FailedFiles { get; set; }

    public List<string> Errors { get; } = [];

    public List<string> Infos { get; } = [];
}
