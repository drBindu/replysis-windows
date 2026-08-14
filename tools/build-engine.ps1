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

Write-Host "Building engine..."
# pyaudiowpatch is imported inside a try/except, which PyInstaller's static
# analysis walked straight past, so the frozen engine silently fell back to
# stock pyaudio and lost WASAPI loopback. Without it, system audio needs a
# VB-Cable install, so collect it explicitly.
& $python -m PyInstaller --noconfirm --onedir `
    --collect-all pyaudiowpatch `
    --name speechmatics_engine `
    --distpath (Join-Path $root "engine-dist") `
    --workpath (Join-Path $root "build\pyinstaller") `
    --specpath (Join-Path $root "build\pyinstaller") `
    (Join-Path $root "speechmatics_engine.py")

$exe = Join-Path $root "engine-dist\speechmatics_engine\speechmatics_engine.exe"
if (-not (Test-Path $exe)) { throw "Engine build produced no executable." }

$size = (Get-ChildItem (Split-Path $exe) -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("Engine built: {0} ({1:N1} MB)" -f $exe, $size)
