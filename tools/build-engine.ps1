# Builds the self-contained speech engine into engine-dist\speechmatics_engine\.
#
# Run this whenever speechmatics_engine.py or requirements.txt changes, then
# build the app as usual. InterviewCopilot.csproj copies the result to engine\
# beside the app, and MainWindow prefers it over a system Python.
#
# The build deliberately uses a throwaway virtual environment. Building against
# the machine's global site-packages pulled unrelated libraries into the bundle
# and failed outright on a half-installed matplotlib.
#
#   powershell -ExecutionPolicy Bypass -File tools\build-engine.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$venv = Join-Path $root "build\engine-venv"
$python = Join-Path $venv "Scripts\python.exe"

if (-not (Test-Path $python)) {
    Write-Host "Creating build virtual environment..."
    py -3 -m venv $venv
}

Write-Host "Installing engine dependencies..."
& $python -m pip install --quiet --upgrade pip
& $python -m pip install --quiet -r requirements.txt pyinstaller

# ── Stamp the source this engine was built from ──────────────────────────────
#
# "Which engine is this user running?" had no answer. The Mac app shipped a
# 1,074-line fork of this file for months while every build succeeded, and
# nothing anywhere would have caught it - not the build, not a test, not a
# support log. A binary that cannot say where it came from is one nobody can
# check.
#
# So the commit, the working-tree state and a hash of the source go into the
# bundle at build time, and the engine prints them on startup. The hash matters
# as much as the commit: a commit id says which revision was checked out, and
# the file hash says whether what was compiled is actually that revision.
$commit = (git rev-parse --short HEAD 2>$null)
if (-not $commit) { $commit = "nogit" }
$dirty = (git status --porcelain -- speechmatics_engine.py 2>$null)
if ($dirty) { $commit = "$commit+dirty" }

# The hash is of the CONTENT, not of the file on disk.
#
# It used to be Get-FileHash over the working-tree file, which cannot match
# across platforms and never could have. core.autocrlf is true here, so Windows
# checks the shared engine out with CRLF while macOS gets LF - same blob, same
# commit, different bytes, by git's deliberate design. The check would have
# reported a fork on every honest cross-platform build, forever.
#
# That is the worst way for a check to be wrong. A dirty tree at least has a
# visible cause; this looked exactly like real drift, had no explanation on the
# surface, and was guaranteed to recur - so the first person to hit it explains
# it away and nobody trusts the tool afterwards. Found by the Mac session
# testing the obvious alternative before reporting drift, which is the only
# reason it was not recorded as one.
#
# git's blob id is content-addressed and line-ending normalised, so it is the
# same on both platforms. It is also the id git itself stores, so a match
# additionally proves the working tree is the committed content - something the
# old method could not tell you at all.
$srcPath = Join-Path $root "speechmatics_engine.py"
$srcHash = (git hash-object $srcPath 2>$null)

if ($srcHash) {
    $srcHash = $srcHash.Trim().Substring(0, 12).ToLower()
} else {
    # No git on the build machine. Reproduce the same value rather than a
    # different-but-stable one: a fallback that stamps an incomparable hash
    # silently reintroduces the bug this replaces.
    #
    # A git blob id is sha1("blob <length>\0" + content) over LF content.
    $raw = [IO.File]::ReadAllBytes($srcPath)
    $lf  = New-Object System.Collections.Generic.List[byte]
    for ($i = 0; $i -lt $raw.Length; $i++) {
        if ($raw[$i] -eq 13 -and ($i + 1) -lt $raw.Length -and $raw[$i + 1] -eq 10) { continue }
        $lf.Add($raw[$i])
    }
    $body   = $lf.ToArray()
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($body.Length)" + [char]0)
    $all    = New-Object byte[] ($header.Length + $body.Length)
    [Array]::Copy($header, 0, $all, 0, $header.Length)
    [Array]::Copy($body, 0, $all, $header.Length, $body.Length)

    $sha1 = [Security.Cryptography.SHA1]::Create()
    $srcHash = ([BitConverter]::ToString($sha1.ComputeHash($all)) -replace '-', '').Substring(0, 12).ToLower()
    $sha1.Dispose()
    Write-Host "git not found; blob id computed directly."
}
$builtAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

# Stamped through a PyInstaller runtime hook rather than by editing the engine
# source, because that source is SHARED with the Mac app byte for byte and the
# hash is only worth anything if both platforms compute it over the same bytes.
#
# The first version of this did edit speechmatics_engine.py, adding a banner to
# it. That would have made the two platforms' hashes differ for identical
# logic — the provenance check reporting a divergence it had caused itself,
# which is worse than not having it. The Mac session spotted it and had already
# used a runtime hook for exactly this reason.
#
# A runtime hook runs before the main script, so the line still prints before
# anything in the engine can fail.
$hookDir = Join-Path $root "build\pyinstaller"
New-Item -ItemType Directory -Force $hookDir | Out-Null
$hook = Join-Path $hookDir "rthook_engine_build.py"

Write-Host "Stamping build: $commit / $srcHash"
@"
# Generated by tools/build-engine.ps1. Not committed, not part of the engine.
print(">>> ENGINE BUILD: $commit src:$srcHash built:$builtAt", flush=True)
"@ | Set-Content -Path $hook -Encoding utf8

Write-Host "Building engine..."
# pyaudiowpatch is imported inside a try/except, which PyInstaller's static
# analysis walked straight past, so the frozen engine silently fell back to
# stock pyaudio and lost WASAPI loopback. Without it, system audio needs a
# VB-Cable install, so collect it explicitly.
& $python -m PyInstaller --noconfirm --onedir `
    --collect-all pyaudiowpatch `
    --runtime-hook $hook `
    --name speechmatics_engine `
    --distpath (Join-Path $root "engine-dist") `
    --workpath (Join-Path $root "build\pyinstaller") `
    --specpath (Join-Path $root "build\pyinstaller") `
    (Join-Path $root "speechmatics_engine.py")

$exe = Join-Path $root "engine-dist\speechmatics_engine\speechmatics_engine.exe"
if (-not (Test-Path $exe)) { throw "Engine build produced no executable." }

# --collect-all drags in wheel source files and .dist-info metadata that the
# engine never reads. Harmless where it lands loose beside an exe, less so once
# it is inside a signed package: five packages ship identically named
# METADATA and RECORD files, and duplicate-name collisions inside a signed
# payload are a well-known way to fail packaging for a reason that reads as
# nothing to do with the cause.
#
# Found on the Mac side by building rather than by reading - Xcode tried to
# compile websockets/speedups.c and failed on a missing Python.h. MSBuild does
# not, but the MSIX payload is signed, so the metadata half applies here.
$dist = Split-Path $exe
$stripped = 0
foreach ($pattern in @("*.c", "*.h", "*.pyx", "*.pyi")) {
    Get-ChildItem $dist -Recurse -Filter $pattern -File -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue; $stripped++ }
}
Get-ChildItem $dist -Recurse -Directory -Filter "*.dist-info" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue; $stripped++ }
Write-Host "Stripped $stripped build-only files from the bundle."

$size = (Get-ChildItem (Split-Path $exe) -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("Engine built: {0} ({1:N1} MB)" -f $exe, $size)
