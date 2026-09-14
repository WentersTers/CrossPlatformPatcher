# w03-assert: fail the BUILD on what is assertable at build time
# (ubuntu 03-assert.sh equivalent, runs LAST so it checks final state).
$ErrorActionPreference = "Stop"
$fail = @()

$sshd = Get-Service -Name sshd -ErrorAction SilentlyContinue
if (-not $sshd -or $sshd.Status -ne "Running") { $fail += "sshd not running" }
if ((Get-Service sshd).StartType -ne "Automatic") { $fail += "sshd not automatic" }

if (-not (Test-Path "C:\Program Files\Python312\python.exe")) { $fail += "python missing" }

$wl = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
if ((Get-ItemProperty $wl).AutoAdminLogon -ne "1") { $fail += "autologon off" }

$au = (Get-ItemProperty "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU" -ErrorAction SilentlyContinue).AUOptions
if ($au -ne 2) { $fail += "AUOptions=$au (want 2)" }

$mon = (powercfg /query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE) 2>$null | Select-String "Current AC Power Setting Index"
if ($mon -match "0x00000000") { } else { $fail += "monitor timeout not zero" }

$freeGB = [math]::Round((Get-PSDrive C).Free / 1GB, 1)
if ($freeGB -lt 30) { $fail += "C: free ${freeGB}GB (<30)" }

if ($fail.Count -gt 0) { throw ("ASSERT-FAIL: " + ($fail -join "; ")) }
Write-Output "w03-assert done (C: free ${freeGB}GB)"
