<#
.SYNOPSIS
    Installs (or uninstalls) the Claude Code for Visual Studio extension into every compatible Visual Studio instance.

.DESCRIPTION
    Uses the VSIXInstaller.exe that ships with Visual Studio. Visual Studio must be closed; the script stops if devenv.exe
    is running rather than closing it for you. By default it installs the newest built VSIX (Release, then Debug).

.PARAMETER VsixPath
    VSIX to install. Defaults to the newest src\ClaudeCodeVS\bin\<Release|Debug>\net48\ClaudeCodeVS.vsix.

.PARAMETER Build
    Rebuild the Release VSIX first (full rebuild, so a manifest version bump is picked up).

.PARAMETER Uninstall
    Remove the extension instead of installing it.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -Build
.EXAMPLE
    .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$VsixPath,
    [switch]$Build,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ExtensionId = 'ClaudeCodeVS.7d1c6c0e-3b54-4f0a-9a52-2f6f0c4a9e11'
$ProjectDir = Join-Path $PSScriptRoot 'src\ClaudeCodeVS'

function Fail([string]$message) { Write-Host $message -ForegroundColor Red; exit 1 }

function Get-VsWhere {
    $path = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $path)) { Fail "vswhere.exe not found at $path. Is Visual Studio 2022 or later installed?" }
    $path
}

function Get-VsixInstaller {
    $installPath = & (Get-VsWhere) -latest -prerelease -products * -property installationPath | Select-Object -First 1
    if (-not $installPath) { Fail 'No Visual Studio installation found.' }
    $exe = Join-Path $installPath 'Common7\IDE\VSIXInstaller.exe'
    if (-not (Test-Path $exe)) { Fail "VSIXInstaller.exe not found at $exe." }
    $exe
}

function Invoke-Build {
    $msbuild = & (Get-VsWhere) -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { Fail 'MSBuild.exe not found.' }
    Write-Host 'Building Release VSIX...'
    & $msbuild (Join-Path $ProjectDir 'ClaudeCodeVS.csproj') /t:Rebuild /p:Configuration=Release /restore /m /v:minimal /nologo /clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { Fail "Build failed (exit code $LASTEXITCODE)." }
}

function Find-Vsix {
    $candidates = 'Release', 'Debug' |
        ForEach-Object { Join-Path $ProjectDir "bin\$_\net48\ClaudeCodeVS.vsix" } |
        Where-Object { Test-Path $_ } |
        ForEach-Object { Get-Item $_ } |
        Sort-Object LastWriteTime -Descending
    if (-not $candidates) { Fail 'No built VSIX found. Run with -Build, or pass -VsixPath.' }
    $candidates[0].FullName
}

function Get-VsixVersion([string]$path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'extension.vsixmanifest' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $manifest.PackageManifest.Metadata.Identity.Version
    }
    finally { $zip.Dispose() }
}

function Invoke-VsixInstaller([string[]]$arguments) {
    $log = Join-Path $env:TEMP ("ClaudeCodeVS-install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
    $process = Start-Process -FilePath (Get-VsixInstaller) -ArgumentList ($arguments + "/logFile:`"$log`"") -Wait -PassThru
    @{ ExitCode = $process.ExitCode; Log = $log }
}

# VSIXInstaller refuses to run (or waits indefinitely) while Visual Studio is open.
$running = Get-Process devenv -ErrorAction SilentlyContinue
if ($running) { Fail "Visual Studio is running (devenv.exe, PID $($running.Id -join ', ')). Close it and run this script again." }

if ($Uninstall) {
    Write-Host "Uninstalling $ExtensionId..."
    $result = Invoke-VsixInstaller @('/quiet', "/uninstall:$ExtensionId")
    switch ($result.ExitCode) {
        0       { Write-Host 'Uninstalled.' -ForegroundColor Green }
        1002    { Write-Host 'The extension is not installed.' -ForegroundColor Yellow }
        default { Fail "Uninstall failed (exit code $($result.ExitCode)). Log: $($result.Log)" }
    }
    exit 0
}

if ($Build) { Invoke-Build }
if (-not $VsixPath) { $VsixPath = Find-Vsix }
if (-not (Test-Path $VsixPath)) { Fail "VSIX not found: $VsixPath" }
$VsixPath = (Resolve-Path $VsixPath).Path
$item = Get-Item $VsixPath

Write-Host "Installing Claude Code for Visual Studio $(Get-VsixVersion $VsixPath)"
Write-Host "  from $VsixPath (built $($item.LastWriteTime))"
$result = Invoke-VsixInstaller @('/quiet', "`"$VsixPath`"")
switch ($result.ExitCode) {
    0       { Write-Host 'Installed. Start Visual Studio and open Tools > Claude Code.' -ForegroundColor Green }
    1001    { Write-Host 'This version is already installed. Bump the version in source.extension.vsixmanifest, or run with -Uninstall first.' -ForegroundColor Yellow }
    default { Fail "Install failed (exit code $($result.ExitCode)). Log: $($result.Log)" }
}
