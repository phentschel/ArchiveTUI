[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string[]]$DestinationRoots,

    [string]$DeployRoot = 'C:\Apps\ArchiveTUI',
    [string]$TaskName = 'ArchiveTUI Watch',
    [int]$VerifySamplePercent = 2,
    [string]$DbHost = '127.0.0.1',
    [int]$DbPort = 5432,
    [string]$DbName = 'archivetui',
    [Parameter(Mandatory = $true)]
    [string]$DbUser,
    [Parameter(Mandatory = $true)]
    [string]$DbPassword,
    [string]$DbSslMode = 'Disable',
    [string]$LegacySqlitePath = 'archive-metadata.db',
    [switch]$EnsurePostgresContainer,
    [switch]$DisableAgeVerify,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

if (-not $DestinationRoots -or $DestinationRoots.Count -eq 0) {
    throw 'At least one destination root is required.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectFile = Join-Path $repoRoot 'src\ArchiveTUI.Cli\ArchiveTUI.Cli.csproj'

$appDir = Join-Path $DeployRoot 'app'
$dataDir = Join-Path $DeployRoot 'data'
$logDir = Join-Path $DeployRoot 'logs'
$runnerPath = Join-Path $DeployRoot 'run-archivetui-watch.ps1'
$composeSource = Join-Path $repoRoot 'docker-compose.postgres.yml'
$composeTarget = Join-Path $DeployRoot 'docker-compose.postgres.yml'

New-Item -ItemType Directory -Force -Path $appDir, $dataDir, $logDir | Out-Null

if (Test-Path $composeSource) {
    Copy-Item -Path $composeSource -Destination $composeTarget -Force
}

if ([System.IO.Path]::IsPathRooted($LegacySqlitePath)) {
    $resolvedLegacySqlitePath = $LegacySqlitePath
} else {
    $resolvedLegacySqlitePath = Join-Path $dataDir $LegacySqlitePath
}

$dotnetCmd = Get-Command dotnet -ErrorAction Stop
$dotnetExe = $dotnetCmd.Source

Write-Host "Using dotnet: $dotnetExe"
Write-Host "Publishing app to: $appDir"

$publishArgs = @(
    'publish',
    $projectFile,
    '-c', 'Release',
    '-o', $appDir
)

if ($SelfContained) {
    $publishArgs += @('-r', 'win-x64', '--self-contained', 'true', '/p:PublishSingleFile=true')
}

& $dotnetExe @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$appDll = Join-Path $appDir 'ArchiveTUI.Cli.dll'
$appExe = Join-Path $appDir 'ArchiveTUI.Cli.exe'

$runtimeArgs = @()
if ($SelfContained -and (Test-Path $appExe)) {
    $runtimeArgs += $appExe
} else {
    if (-not (Test-Path $appDll)) {
        throw "Published DLL not found: $appDll"
    }
    $runtimeArgs += $appDll
}

$runtimeArgs += $SourceRoot
$runtimeArgs += $DestinationRoots
$runtimeArgs += '--verify-sample'
$runtimeArgs += [string]([Math]::Clamp($VerifySamplePercent, 0, 100))

if ($DisableAgeVerify) {
    $runtimeArgs += '--disable-age-verify'
} else {
    $runtimeArgs += '--enable-age-verify'
}

$runtimeArgs += '--headless'
$runtimeArgs += '--watch'

$escapedDbHost = $DbHost.Replace("'", "''")
$escapedDbName = $DbName.Replace("'", "''")
$escapedDbUser = $DbUser.Replace("'", "''")
$escapedDbPassword = $DbPassword.Replace("'", "''")
$escapedDbSslMode = $DbSslMode.Replace("'", "''")
$escapedLegacySqlitePath = $resolvedLegacySqlitePath.Replace("'", "''")

$runnerContent = @"
`$ErrorActionPreference = 'Stop'
`$logDir = '$logDir'
`$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
`$logFile = Join-Path `$logDir "watch-`$stamp.log"
`$env:ARCHIVETUI_DB_HOST = '$escapedDbHost'
`$env:ARCHIVETUI_DB_PORT = '$DbPort'
`$env:ARCHIVETUI_DB_NAME = '$escapedDbName'
`$env:ARCHIVETUI_DB_USER = '$escapedDbUser'
`$env:ARCHIVETUI_DB_PASSWORD = '$escapedDbPassword'
`$env:ARCHIVETUI_DB_SSLMODE = '$escapedDbSslMode'
`$env:ARCHIVETUI_LEGACY_SQLITE_PATH = '$escapedLegacySqlitePath'

"@

if ($EnsurePostgresContainer) {
    $runnerContent += @"
if (Get-Command docker -ErrorAction SilentlyContinue) {
    if (Test-Path '$composeTarget') {
        docker compose -f '$composeTarget' up -d postgres
    }
}

"@
}

if ($SelfContained -and (Test-Path $appExe)) {
    $runnerContent += "& '$appExe'"
} else {
    $runnerContent += "& '$dotnetExe'"
}

foreach ($arg in $runtimeArgs) {
    $escaped = $arg.Replace("'", "''")
    $runnerContent += " '$escaped'"
}

$runnerContent += " *>> `$logFile`n"

Set-Content -Path $runnerPath -Value $runnerContent -Encoding ascii

$taskAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$runnerPath`""
$taskTrigger = New-ScheduledTaskTrigger -AtStartup
$taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
$taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask -TaskName $TaskName -Action $taskAction -Trigger $taskTrigger -Settings $taskSettings -Principal $taskPrincipal | Out-Null
Start-ScheduledTask -TaskName $TaskName

Write-Host "Installed task '$TaskName'."
Write-Host "Deploy root: $DeployRoot"
Write-Host "Runner script: $runnerPath"
Write-Host "PostgreSQL: $DbHost`:$DbPort/$DbName"
Write-Host "Legacy SQLite import path: $resolvedLegacySqlitePath"
Write-Host "Log dir: $logDir"
Write-Host "Task account: SYSTEM"
Write-Host "If you use network shares, change the task account to a domain/local user with share permissions."
