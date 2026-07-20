param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [Parameter(Mandatory = $true)]
    [PSCredential]$WebServiceCredential,
    [Parameter(Mandatory = $true)]
    [PSCredential]$WorkerServiceCredential,
    [string]$WebServiceName = "HeatSynQWeb",
    [string]$WorkerServiceName = "HeatSynQWorker"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath($InstallRoot)
$webExecutable = Join-Path $root "current\Web\HeatSynQ.Web.exe"
$workerExecutable = Join-Path $root "current\Worker\HeatSynQ.Worker.exe"

foreach ($executable in @($webExecutable, $workerExecutable)) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published service executable not found: $executable"
    }
}

function Assert-ExistingServiceIdentity {
    param(
        [string]$ServiceName,
        [PSCredential]$Credential
    )
    $escapedName = $ServiceName.Replace("'", "''")
    $service = Get-CimInstance Win32_Service -Filter "Name='$escapedName'"
    if ($null -eq $service) {
        throw "Could not inspect the existing $ServiceName service identity."
    }
    if (-not [string]::Equals(
            $service.StartName,
            $Credential.UserName,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$ServiceName already exists under '$($service.StartName)' instead of '$($Credential.UserName)'. Remove or explicitly reconfigure the service identity before continuing."
    }
}

$webService = Get-Service -Name $WebServiceName -ErrorAction SilentlyContinue
if ($webService) {
    Assert-ExistingServiceIdentity $WebServiceName $WebServiceCredential
}
else {
    New-Service -Name $WebServiceName -BinaryPathName "`"$webExecutable`"" `
        -DisplayName "HeatSynQ Web" -StartupType Automatic `
        -Credential $WebServiceCredential
}
$workerService = Get-Service -Name $WorkerServiceName -ErrorAction SilentlyContinue
if ($workerService) {
    Assert-ExistingServiceIdentity $WorkerServiceName $WorkerServiceCredential
}
else {
    New-Service -Name $WorkerServiceName -BinaryPathName "`"$workerExecutable`"" `
        -DisplayName "HeatSynQ Background Worker" -StartupType Automatic `
        -Credential $WorkerServiceCredential
}

& sc.exe failure $WebServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
if ($LASTEXITCODE -ne 0) { throw "Could not configure web service recovery." }
& sc.exe failure $WorkerServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
if ($LASTEXITCODE -ne 0) { throw "Could not configure worker service recovery." }
