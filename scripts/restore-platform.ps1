param(
    [Parameter(Mandatory = $true)]
    [string]$Archive,
    [Parameter(Mandatory = $true)]
    [string]$TargetDatabaseUrl,
    [Parameter(Mandatory = $true)]
    [string]$MaintenanceDatabaseUrl,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$TargetDatabaseName,
    [Parameter(Mandatory = $true)]
    [string]$TargetManagedStoragePath,
    [switch]$ReplaceManagedStorage,
    [string]$SevenZip = "7z.exe",
    [string]$PgDump = "pg_dump.exe",
    [string]$PgDropDb = "dropdb.exe",
    [string]$Psql = "psql.exe",
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
$preRestoreDump = Join-Path $restoreRoot "pre-restore.dump"
$databaseRestoreStarted = $false
$preserveRestoreArtifacts = $false

function Assert-TargetDatabaseIdentity {
    $databaseNameOutput = @(
        & $Psql --dbname=$TargetDatabaseUrl --tuples-only --no-align `
            --set=ON_ERROR_STOP=1 --command="SELECT current_database();"
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Could not verify the target database name (exit code $LASTEXITCODE)."
    }
    $databaseName = ($databaseNameOutput -join "").Trim()
    if (-not [string]::Equals(
        $databaseName,
        $TargetDatabaseName,
        [StringComparison]::Ordinal)) {
        throw "TargetDatabaseName '$TargetDatabaseName' does not match the database addressed by TargetDatabaseUrl ('$databaseName')."
    }
}

function Restore-ManagedStorageBackup {
    if (-not (Test-Path -LiteralPath $managedStorageBackup -PathType Container)) {
        return
    }
    if (Test-Path -LiteralPath $resolvedManagedStorage) {
        Remove-Item -LiteralPath $resolvedManagedStorage -Recurse -Force
    }
    Move-Item -LiteralPath $managedStorageBackup -Destination $resolvedManagedStorage
}

function Restore-PreRestoreDatabase {
    & $PgDropDb --maintenance-db=$MaintenanceDatabaseUrl --force --if-exists `
        $TargetDatabaseName
    if ($LASTEXITCODE -ne 0) {
        throw "Pre-restore database removal failed with exit code $LASTEXITCODE."
    }
    & $PgRestore --dbname=$MaintenanceDatabaseUrl --create --exit-on-error `
        $preRestoreDump
    if ($LASTEXITCODE -ne 0) {
        throw "Pre-restore database rollback failed with exit code $LASTEXITCODE."
    }
}

Assert-TargetDatabaseIdentity

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

    & $PgDump --dbname=$TargetDatabaseUrl --format=custom --create `
        --file=$preRestoreDump
    if ($LASTEXITCODE -ne 0) {
        throw "Pre-restore safety backup failed with exit code $LASTEXITCODE."
    }

    try {
        $databaseRestoreStarted = $true
        & $PgRestore --dbname=$TargetDatabaseUrl --clean --if-exists --exit-on-error $dump.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "pg_restore failed with exit code $LASTEXITCODE."
        }
        if (Test-Path -LiteralPath $resolvedManagedStorage) {
            Move-Item -LiteralPath $resolvedManagedStorage -Destination $managedStorageBackup
        }
        Move-Item -LiteralPath $managedStorageStaging -Destination $resolvedManagedStorage
    }
    catch {
        $restoreFailure = $_.Exception
        $rollbackFailures = @()
        if ($databaseRestoreStarted) {
            try {
                Restore-PreRestoreDatabase
            }
            catch {
                $rollbackFailures += $_.Exception.Message
            }
        }
        try {
            Restore-ManagedStorageBackup
        }
        catch {
            $rollbackFailures += $_.Exception.Message
        }
        if ($rollbackFailures.Count -gt 0) {
            $preserveRestoreArtifacts = $true
            throw "Restore failed: $($restoreFailure.Message) Rollback also failed: $($rollbackFailures -join ' | '). Recovery artifacts were retained at $restoreRoot."
        }
        throw $restoreFailure
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
    if (-not $preserveRestoreArtifacts -and (Test-Path -LiteralPath $restoreRoot)) {
        Remove-Item -LiteralPath $restoreRoot -Recurse -Force
    }
}
