param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseSource,
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$VerifiedBackupArchive,
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
$maintenance = Join-Path $root "maintenance.flag"
[System.IO.Directory]::CreateDirectory($root) | Out-Null
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
    $previous = if (Test-Path -LiteralPath $stable) {
        (Get-Item -LiteralPath $stable).Target
    } else { $null }
    if (Test-Path -LiteralPath $stable) {
        Remove-Item -LiteralPath $stable -Force
    }
    New-Item -ItemType Junction -Path $stable -Target $release | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $root "previous-release.txt"),
        [string]$previous)
    Start-Service -Name $WebServiceName
    Start-Service -Name $WorkerServiceName
}
catch {
    Write-Error "Deployment failed. Services remain controlled for documented rollback: $($_.Exception.Message)"
    throw
}
finally {
    if (Test-Path -LiteralPath $maintenance) {
        Remove-Item -LiteralPath $maintenance -Force
    }
}
