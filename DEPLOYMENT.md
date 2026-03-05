# ArchiveTUI Deployment Guide

This guide describes how to deploy ArchiveTUI with PostgreSQL running in Docker and persistent data storage.

## 1. Architecture

- ArchiveTUI app: .NET 9 CLI/TUI process
- Metadata DB: PostgreSQL 16 in Docker
- Persistence: Docker named volume `archivetui_pgdata`
- Optional first-run migration source: legacy SQLite file via `ARCHIVETUI_LEGACY_SQLITE_PATH`

## 2. Prerequisites

Install:

1. Docker + Docker Compose
2. .NET 9 SDK/runtime (SDK for build, runtime for framework-dependent execution)
3. PowerShell (Windows) or shell of choice (macOS/Linux)

Verify:

```bash
docker --version
docker compose version
dotnet --info
```

## 3. Configure Environment

Create runtime env file from template:

```bash
cp .env.example .env
```

Set at minimum:

- `ARCHIVETUI_DB_USER`
- `ARCHIVETUI_DB_PASSWORD`

Default values are suitable for local deployment:

- `ARCHIVETUI_DB_HOST=127.0.0.1`
- `ARCHIVETUI_DB_PORT=5432`
- `ARCHIVETUI_DB_NAME=archivetui`
- `ARCHIVETUI_DB_SSLMODE=Disable`
- `ARCHIVETUI_LEGACY_SQLITE_PATH=archive-metadata.db`

## 4. Start PostgreSQL

```bash
docker compose -f docker-compose.postgres.yml --env-file .env up -d
```

Validate health:

```bash
docker compose -f docker-compose.postgres.yml --env-file .env ps
```

## 5. Build and Publish

From repo root:

```bash
dotnet restore ArchiveTUI.sln
dotnet build ArchiveTUI.sln -c Release
dotnet publish src/ArchiveTUI.Cli/ArchiveTUI.Cli.csproj -c Release -o ./publish
```

## 6. Run ArchiveTUI

ArchiveTUI reads DB config from environment variables.

### Linux/macOS

```bash
set -a
source .env
set +a

./publish/ArchiveTUI.Cli /source/path /backup/path --headless
```

### Windows PowerShell

```powershell
Get-Content .env | ForEach-Object {
  if ($_ -match '^(?<k>[^#=]+)=(?<v>.*)$') {
    [Environment]::SetEnvironmentVariable($Matches.k.Trim(), $Matches.v.Trim(), 'Process')
  }
}

.\publish\ArchiveTUI.Cli.exe "D:\Source" "E:\Backup1" --headless
```

### Watch mode

```bash
./publish/ArchiveTUI.Cli /source/path /backup/path --headless --watch
```

## 7. One-Time Legacy SQLite Import

On startup, ArchiveTUI imports from SQLite only if all are true:

1. PostgreSQL metadata tables are empty
2. Migration marker `sqlite_import_v1` does not exist
3. File at `ARCHIVETUI_LEGACY_SQLITE_PATH` exists

Import is transactional and read-only for the SQLite file.

## 8. Backups and Rollback

### Backup PostgreSQL volume

```bash
docker run --rm -v archivetui_pgdata:/volume -v "$(pwd)":/backup alpine \
  sh -c "tar czf /backup/archivetui_pgdata-$(date +%Y%m%d%H%M%S).tgz -C /volume ."
```

### Rollback app version

1. Stop app process/task
2. Start previous app build
3. Keep PostgreSQL volume as-is unless DB rollback is required

## 9. Windows Scheduled Task Deployment

Use installer script:

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

## 10. Troubleshooting

### Missing required DB vars

ArchiveTUI fails fast if `ARCHIVETUI_DB_USER` or `ARCHIVETUI_DB_PASSWORD` is missing.

### Cannot connect to database

- Check container status with `docker compose ... ps`
- Validate host/port/user/password/db name
- Confirm `ARCHIVETUI_DB_SSLMODE` matches environment

### Import did not run

- Ensure PostgreSQL tables are empty
- Ensure `ARCHIVETUI_LEGACY_SQLITE_PATH` points to existing file
- Ensure marker `sqlite_import_v1` is absent
