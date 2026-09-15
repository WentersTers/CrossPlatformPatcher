# w02b-runtime: native-code runtime the OWW stack needs (VC++ redist x64).
# The installer is file-provisioned from VALIDATED bytes (Win11 census tree,
# md5 486f81facf798678c3244c7cf35a557f, staged on host — see template), never
# downloaded at build time. The mingw trio (libgcc/libstdc++/winpthread)
# rides app/sidecar staging, not the golden (layering: golden is system).
# Win10-only stage (Win11 golden predates it; wire deliberately, not by edit).
$ErrorActionPreference = "Stop"

$inst = "C:\Windows\Temp\packer-runtime\vc_redist.x64.exe"
if (-not (Test-Path $inst)) { throw "runtime installer missing at $inst" }
Start-Process -FilePath $inst -ArgumentList "/install", "/quiet", "/norestart" -Wait
$rt = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" -ErrorAction SilentlyContinue
if (-not $rt) { throw "VC++ x64 runtime key absent after install" }
$ver = [version]($rt.Version -replace '^v', '')
if ($ver -lt [version]"14.44") { throw "VC++ x64 too old: $($rt.Version) (want >= 14.44)" }
Remove-Item "C:\Windows\Temp\packer-runtime" -Recurse -Force
Write-Output "w02b-runtime done ($($rt.Version))"
