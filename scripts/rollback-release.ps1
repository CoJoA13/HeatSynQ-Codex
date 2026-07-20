param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$WebServiceName = "HeatSynQWeb",
    [string]$WorkerServiceName = "HeatSynQWorker"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath($InstallRoot)
$stable = Join-Path $root "current"
$previousFile = Join-Path $root "previous-release.txt"
if (-not (Test-Path -LiteralPath $previousFile -PathType Leaf)) {
    throw "No previous release is recorded."
}
$previous = [System.IO.File]::ReadAllText($previousFile).Trim()
if (-not (Test-Path -LiteralPath $previous -PathType Container)) {
    throw "Recorded previous release is unavailable: $previous"
}

Stop-Service -Name $WorkerServiceName -ErrorAction Stop
Stop-Service -Name $WebServiceName -ErrorAction Stop
if (Test-Path -LiteralPath $stable) {
    Remove-Item -LiteralPath $stable -Force
}
New-Item -ItemType Junction -Path $stable -Target $previous | Out-Null
Start-Service -Name $WebServiceName
Start-Service -Name $WorkerServiceName
Write-Output "Application binaries rolled back to $previous. Database restore, if required, must follow the release notes."
