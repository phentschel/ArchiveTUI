[CmdletBinding()]
param(
    [string]$TaskName = 'ArchiveTUI Watch'
)

$ErrorActionPreference = 'Stop'

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $existing) {
    Write-Host "Task '$TaskName' does not exist."
    exit 0
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Host "Removed task '$TaskName'."
