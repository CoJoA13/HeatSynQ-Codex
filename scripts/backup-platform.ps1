param(
    [Parameter(Mandatory = $true)]
    [string]$Destination,
    [string]$PgDump = "pg_dump.exe",
    [string]$SevenZip = "7z.exe"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:HEATSYNQ_DATABASE_URL)) {
    throw "HEATSYNQ_DATABASE_URL is required."
}

if ([string]::IsNullOrWhiteSpace($env:HEATSYNQ_BACKUP_PASSWORD)) {
    throw "HEATSYNQ_BACKUP_PASSWORD is required."
}

$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
[System.IO.Directory]::CreateDirectory($resolvedDestination) | Out-Null
$timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
$temporaryDump = Join-Path $env:TEMP "heatsynq-platform-$timestamp.dump"
$archive = Join-Path $resolvedDestination "heatsynq-platform-$timestamp.7z"

try {
    & $PgDump --dbname=$env:HEATSYNQ_DATABASE_URL --format=custom --file=$temporaryDump
    if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE." }

    & $SevenZip a -t7z -mhe=on -p"$env:HEATSYNQ_BACKUP_PASSWORD" $archive $temporaryDump
    if ($LASTEXITCODE -ne 0) { throw "Backup encryption failed with exit code $LASTEXITCODE." }

    & $SevenZip t -p"$env:HEATSYNQ_BACKUP_PASSWORD" $archive
    if ($LASTEXITCODE -ne 0) { throw "Backup integrity test failed with exit code $LASTEXITCODE." }

    Write-Output $archive
}
finally {
    if (Test-Path -LiteralPath $temporaryDump) {
        Remove-Item -LiteralPath $temporaryDump -Force
    }
}
