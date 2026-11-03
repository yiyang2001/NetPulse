<#
.SYNOPSIS
    Builds the release files of NetPulse into artifacts\<version>\.

.DESCRIPTION
    For every runtime (default win-x64) two single-file exes are built:
      NetPulse-<version>-<rid>.exe            small, needs the .NET 10 Desktop Runtime
      NetPulse-<version>-<rid>-full.exe       self-contained, needs nothing else (much bigger)
    plus SHA256SUMS.txt. Each exe is also the installer: Settings > Install and updates > Install on this PC.

    Signing is optional: pass -CertificateThumbprint (a code-signing certificate in the current user's or the
    machine's store) and signtool.exe from the Windows SDK is used. Unsigned builds work, but SmartScreen
    warns about them until they have built up reputation.

.EXAMPLE
    .\build\publish.ps1
.EXAMPLE
    .\build\publish.ps1 -Runtime win-x64, win-arm64 -UpdateRepository owner/netpulse
.EXAMPLE
    .\build\publish.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [string[]] $Runtime = @('win-x64'),

    # GitHub "owner/repo" for the opt-in update check; empty builds have no update check.
    [string] $UpdateRepository = '',

    [string] $CertificateThumbprint = '',
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $SignTool = '',

    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\NetPulse\NetPulse.csproj'

[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

if ($UpdateRepository -and $UpdateRepository -notmatch '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$') {
    throw "UpdateRepository must look like owner/repo, not '$UpdateRepository'."
}

function Invoke-Checked([string] $exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe exited with $LASTEXITCODE." }
}

function Find-SignTool {
    if ($SignTool) { return $SignTool }
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw 'signtool.exe not found: install the Windows SDK or pass -SignTool.' }
    return $found.FullName
}

$out = Join-Path $root "artifacts\$version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $SkipTests) {
    Write-Host '== Tests' -ForegroundColor Cyan
    Invoke-Checked dotnet @('test', (Join-Path $root 'NetPulse.slnx'), '-c', 'Release', '--nologo', '-v', 'q')
}

$common = @('-c', 'Release', '--nologo', '-p:PublishSingleFile=true', '-p:DebugType=none', '-p:GenerateDocumentationFile=false')
if ($UpdateRepository) { $common += "-p:NetPulseUpdateRepository=$UpdateRepository" }

$built = @()
foreach ($rid in $Runtime) {
    $variants = @(
        @{ Suffix = '';      Args = @('--self-contained', 'false') },
        @{ Suffix = '-full'; Args = @('--self-contained', 'true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
    )
    foreach ($variant in $variants) {
        $name = "NetPulse-$version-$rid$($variant.Suffix).exe"
        Write-Host "== $name" -ForegroundColor Cyan
        $staging = Join-Path $out ".staging-$rid$($variant.Suffix)"
        Invoke-Checked dotnet (@('publish', $project, '-r', $rid, '-o', $staging) + $common + $variant.Args)
        Move-Item (Join-Path $staging 'NetPulse.exe') (Join-Path $out $name)
        Remove-Item $staging -Recurse -Force
        $built += Join-Path $out $name
    }
}

if ($CertificateThumbprint) {
    Write-Host '== Signing' -ForegroundColor Cyan
    $tool = Find-SignTool
    foreach ($file in $built) {
        Invoke-Checked $tool @('sign', '/sha1', $CertificateThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'NetPulse', $file)
        Invoke-Checked $tool @('verify', '/pa', '/q', $file)
    }
}

# Same format as sha256sum, so "sha256sum -c SHA256SUMS.txt" works too.
$sums = foreach ($file in $built) {
    '{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $file)
}
[IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), [string[]] $sums)

Write-Host ''
Write-Host "NetPulse $version -> $out" -ForegroundColor Green
Get-ChildItem $out | ForEach-Object { '{0,10:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name }
if (-not $CertificateThumbprint) { Write-Host 'Not signed (no -CertificateThumbprint).' -ForegroundColor Yellow }
if (-not $UpdateRepository) { Write-Host 'No update check (no -UpdateRepository).' -ForegroundColor Yellow }
