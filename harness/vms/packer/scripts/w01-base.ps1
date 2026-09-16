# w01-base: golden pins (wallpaper, power, lock, update, consumer content).
# Runs over SSH as an admin user; LocalAccountTokenFilterPolicy (answer
# file) lets these HKLM writes through. Idempotent.
$ErrorActionPreference = "Stop"

# Wallpaper: solid black (the visual-pin equivalent; screenshot-stable).
Set-ItemProperty -Path "HKCU:\Control Panel\Desktop" -Name WallPaper -Value ""
Set-ItemProperty -Path "HKCU:\Control Panel\Colors" -Name Background -Value "0 0 0"
RUNDLL32.EXE user32.dll,UpdatePerUserSystemParameters 1, $true

# Win10 LTSC lesson (2026-09-15): the console autologon user is
# Administrator, not the SSH build user, so an HKCU-only pin leaves the
# default blue wallpaper on the console. Pin every loaded user hive plus
# .DEFAULT so the console session and future profiles inherit solid black.
foreach ($hive in (Get-ChildItem Registry::HKEY_USERS -ErrorAction SilentlyContinue)) {
    if ($hive.Name -match '_Classes$') { continue }
    $desk = "$($hive.Name)\Control Panel\Desktop"
    $cols = "$($hive.Name)\Control Panel\Colors"
    try { Set-ItemProperty -Path "Registry::$desk" -Name WallPaper -Value "" -ErrorAction Stop } catch {}
    try { Set-ItemProperty -Path "Registry::$cols" -Name Background -Value "0 0 0" -ErrorAction Stop } catch {}
}

# Power: never sleep/hibernate/blank on AC (the noblank equivalent).
powercfg /change standby-timeout-ac 0 | Out-Null
powercfg /change monitor-timeout-ac 0 | Out-Null
powercfg /hibernate off | Out-Null

# Lock screen off (console must stay on desktop for the harness).
$personal = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization"
New-Item -Path $personal -Force | Out-Null
Set-ItemProperty -Path $personal -Name NoLockScreen -Value 1 -Type DWord

# Update: notify-only, no auto-reboot (the updater-disabled equivalent).
$wu = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"
New-Item -Path $wu -Force | Out-Null
Set-ItemProperty -Path $wu -Name AUOptions -Value 2 -Type DWord
Set-ItemProperty -Path $wu -Name NoAutoRebootWithLoggedOnUsers -Value 1 -Type DWord

# No consumer-feature downloads (eval purity: no suggested apps).
$cloud = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent"
New-Item -Path $cloud -Force | Out-Null
Set-ItemProperty -Path $cloud -Name DisableWindowsConsumerFeatures -Value 1 -Type DWord

# First-logon animation off (faster console readiness).
Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name EnableFirstLogonAnimation -Value 0 -Type DWord

Write-Output "w01-base done"
