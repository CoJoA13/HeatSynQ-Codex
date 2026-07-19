param(
    [Parameter(Mandatory = $true)]
    [string]$Archive,
    [Parameter(Mandatory = $true)]
    [string]$TargetDatabaseUrl,
    [Parameter(Mandatory = $true)]
    [string]$TargetManagedStoragePath,
    [switch]$ReplaceManagedStorage,
    [string]$SevenZip = "7z.exe",
    [string]$PgRestore = "pg_restore.exe"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:HEATSYNQ_BACKUP_PASSWORD)) {
    throw "HEATSYNQ_BACKUP_PASSWORD is required."
}

$resolvedArchive = [System.IO.Path]::GetFullPath($Archive)
$resolvedManagedStorage = [System.IO.Path]::GetFullPath($TargetManagedStoragePath)
if (-not (Test-Path -LiteralPath $resolvedArchive -PathType Leaf)) {
    throw "Backup archive does not exist: $resolvedArchive"
}

$restoreRoot = Join-Path $env:TEMP "heatsynq-restore-$([Guid]::NewGuid().ToString('N'))"
$managedStorageParent = [System.IO.Path]::GetDirectoryName($resolvedManagedStorage)
if ([string]::IsNullOrWhiteSpace($managedStorageParent)) {
    throw "Target managed storage must be a directory beneath a parent path."
}
$managedStorageStaging = Join-Path $managedStorageParent ".heatsynq-managed-restore-$([Guid]::NewGuid().ToString('N'))"
$managedStorageBackup = Join-Path $managedStorageParent ".heatsynq-managed-backup-$([Guid]::NewGuid().ToString('N'))"

function Restore-ManagedStorageBackup {
    if (-not (Test-Path -LiteralPath $managedStorageBackup -PathType Container)) {
        return
    }
    if (Test-Path -LiteralPath $resolvedManagedStorage) {
        Remove-Item -LiteralPath $resolvedManagedStorage -Recurse -Force
    }
    Move-Item -LiteralPath $managedStorageBackup -Destination $resolvedManagedStorage
}

[System.IO.Directory]::CreateDirectory($restoreRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($managedStorageParent) | Out-Null

try {
    & $SevenZip t -p"$env:HEATSYNQ_BACKUP_PASSWORD" $resolvedArchive
    if ($LASTEXITCODE -ne 0) { throw "Backup integrity test failed with exit code $LASTEXITCODE." }

    & $SevenZip x -y -p"$env:HEATSYNQ_BACKUP_PASSWORD" "-o$restoreRoot" $resolvedArchive
    if ($LASTEXITCODE -ne 0) { throw "Backup extraction failed with exit code $LASTEXITCODE." }

    $dump = Get-ChildItem -LiteralPath $restoreRoot -Filter "*.dump" -File |
        Select-Object -First 1
    if ($null -eq $dump) { throw "The archive did not contain a PostgreSQL custom-format dump." }
    $managedFiles = Join-Path $restoreRoot "managed-files"
    if (-not (Test-Path -LiteralPath $managedFiles -PathType Container)) {
        throw "The archive did not contain managed file storage."
    }

    if (Test-Path -LiteralPath $resolvedManagedStorage) {
        $hasExistingFiles = $null -ne (
            Get-ChildItem -LiteralPath $resolvedManagedStorage -Force |
                Select-Object -First 1)
        if ($hasExistingFiles -and -not $ReplaceManagedStorage) {
            throw "Target managed storage is not empty. Use -ReplaceManagedStorage for a controlled replacement."
        }
    }

    [System.IO.Directory]::CreateDirectory($managedStorageStaging) | Out-Null
    Get-ChildItem -LiteralPath $managedFiles -Force |
        Copy-Item -Destination $managedStorageStaging -Recurse -Force

    & $PgRestore --dbname=$TargetDatabaseUrl --clean --if-exists --exit-on-error $dump.FullName
    if ($LASTEXITCODE -ne 0) { throw "pg_restore failed with exit code $LASTEXITCODE." }

    try {
        if (Test-Path -LiteralPath $resolvedManagedStorage) {
            Move-Item -LiteralPath $resolvedManagedStorage -Destination $managedStorageBackup
        }
        Move-Item -LiteralPath $managedStorageStaging -Destination $resolvedManagedStorage
    }
    catch {
        Restore-ManagedStorageBackup
        throw
    }
    if (Test-Path -LiteralPath $managedStorageBackup) {
        Remove-Item -LiteralPath $managedStorageBackup -Recurse -Force
    }

    Write-Output "Restore completed from $resolvedArchive."
}
finally {
    if (Test-Path -LiteralPath $managedStorageStaging) {
        Remove-Item -LiteralPath $managedStorageStaging -Recurse -Force
    }
    if (Test-Path -LiteralPath $restoreRoot) {
        Remove-Item -LiteralPath $restoreRoot -Recurse -Force
    }
}
