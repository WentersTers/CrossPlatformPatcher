# w04-cleanup: generalize-lite (no sysprep — snapshot lineage is the pattern).
# Temp files, setup logs, download cache. Keep drivers and servicing state.
$ErrorActionPreference = "Continue"

Remove-Item -Path "C:\Windows\Temp\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "C:\Windows\Panther\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path '$Windows.~BT' -Recurse -Force -ErrorAction SilentlyContinue
Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
Remove-Item -Path "C:\Windows\SoftwareDistribution\Download\*" -Recurse -Force -ErrorAction SilentlyContinue
Start-Service -Name wuauserv -ErrorAction SilentlyContinue
wevtutil el | ForEach-Object { wevtutil cl "$_" 2>$null }

Write-Output "w04-cleanup done"
