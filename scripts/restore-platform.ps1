param(
    [Parameter(Mandatory = $true)]
    [string]$Archive,
    [Parameter(Mandatory = $true)]
    [string]$TargetDatabaseUrl,
    [string]$SevenZip = "7z.exe",
    [string]$PgRestore = "pg_restore.exe"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:HEATSYNQ_BACKUP_PASSWORD)) {
    throw "HEATSYNQ_BACKUP_PASSWORD is required."
}

$resolvedArchive = [System.IO.Path]::GetFullPath($Archive)
if (-not (Test-Path -LiteralPath $resolvedArchive -PathType Leaf)) {
    throw "Backup archive does not exist: $resolvedArchive"
}

$restoreRoot = Join-Path $env:TEMP "heatsynq-restore-$([Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($restoreRoot) | Out-Null

try {
    & $SevenZip t -p"$env:HEATSYNQ_BACKUP_PASSWORD" $resolvedArchive
    if ($LASTEXITCODE -ne 0) { throw "Backup integrity test failed with exit code $LASTEXITCODE." }

    & $SevenZip x -y -p"$env:HEATSYNQ_BACKUP_PASSWORD" "-o$restoreRoot" $resolvedArchive
    if ($LASTEXITCODE -ne 0) { throw "Backup extraction failed with exit code $LASTEXITCODE." }

    $dump = Get-ChildItem -LiteralPath $restoreRoot -Filter "*.dump" -File |
        Select-Object -First 1
    if ($null -eq $dump) { throw "The archive did not contain a PostgreSQL custom-format dump." }

    & $PgRestore --dbname=$TargetDatabaseUrl --clean --if-exists --exit-on-error $dump.FullName
    if ($LASTEXITCODE -ne 0) { throw "pg_restore failed with exit code $LASTEXITCODE." }

    Write-Output "Restore completed from $resolvedArchive."
}
finally {
    if (Test-Path -LiteralPath $restoreRoot) {
        Remove-Item -LiteralPath $restoreRoot -Recurse -Force
    }
}
