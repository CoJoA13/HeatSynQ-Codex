param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseSource,
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$VerifiedBackupArchive,
    [Parameter(Mandatory = $true)]
    [string]$MaintenanceFlagPath,
    [string]$WebServiceName = "HeatSynQWeb",
    [string]$WorkerServiceName = "HeatSynQWorker"
)

$ErrorActionPreference = "Stop"
$source = [System.IO.Path]::GetFullPath($ReleaseSource)
$root = [System.IO.Path]::GetFullPath($InstallRoot)
$backup = [System.IO.Path]::GetFullPath($VerifiedBackupArchive)
if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) {
    throw "A verified pre-update backup archive is required."
}
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') {
    throw "Version contains unsupported characters."
}
$release = Join-Path $root "releases\$Version"
$stable = Join-Path $root "current"
$maintenance = [System.IO.Path]::GetFullPath($MaintenanceFlagPath)
$maintenanceParent = [System.IO.Path]::GetDirectoryName($maintenance)
if ([string]::IsNullOrWhiteSpace($maintenanceParent)) {
    throw "Maintenance flag must be beneath a parent directory."
}
$previousRelease = if (Test-Path -LiteralPath $stable) {
    [string](Get-Item -LiteralPath $stable).Target
} else { $null }
$deploymentSucceeded = $false
$junctionUpdateStarted = $false

function Restore-PreviousRelease {
    if (-not $junctionUpdateStarted -or [string]::IsNullOrWhiteSpace($previousRelease)) {
        return
    }
    if (Test-Path -LiteralPath $stable) {
        Remove-Item -LiteralPath $stable -Force
    }
    New-Item -ItemType Junction -Path $stable -Target $previousRelease | Out-Null
}

[System.IO.Directory]::CreateDirectory($root) | Out-Null
[System.IO.Directory]::CreateDirectory($maintenanceParent) | Out-Null
[System.IO.File]::WriteAllText($maintenance, "Update $Version in progress.")

try {
    Stop-Service -Name $WorkerServiceName -ErrorAction Stop
    Stop-Service -Name $WebServiceName -ErrorAction Stop
    if (Test-Path -LiteralPath $release) {
        throw "Release directory already exists: $release"
    }
    Copy-Item -LiteralPath $source -Destination $release -Recurse
    $migrationBundle = Join-Path $release "migrate.exe"
    if (-not (Test-Path -LiteralPath $migrationBundle -PathType Leaf)) {
        throw "The release does not contain the required migration bundle: $migrationBundle"
    }
    & $migrationBundle
    if ($LASTEXITCODE -ne 0) {
        throw "Database migration failed with exit code $LASTEXITCODE."
    }
    $junctionUpdateStarted = $true
    if (Test-Path -LiteralPath $stable) {
        Remove-Item -LiteralPath $stable -Force
    }
    New-Item -ItemType Junction -Path $stable -Target $release | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $root "previous-release.txt"),
        [string]$previousRelease)
    Start-Service -Name $WebServiceName
    (Get-Service -Name $WebServiceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    Start-Service -Name $WorkerServiceName
    (Get-Service -Name $WorkerServiceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    $deploymentSucceeded = $true
}
catch {
    Stop-Service -Name $WorkerServiceName -ErrorAction SilentlyContinue
    Stop-Service -Name $WebServiceName -ErrorAction SilentlyContinue
    Restore-PreviousRelease
    if (-not [string]::IsNullOrWhiteSpace($previousRelease)) {
        Start-Service -Name $WebServiceName -ErrorAction SilentlyContinue
        Start-Service -Name $WorkerServiceName -ErrorAction SilentlyContinue
    }
    Write-Error "Deployment failed. Maintenance mode remains active for verification: $($_.Exception.Message)"
    throw
}
finally {
    if ($deploymentSucceeded -and (Test-Path -LiteralPath $maintenance)) {
        Remove-Item -LiteralPath $maintenance -Force
    }
}
