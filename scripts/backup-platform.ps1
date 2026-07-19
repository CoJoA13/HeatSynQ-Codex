param(
    [Parameter(Mandatory = $true)]
    [string]$Destination,
    [Parameter(Mandatory = $true)]
    [string]$OffServerDestination,
    [string]$StatusPath = "C:\ProgramData\HeatSynQ\last-successful-backup.txt",
    [ValidateRange(1, 3650)]
    [int]$RetentionDays = 14,
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
$resolvedOffServerDestination = [System.IO.Path]::GetFullPath($OffServerDestination)
$resolvedStatusPath = [System.IO.Path]::GetFullPath($StatusPath)
[System.IO.Directory]::CreateDirectory($resolvedDestination) | Out-Null
[System.IO.Directory]::CreateDirectory($resolvedOffServerDestination) | Out-Null
[System.IO.Directory]::CreateDirectory(
    [System.IO.Path]::GetDirectoryName($resolvedStatusPath)) | Out-Null
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

    $offServerArchive = Join-Path $resolvedOffServerDestination ([System.IO.Path]::GetFileName($archive))
    Copy-Item -LiteralPath $archive -Destination $offServerArchive
    & $SevenZip t -p"$env:HEATSYNQ_BACKUP_PASSWORD" $offServerArchive
    if ($LASTEXITCODE -ne 0) { throw "Off-server backup integrity test failed with exit code $LASTEXITCODE." }

    $cutoff = [DateTimeOffset]::UtcNow.AddDays(-$RetentionDays)
    Get-ChildItem -LiteralPath $resolvedDestination -Filter "heatsynq-platform-*.7z" -File |
        Where-Object { $_.LastWriteTimeUtc -lt $cutoff.UtcDateTime } |
        Remove-Item -Force

    [System.IO.File]::WriteAllText(
        $resolvedStatusPath,
        [DateTimeOffset]::UtcNow.ToString("O"))
    Write-Output $offServerArchive
}
finally {
    if (Test-Path -LiteralPath $temporaryDump) {
        Remove-Item -LiteralPath $temporaryDump -Force
    }
}
