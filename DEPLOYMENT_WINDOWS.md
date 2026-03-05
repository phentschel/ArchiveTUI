# ArchiveTUI Deployment Guide (Windows, PostgreSQL + Docker)

This guide explains how to deploy and run **ArchiveTUI** on Windows using a local PostgreSQL Docker container with persistent storage.

ArchiveTUI is a local .NET CLI/TUI backup tool. It does **not** host a web server and does **not** require inbound app ports.

## 1. Prerequisites

Install:

1. Windows 10/11 or Windows Server 2019+
2. Docker Desktop (or Docker Engine + Compose plugin)
3. .NET 9 SDK (for build) or .NET 9 Runtime (for framework-dependent run)
4. PowerShell 5.1+ or PowerShell 7+
5. Access permissions to source/destination folders

Check installation:

```powershell
docker --version
docker compose version
dotnet --info
dotnet --list-runtimes
```

## 2. Repository and Folder Layout

Recommended deployment layout:

- `C:\Apps\ArchiveTUI\app` (published binaries)
- `C:\Apps\ArchiveTUI\data` (optional legacy SQLite file location)
- `C:\Apps\ArchiveTUI\logs` (stdout/stderr logs)

Example setup:

```powershell
New-Item -ItemType Directory -Force C:\Apps\ArchiveTUI\app, C:\Apps\ArchiveTUI\data, C:\Apps\ArchiveTUI\logs | Out-Null
```

## 3. PostgreSQL Container (Persistent)

From repository root:

```powershell
Copy-Item .env.example .env -Force
# Edit .env and set ARCHIVETUI_DB_PASSWORD (and optional user/name overrides)
notepad .env

docker compose -f .\docker-compose.postgres.yml --env-file .\.env up -d
```

Validate health:

```powershell
docker compose -f .\docker-compose.postgres.yml --env-file .\.env ps
```

Persistence is provided by Docker named volume `archivetui_pgdata`.

## 4. Build and Publish

From repository root:

### Option A: Framework-dependent

```powershell
dotnet restore .\ArchiveTUI.sln
dotnet build .\ArchiveTUI.sln -c Release
dotnet publish .\src\ArchiveTUI.Cli\ArchiveTUI.Cli.csproj -c Release -o C:\Apps\ArchiveTUI\app
```

### Option B: Self-contained single-file

```powershell
dotnet restore .\ArchiveTUI.sln
dotnet publish .\src\ArchiveTUI.Cli\ArchiveTUI.Cli.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o C:\Apps\ArchiveTUI\app
```

## 5. Required Runtime Environment Variables

ArchiveTUI reads DB settings from env vars:

- `ARCHIVETUI_DB_HOST` (default `127.0.0.1`)
- `ARCHIVETUI_DB_PORT` (default `5432`)
- `ARCHIVETUI_DB_NAME` (default `archivetui`)
- `ARCHIVETUI_DB_USER` (**required**)
- `ARCHIVETUI_DB_PASSWORD` (**required**)
- `ARCHIVETUI_DB_SSLMODE` (default `Disable`)
- `ARCHIVETUI_LEGACY_SQLITE_PATH` (default `archive-metadata.db`)

First startup can import legacy SQLite data once when `ARCHIVETUI_LEGACY_SQLITE_PATH` points to an existing file.

## 6. Runtime Modes and Commands

Set env vars in your shell before launching manually:

```powershell
$env:ARCHIVETUI_DB_HOST = '127.0.0.1'
$env:ARCHIVETUI_DB_PORT = '5432'
$env:ARCHIVETUI_DB_NAME = 'archivetui'
$env:ARCHIVETUI_DB_USER = 'archivetui'
$env:ARCHIVETUI_DB_PASSWORD = 'change-this-password'
$env:ARCHIVETUI_DB_SSLMODE = 'Disable'
$env:ARCHIVETUI_LEGACY_SQLITE_PATH = 'C:\Apps\ArchiveTUI\data\archive-metadata.db'
```

### Interactive mode

```powershell
cd C:\Apps\ArchiveTUI\app
dotnet .\ArchiveTUI.Cli.dll
```

### Headless one-shot mode

```powershell
dotnet .\ArchiveTUI.Cli.dll "D:\Source" "E:\Backup1" "F:\Backup2" --headless
```

### Headless watch mode

```powershell
dotnet .\ArchiveTUI.Cli.dll "D:\Source" "E:\Backup1" --verify-sample 2 --enable-age-verify --headless --watch
```

### Useful flags

- `--verify-sample <0-100>`
- `--enable-age-verify` / `--disable-age-verify`
- `--headless`
- `--watch`

## 7. Logging

Use redirection in headless runs:

```powershell
$ts = Get-Date -Format "yyyyMMdd-HHmmss"
dotnet C:\Apps\ArchiveTUI\app\ArchiveTUI.Cli.dll "D:\Source" "E:\Backup1" --headless *>> "C:\Apps\ArchiveTUI\logs\run-$ts.log"
```

## 8. Autostart with Task Scheduler (Recommended)

Use the installer script:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-ArchiveTUIWindowsTask.ps1 `
  -SourceRoot "D:\Source" `
  -DestinationRoots "E:\Backup1","F:\Backup2" `
  -DeployRoot "C:\Apps\ArchiveTUI" `
  -TaskName "ArchiveTUI Watch" `
  -DbUser "archivetui" `
  -DbPassword "change-this-password" `
  -EnsurePostgresContainer
```

This will:

1. Publish the app.
2. Create app/data/log directories.
3. Copy compose file to deploy root.
4. Register startup task with required `ARCHIVETUI_DB_*` env vars.
5. Optionally ensure PostgreSQL container is up before each run (`-EnsurePostgresContainer`).

To remove task:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Remove-ArchiveTUIWindowsTask.ps1 -TaskName "ArchiveTUI Watch"
```

## 9. Upgrade and Rollback

### Upgrade

1. Stop scheduled task/process.
2. Backup PostgreSQL volume:

```powershell
docker run --rm -v archivetui_pgdata:/volume -v ${PWD}:/backup alpine sh -c "tar czf /backup/archivetui_pgdata-$(date +%Y%m%d%H%M%S).tgz -C /volume ."
```

3. Publish and deploy new app build.
4. Restart scheduled task.

### Rollback

1. Stop scheduled task/process.
2. Repoint task to prior app build.
3. Restore PostgreSQL volume backup only if needed.

## 10. Validation Checklist

1. PostgreSQL container is healthy.
2. App starts without DB errors.
3. Backup runs and files copy to destinations.
4. Watch mode reacts to source changes.
5. Restarting container keeps metadata.
6. No unexpected listening ports from app process.

## 11. Troubleshooting

### Missing env vars

- Ensure `ARCHIVETUI_DB_USER` and `ARCHIVETUI_DB_PASSWORD` are set in run context.

### DB connection failures

- Verify container status: `docker compose -f .\docker-compose.postgres.yml ps`.
- Verify host/port/user/password values.

### Legacy import did not run

- Import only runs once on first startup when PostgreSQL tables are empty and legacy file exists.
- Check `ARCHIVETUI_LEGACY_SQLITE_PATH` points to the expected file.

### Watch mode not triggering

- Confirm `--watch` is set.
- Confirm source path and account permissions.

## 12. Security Notes

1. Use strong DB passwords.
2. Restrict Docker/host access.
3. Use dedicated least-privilege account for scheduled task where possible.
4. Protect logs and metadata because they may contain sensitive filesystem details.
