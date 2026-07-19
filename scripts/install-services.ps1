param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$WebServiceName = "HeatSynQWeb",
    [string]$WorkerServiceName = "HeatSynQWorker"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath($InstallRoot)
$webExecutable = Join-Path $root "Web\HeatSynQ.Web.exe"
$workerExecutable = Join-Path $root "Worker\HeatSynQ.Worker.exe"

foreach ($executable in @($webExecutable, $workerExecutable)) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published service executable not found: $executable"
    }
}

if (-not (Get-Service -Name $WebServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $WebServiceName -BinaryPathName "`"$webExecutable`"" `
        -DisplayName "HeatSynQ Web" -StartupType Automatic
}
if (-not (Get-Service -Name $WorkerServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $WorkerServiceName -BinaryPathName "`"$workerExecutable`"" `
        -DisplayName "HeatSynQ Background Worker" -StartupType Automatic
}

& sc.exe failure $WebServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
if ($LASTEXITCODE -ne 0) { throw "Could not configure web service recovery." }
& sc.exe failure $WorkerServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
if ($LASTEXITCODE -ne 0) { throw "Could not configure worker service recovery." }
