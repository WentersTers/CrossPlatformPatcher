# w02-tooling: the sidecar's runtime (Python 3.12, all-users, on PATH).
# Pinned MSI from python.org (host egress proven). No Vosk model here —
# the model lands at sidecar stage. .NET Framework is inbox; nothing to do.
$ErrorActionPreference = "Stop"
# Headless provisioner sessions have no console buffer; download progress
# rendering throws (ReadConsoleOutput 0x5). Silence it (Win10 build finding).
$ProgressPreference = "SilentlyContinue"

$ver = "3.12.7"
$msi = "$env:TEMP\python-$ver-amd64.exe"
Invoke-WebRequest -Uri "https://www.python.org/ftp/python/$ver/python-$ver-amd64.exe" -OutFile $msi
Start-Process -FilePath $msi -ArgumentList "/quiet", "InstallAllUsers=1", "PrependPath=1", "Include_test=0" -Wait
Remove-Item $msi -Force

# Fresh sessions inherit PATH from the registry; assert in this one via refresh.
$py = "C:\Program Files\Python312\python.exe"
if (-not (Test-Path $py)) { throw "python missing at $py" }
& $py --version
& $py -m pip --version | Out-Null
Write-Output "w02-tooling done"
