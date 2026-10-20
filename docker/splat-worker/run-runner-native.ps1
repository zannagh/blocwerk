# Run a Blocwerk 3D runner natively on Windows (x64): Brush trains on the GPU through wgpu (Vulkan or DX12, with the
# NVIDIA / AMD / Intel driver's own ICD: not the WSL2 dzn path that crashes, see README "NVIDIA hosts"). The Windows
# twin of run-runner-native.sh: the runner connects OUT to the Blocwerk server (no inbound port) and trains the
# photo-real views it is given; see README "3D runner". The key comes from the environment only, never an argument.
#
#   $env:BWR_KEY = 'bwr_...'
#   powershell -ExecutionPolicy Bypass -File docker\splat-worker\run-runner-native.ps1 --server https://blocwerk.app
#
# Needs: Python 3.12 (uv, the py launcher or python on PATH; uv can fetch one); no COLMAP (the server runs it).
# Downloads the pinned Brush release (sha256-checked, Mark-of-the-Web removed) into .\.bin and makes a venv in
# .\.venv on first run, from requirements.txt + requirements-windows.txt (pywinpty: the pseudo-console Brush needs to
# print its progress). Works in Windows PowerShell 5.1 and PowerShell 7. Keep this file ASCII (5.1 reads it as ANSI).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is many times slower with its progress bar in 5.1

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Shared = (Resolve-Path (Join-Path $Here '..\compute-jobs-py')).Path
$Geometry = (Resolve-Path (Join-Path $Here '..\wall-geometry')).Path

$BrushVersion = 'v0.3.0'
$BrushAsset = 'brush-app-x86_64-pc-windows-msvc'
$BrushSha256 = 'b68e3e9cf052d51bf3ee30776fa5a364de7f2ba13b58443128ff797bb7bcfcd6'
$PythonVersion = '3.12'

function Fail([string]$Message, [int]$Code = 1) {
    [Console]::Error.WriteLine($Message)
    exit $Code
}

function Invoke-Native([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { Fail "$What failed (exit code $LASTEXITCODE)" }
}

# A probe: the command's stdout (stderr dropped), or $null when it fails. Windows PowerShell 5.1 turns redirected
# native stderr into error records, which $ErrorActionPreference = 'Stop' would make fatal: relax it here, and catch.
function Get-NativeOutput([scriptblock]$Command) {
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $Command 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return $out
    }
    catch {
        return $null
    }
}

if ($env:OS -ne 'Windows_NT') { Fail 'run-runner-native.ps1 is for Windows; on a Mac use run-runner-native.sh' }
$arch = $env:PROCESSOR_ARCHITECTURE
if ($env:PROCESSOR_ARCHITEW6432) { $arch = $env:PROCESSOR_ARCHITEW6432 }
if ($arch -ne 'AMD64' -and -not $env:BRUSH_BIN) {
    Fail "Brush publishes an x64 Windows build only (this is $arch); set BRUSH_BIN to a build of your own"
}

function Install-Brush {
    $bin = Join-Path $Here ".bin\brush-$BrushVersion-$BrushAsset"  # versioned: a version bump downloads anew
    $exe = Join-Path $bin 'brush_app.exe'
    if (Test-Path -LiteralPath $exe) { return $exe }
    New-Item -ItemType Directory -Force -Path (Join-Path $Here '.bin') | Out-Null
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("brush-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $zip = Join-Path $tmp 'brush.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $url = "https://github.com/ArthurBrussee/brush/releases/download/$BrushVersion/$BrushAsset.zip"
        Write-Host "downloading Brush $BrushVersion ..."
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip
        $sum = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
        if ($sum -ne $BrushSha256) { Fail "Brush download checksum mismatch ($sum)" }
        Expand-Archive -LiteralPath $zip -DestinationPath $bin -Force
        # the Windows quarantine: drop the Mark-of-the-Web so SmartScreen does not block an unattended start
        Get-ChildItem -LiteralPath $bin -Recurse -File | Unblock-File
    }
    finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath $exe)) { Fail "the Brush archive has no brush_app.exe" }
    return $exe
}

function New-Venv([string]$Venv) {
    if (Get-Command uv -ErrorAction SilentlyContinue) {
        Invoke-Native 'uv venv' { uv venv -q --python $PythonVersion $Venv }
    }
    elseif (Get-Command py -ErrorAction SilentlyContinue) {
        Invoke-Native 'py -m venv' { py "-$PythonVersion" -m venv $Venv }
    }
    elseif (Get-Command python -ErrorAction SilentlyContinue) {
        Invoke-Native 'python -m venv' { python -m venv $Venv }  # the Store's python.exe stub fails here: install 3.12
    }
    else {
        Fail "no Python found: install uv (winget install astral-sh.uv) or Python $PythonVersion"
    }
}

function Get-RequirementsSum {
    $parts = foreach ($f in 'requirements.txt', 'requirements-windows.txt') {
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Here $f)).Hash
    }
    return ($parts -join '-').ToLowerInvariant()
}

function Install-Requirements([string]$Venv, [string]$Python) {
    $stamp = Join-Path $Venv '.requirements-sha256'
    $sum = Get-RequirementsSum
    if ((Test-Path -LiteralPath $stamp) -and ((Get-Content -LiteralPath $stamp -Raw).Trim() -eq $sum)) { return }
    Write-Host "installing the Python requirements into $Venv ..."
    $reqs = @('-r', (Join-Path $Here 'requirements.txt'), '-r', (Join-Path $Here 'requirements-windows.txt'))
    if (Get-Command uv -ErrorAction SilentlyContinue) {
        $env:VIRTUAL_ENV = $Venv
        Invoke-Native 'uv pip install' { uv pip install -q @reqs }
        Remove-Item Env:\VIRTUAL_ENV
    }
    else {
        # a venv made by uv has no pip; bootstrap it so a run without uv on PATH (a scheduled task) still works
        $probe = Get-NativeOutput { & $Python -m pip --version }
        if ($null -eq $probe) { Invoke-Native 'ensurepip' { & $Python -m ensurepip -q } }
        Invoke-Native 'pip install' { & $Python -m pip install -q @reqs }
    }
    Set-Content -LiteralPath $stamp -Value $sum -Encoding ASCII
}

if (-not $env:BRUSH_BIN) { $env:BRUSH_BIN = Install-Brush }
if (-not $env:BWR_KEY) { Fail 'set BWR_KEY to the runner key (bwr_...) shown when the runner was created' }

$venv = Join-Path $Here '.venv'
$python = Join-Path $venv 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) { New-Venv $venv }
Install-Requirements $venv $python

$sep = [IO.Path]::PathSeparator
$env:PYTHONPATH = (@($Here, $Shared, $Geometry) + @($env:PYTHONPATH | Where-Object { $_ })) -join $sep
$env:PYTHONUTF8 = '1'  # logs and job files in UTF-8 whatever the console code page
if (-not $env:RUNNER_WORK_DIR) { $env:RUNNER_WORK_DIR = Join-Path $env:LOCALAPPDATA 'blocwerk-runner' }
if (-not $env:BRUSH_CACHE_DIR) { $env:BRUSH_CACHE_DIR = Join-Path $env:RUNNER_WORK_DIR 'brush-cache' }
if (-not $env:GIT_SHA) {
    $env:GIT_SHA = 'unknown'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $sha = Get-NativeOutput { & git -C $Here rev-parse --short HEAD }
        if ($sha) { $env:GIT_SHA = "$sha".Trim() }
    }
}

Set-Location -LiteralPath $Here
& $python -m splatworker.gpurunner @args
exit $LASTEXITCODE
