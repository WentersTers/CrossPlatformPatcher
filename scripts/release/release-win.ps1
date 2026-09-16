# Release pipeline: Windows .exe first (v0.1.1 content, fully measured).
#
# Gates, in order (fail-fast; every gate echoes PASS/FAIL):
#   1. dotnet build (Release) must be error-free.
#   2. Full test suite must be green.
#   3. Single-file self-contained win-x64 publish.
#   4. Determinism: publish twice into separate dirs; byte-identical or FAIL.
#   5. Artifact audit (transformation-only boundary, mechanical):
#      a. embedded-resource manifest allowlist (no surprise blobs);
#      b. product-marker byte scan (no product bytes/fragments in the bundle).
#   6. Stage release dir: versioned exe, SHA-256 file, third-party notices,
#      VB-CABLE bundle (operator-staged Pack45) or download-on-demand note.
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File scripts/release/release-win.ps1 `
#     [-VBCableZip <path to VBCABLE_Driver_Pack45.zip>] [-SkipTests]
#
# VB-CABLE terms (vb-audio.com/Services/licensing.htm, verified 2026-09-16):
# base VB-CABLE package MAY be distributed/embedded (incl. silent install)
# with a free or commercial app while the donationware model stays
# applicable: the end user must be able to identify it as a VB-Audio app and
# be told its origin (www.vb-cable.com) plus its donationware nature.
# A+B/C+D packs must NOT be bundled. This pipeline bundles only the base
# Pack45 zip as-is and writes the attribution into THIRD-PARTY-NOTICES.txt.

param(
    [string]$VBCableZip = "",
    [string]$AppImagePath = "",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Version = "0.1.1"
$ReleaseDir = Join-Path $RepoRoot "release\v0.1.1"

function Gate($name, [scriptblock]$body) {
    Write-Host ""
    Write-Host "=== GATE: $name ==="
    & $body
    Write-Host "PASS: $name"
}

function Fail($msg) {
    Write-Host "FAIL: $msg" -ForegroundColor Red
    exit 1
}

Set-Location $RepoRoot

Gate "build Release error-free" {
    dotnet build CrossPlatformPatcher.csproj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "build errors" }
}

# NOTE: the test suite runs inside the determinism gate below (between the
# two publishes), not here -- see the comment there.

$PubA = Join-Path $env:TEMP "paicom-pub-A"
$PubB = Join-Path $env:TEMP "paicom-pub-B"
$PublishArgs = @("publish", "CrossPlatformPatcher.csproj", "-c", "Release",
    "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true",
    "--nologo", "-v", "q")

Gate "publish win-x64 single-file (A)" {
    if (Test-Path $PubA) { Remove-Item $PubA -Recurse -Force }
    dotnet @PublishArgs -o $PubA
    if ($LASTEXITCODE -ne 0) { Fail "publish A" }
}

Gate "determinism: publish twice byte-identical, test-interleaved (B)" {
    # The test suite runs BETWEEN the two publishes on purpose: a past
    # unexplained hash transition (E071->856E, no compiled-input change)
    # suggested drift across interleaved dotnet operations. If the tree is
    # truly deterministic, interleaving changes nothing; if not, this gate
    # fails loudly instead of shipping a hash the next rebuild cannot honor.
    if (-not $SkipTests) {
        dotnet test CrossPlatformPatcher.Tests\CrossPlatformPatcher.Tests.csproj -c Release --nologo 2>$null
        if ($LASTEXITCODE -ne 0) { Fail "test failures" }
        Write-Host "interleaved test suite green"
    } else {
        Write-Host "SKIP: interleaved tests (--SkipTests)"
    }
    if (Test-Path $PubB) { Remove-Item $PubB -Recurse -Force }
    dotnet @PublishArgs -o $PubB
    if ($LASTEXITCODE -ne 0) { Fail "publish B" }
    $hashA = (Get-FileHash (Join-Path $PubA "CrossPlatformPatcher.exe") -Algorithm SHA256).Hash
    $hashB = (Get-FileHash (Join-Path $PubB "CrossPlatformPatcher.exe") -Algorithm SHA256).Hash
    if ($hashA -ne $hashB) { Fail "publish outputs differ ($hashA vs $hashB)" }
    Write-Host "deterministic exe sha256: $hashA"
}

Gate "artifact audit: embedded-resource allowlist" {
    # Transformation-only boundary: the bundle may carry OUR injected
    # payload (OWW dll, onnx, vosk natives, managed shims) and nothing else.
    # Any logical resource name outside this list fails the release.
    $allow = @(
        "PAIcom.OWW.dll",
        "System.Memory.dll", "System.Buffers.dll", "System.Numerics.Vectors.dll",
        "System.Runtime.CompilerServices.Unsafe.dll", "onnxruntime.managed.dll",
        "vosk.native.win-x86.dll", "vosk.native.win-gcc-x86.dll",
        "vosk.native.win-stdc-x86.dll", "vosk.native.win-pthread-x86.dll",
        "vosk.native.win-x64.dll", "vosk.native.win-gcc.dll",
        "vosk.native.win-stdc.dll", "vosk.native.win-pthread.dll",
        "vosk.native.linux-x64.so", "vosk.native.linux-arm64.so",
        "vosk.native.osx.dylib",
        "vosk.managed.dll", "naudio.core.dll", "naudio.winmm.dll",
        "newtonsoft.json.dll",
        "oww.model.hey_pie_com.quant.onnx", "oww.feature.melspectrogram.onnx",
        "oww.feature.embedding.onnx",
        "onnxruntime.native.win-x86.dll", "onnxruntime.native.win-x64.dll",
        "onnxruntime.native.linux-x64.so", "onnxruntime.native.linux-arm64.so",
        "onnxruntime.native.osx-x64.dylib", "onnxruntime.native.osx-arm64.dylib"
    )
    $exe = Join-Path $PubA "CrossPlatformPatcher.exe"
    $found = & {
        # Managed-resource inventory via dnlib-free inspection: monodis-level
        # parse is overkill; the build's own manifest is authoritative.
        # Cross-check: every EmbeddedResource LogicalName in the built
        # assembly must be allowlisted. Implemented with System.Reflection
        # over the published single-file? Single-file bundles are not
        # loadable — instead verify the intermediate build output.
        $asm = Join-Path $RepoRoot "bin\Release\net8.0\CrossPlatformPatcher.dll"
        $names = [System.Reflection.Assembly]::LoadFrom($asm).GetManifestResourceNames()
        $names
    }
    $bad = @($found | Where-Object { $_ -notin $allow })
    if ($bad.Count -gt 0) { Fail ("non-allowlisted embedded resources: " + ($bad -join ", ")) }
    Write-Host ("resources checked: {0}, allowlisted: all" -f $found.Count)
}

Gate "artifact audit: product-marker byte scan" {
    # No product bytes, fragments, decrypted resources, or manifest content.
    # Markers below are product-stack-exclusive: none can appear in our
    # sources legitimately (our own output naming uses PAIcom_patched, and
    # CLI help mentioning PAIcom.exe is intentionally NOT on this list —
    # filename references are not product content).
    $markers = @(
        "Assembly-CSharp",          # product managed game assembly
        "UnityPlayer",              # product engine native
        "Guna.UI",                  # product UI library (never referenced here)
        "globalgamemanagers",       # product Unity data
        "steam_api64",              # product Steamworks native
        "Ovidiu Dendrino",          # product credits string (binary scan only)
        "PAIcom.exe.config",        # product config (our output is .patched.exe)
        "sharedassets",             # product Unity data
        "level0",                   # product Unity data (bare token too broad alone? kept: see note)
        "MonoPosixHelper"           # product mono embed
    )
    $exe = Join-Path $PubA "CrossPlatformPatcher.exe"
    $raw = [System.IO.File]::ReadAllBytes($exe)
    $text = [System.Text.Encoding]::ASCII.GetString($raw)
    # NOTE on "level0": kept because a hit always co-occurs with the Unity
    # markers above in genuine product blobs; a lone hit would still fail
    # the gate and force a human look, which is the intended behavior.
    $hits = @($markers | Where-Object { $text.Contains($_) })
    if ($hits.Count -gt 0) { Fail ("product markers in bundle: " + ($hits -join ", ")) }
    Write-Host ("markers scanned: {0}, hits: none" -f $markers.Count)
}

Gate "stage release dir + SHA-256" {
    if (Test-Path $ReleaseDir) { Remove-Item $ReleaseDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
    $staged = Join-Path $ReleaseDir "CrossPlatformPatcher-0.1.1-win-x64.exe"
    Copy-Item (Join-Path $PubA "CrossPlatformPatcher.exe") $staged
    $sha = (Get-FileHash $staged -Algorithm SHA256).Hash
    "$sha  CrossPlatformPatcher-0.1.1-win-x64.exe" | Set-Content (Join-Path $ReleaseDir "SHA256SUMS.txt")
    Write-Host "staged: $staged"
    Write-Host "sha256: $sha"
    if ($AppImagePath -ne "" -and (Test-Path $AppImagePath)) {
        # Linux artifact, built + determinism-proven by
        # scripts/release/build-appimage.sh (reproducible squashfs:
        # fixed SOURCE_DATE_EPOCH, -all-root, pinned runtime).
        $aiStaged = Join-Path $ReleaseDir "CrossPlatformPatcher-0.1.1-x86_64.AppImage"
        Copy-Item $AppImagePath $aiStaged
        $aiSha = (Get-FileHash $aiStaged -Algorithm SHA256).Hash
        "$aiSha  CrossPlatformPatcher-0.1.1-x86_64.AppImage" | Add-Content (Join-Path $ReleaseDir "SHA256SUMS.txt")
        Write-Host "staged AppImage: $aiStaged"
        Write-Host "sha256: $aiSha"
    } elseif ($AppImagePath -ne "") {
        Fail "AppImage path not found: $AppImagePath"
    }
}

Gate "third-party notices + VB-CABLE" {
    $notices = @(
        "THIRD-PARTY NOTICES -- PAIcom voice release v0.1.1",
        "",
        "VB-CABLE (VB-Audio Virtual Cable driver, Pack45):",
        "  Origin: https://vb-cable.com / https://vb-audio.com/Cable",
        "  VB-CABLE is donationware by VB-Audio Software (V. Burel).",
        "  All participations are welcome; if you find it useful, please",
        "  donate for your license on the VB-Audio webshop.",
        "  Bundled here unmodified under the VB-CABLE distribution terms",
        "  (vb-audio.com/Services/licensing.htm): end-user distribution",
        "  with attribution; A+B/C+D packs are NOT bundled.",
        "",
        "Vosk (Alphacephei) small English model + libvosk: Apache 2.0,",
        "  https://alphacephei.com/vosk/",
        "ONNX Runtime (Microsoft): MIT, https://github.com/microsoft/onnxruntime",
        "NAudio: MIT, https://github.com/naudio/NAudio",
        "dnlib (build-time IL rewriting): MIT, https://github.com/0xd4d/dnlib"
    )
    $notices | Set-Content (Join-Path $ReleaseDir "THIRD-PARTY-NOTICES.txt")
    if ($VBCableZip -ne "" -and (Test-Path $VBCableZip)) {
        $vbStaged = Join-Path $ReleaseDir "VBCABLE_Driver_Pack45.zip"
        Copy-Item $VBCableZip $vbStaged
        $vbSha = (Get-FileHash $vbStaged -Algorithm SHA256).Hash
        "$vbSha  VBCABLE_Driver_Pack45.zip" | Add-Content (Join-Path $ReleaseDir "SHA256SUMS.txt")
        Write-Host "bundled VB-CABLE pack (operator-staged, unmodified)."
        Write-Host "sha256: $vbSha"
    } else {
        @(
            "VB-CABLE is NOT in this folder (download-on-demand path).",
            "If the voice chain needs a virtual cable:",
            "  1. Download VBCABLE_Driver_Pack45.zip from https://vb-cable.com",
            "  2. Extract, run VBCABLE_Setup_x64.exe as Administrator, reboot.",
            "  3. Set CABLE Output = default recording device."
        ) | Set-Content (Join-Path $ReleaseDir "VBCABLE-README.txt")
        Write-Host "VB-CABLE absent: wrote download-on-demand note."
    }
}

function Replace-MarkedSection([string]$notes, [string]$beginStart, [string]$endMarker, [string]$block) {
    $bi = $notes.IndexOf($beginStart)
    if ($bi -lt 0) { Fail ("notes missing marker " + $beginStart) }
    $lineEnd = $notes.IndexOf("`n", $bi)
    $ei = $notes.IndexOf($endMarker)
    if ($ei -lt 0 -or $ei -le $lineEnd) { Fail ("notes missing marker " + $endMarker) }
    return $notes.Substring(0, $lineEnd + 1) + $block + "`r`n" + $notes.Substring($ei)
}

function Write-NotesFile([string]$notesPath, [string]$updated) {
    # NOTE: [System.Text.Encoding]::UTF8 emits a BOM on Windows PowerShell 5.1;
    # construct BOM-less explicitly so the notes diff stays content-only.
    # The Edit tooling may also introduce a BOM; strip a leading one so
    # regeneration is stable regardless of how the template was last saved.
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    if ($updated.Length -gt 0 -and $updated[0] -eq [char]0xFEFF) {
        $updated = $updated.Substring(1)
    }
    [System.IO.File]::WriteAllText($notesPath, $updated, $utf8NoBom)
}

function Get-SumsEntries([string]$sumsPath) {
    $entries = Get-Content $sumsPath | ForEach-Object {
        if ($_ -match '^\s*([0-9A-Fa-f]{64})\s+(.+?)\s*$') {
            [PSCustomObject]@{ Hash = $Matches[1].ToUpperInvariant(); File = $Matches[2].Trim() }
        }
    }
    if (@($entries).Count -eq 0) { Fail "no parsable entries in SHA256SUMS.txt" }
    return @($entries)
}

# Known issues as structured data (symptom + status), rendered into the
# notes KNOWN block. Plain language, symptom first: the section exists to
# stop duplicate reports, so each entry leads with what the user feels.
$KnownItems = @(
    @{ Title = "Pop-ups that keep coming back";
       Symptom = "An info pop-up (for example the Discord invite) may appear every time the app starts, even after you dismiss it.";
       Status = "Known, being worked on. Dismissing it is safe; nothing breaks." },
    @{ Title = "Slow starts and browser-tab bursts on poor connections";
       Symptom = "On a slow connection the app can take a while to start, and a voice command may open several browser tabs at once.";
       Status = "Known, queued behind the network work." },
    @{ Title = "Menu buttons that do nothing";
       Symptom = "Some game buttons in the menu do not launch anything when clicked.";
       Status = "Expected in this version; game wiring is planned work, not a broken install." }
)

Gate "release notes Known Issues from structured data" {
    $notesPath = Join-Path $RepoRoot "docs\release-notes-v0.1.1.md"
    $notes = [System.IO.File]::ReadAllText($notesPath, [System.Text.Encoding]::UTF8)
    if ($notes.Length -gt 0 -and $notes[0] -eq [char]0xFEFF) { $notes = $notes.Substring(1) }
    $lines = @()
    foreach ($item in $KnownItems) {
        $lines += ("- **" + $item["Title"] + ":** " + $item["Symptom"] + " " + $item["Status"])
        $lines += ""
    }
    $block = $lines -join "`r`n"
    $updated = Replace-MarkedSection $notes "<!-- KNOWN-BEGIN" "<!-- KNOWN-END -->" $block
    Write-NotesFile $notesPath $updated
    Write-Host ("known items rendered: {0}" -f $KnownItems.Count)
}

function Write-VerifyBlock {
    $sumsPath = Join-Path $ReleaseDir "SHA256SUMS.txt"
    $notesPath = Join-Path $RepoRoot "docs\release-notes-v0.1.1.md"
    $entries = Get-SumsEntries $sumsPath
    $hasAppImage = @($entries | Where-Object { $_.File -like "*.AppImage" }).Count -gt 0
    $hasBundle = @($entries | Where-Object { $_.File -like "*.zip" }).Count -gt 0
    $lines = @()
    foreach ($e in $entries) {
        $lines += '```'
        $lines += $e.File
        $lines += ("SHA-256: " + $e.Hash)
        $lines += '```'
        $lines += ""
        $lines += "Windows (certutil):"
        $lines += ""
        $lines += '```'
        $lines += ("certutil -hashfile " + $e.File + " SHA256")
        $lines += '```'
        $lines += ""
        $lines += "Linux (sha256sum):"
        $lines += ""
        $lines += '```'
        $lines += ("sha256sum " + $e.File)
        $lines += '```'
        $lines += ""
    }
    $lines += "Compare the output to the SHA-256 above. The exe and AppImage builds are deterministic:"
    $lines += "rebuilding these sources produces byte-identical bytes, so anyone can"
    $lines += "reproduce those hashes. No bundled verifier is shipped -- verify externally."
    if ($hasBundle) {
        $lines += "The bundle zip is transport packaging for the single download; its hash"
        $lines += "covers this packaging run, while the exe/AppImage hashes are reproducible."
    }
    if (-not $hasAppImage) {
        $lines += "v0.1.1 publishes Windows-first; the sha256sum line also verifies"
        $lines += "the Windows exe before copying it over. The Linux .AppImage arrives"
        $lines += "with its own hash in its own release entry."
    }
    $block = $lines -join "`r`n"
    $block | Set-Content (Join-Path $ReleaseDir "VERIFY.txt")
    $notes = [System.IO.File]::ReadAllText($notesPath, [System.Text.Encoding]::UTF8)
    $updated = Replace-MarkedSection $notes "<!-- VERIFY-BEGIN" "<!-- VERIFY-END -->" $block
    Write-NotesFile $notesPath $updated
    Write-Host ("verify block: {0} artifact(s), notes rewritten" -f $entries.Count)
}

Gate "release notes Verify from SHA256SUMS (never by hand)" {
    Write-VerifyBlock
}

Gate "single bundle zip (first download is the only download)" {
    # The release is ONE download: exe + AppImage + VB-CABLE pack +
    # notices + notes. Driver setup still runs separately after extract
    # (elevation + reboot are unavoidable), but nothing is fetched twice.
    # Zip timestamps vary per run, so the bundle hash covers this packaging
    # run; provenance lives in the deterministic inner artifacts.
    $exeStaged = Join-Path $ReleaseDir "CrossPlatformPatcher-0.1.1-win-x64.exe"
    $aiStaged = Join-Path $ReleaseDir "CrossPlatformPatcher-0.1.1-x86_64.AppImage"
    $vbStaged = Join-Path $ReleaseDir "VBCABLE_Driver_Pack45.zip"
    foreach ($need in @($exeStaged, $aiStaged, $vbStaged)) {
        if (-not (Test-Path $need)) { Fail ("bundle member missing: " + $need) }
    }
    $bundleName = "PAIcom-Voice-v0.1.1.zip"
    $bundlePath = Join-Path $ReleaseDir $bundleName
    if (Test-Path $bundlePath) { Remove-Item $bundlePath -Force }
    $notesPath = Join-Path $RepoRoot "docs\release-notes-v0.1.1.md"
    Compress-Archive -Path $exeStaged, $aiStaged, $vbStaged, `
        (Join-Path $ReleaseDir "THIRD-PARTY-NOTICES.txt"), $notesPath `
        -DestinationPath $bundlePath
    $bundleSha = (Get-FileHash $bundlePath -Algorithm SHA256).Hash
    $innerLines = Get-Content (Join-Path $ReleaseDir "SHA256SUMS.txt")
    $bundleLine = "$bundleSha  $bundleName"
    @($bundleLine) + $innerLines | Set-Content (Join-Path $ReleaseDir "SHA256SUMS.txt")
    Write-Host "bundled: $bundlePath"
    Write-Host "sha256: $bundleSha"
}

Gate "release notes Verify refresh (bundle on top)" {
    Write-VerifyBlock
}

Write-Host ""
Write-Host "RELEASE PIPELINE COMPLETE: $ReleaseDir"
Get-ChildItem $ReleaseDir | Select-Object Name, Length
