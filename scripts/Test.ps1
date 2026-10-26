[CmdletBinding()]
param(
    [switch] $Store,
    [string] $DotNetPath = 'dotnet',
    [string] $ResultsDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command $DotNetPath -ErrorAction Stop).Source
$sdkRoot = Split-Path -Parent $dotnet
# Test child processes need the same runtime as the SDK used for this build.
$previousRoot = $env:DOTNET_ROOT
$previousX64 = $env:DOTNET_ROOT_X64
$mode = if ($Store) { 'store' } else { 'desktop' }
$runRoot = Join-Path $env:LOCALAPPDATA ('CodexLover\Validation\' + $mode + '-' + [Guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $runRoot 'artifacts'
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) { $ResultsDirectory = Join-Path $runRoot 'results' }
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$properties = @("-p:ArtifactsPath=$artifacts")
if ($Store) {
    $properties += '-p:StoreBuild=true'
    $properties += "-p:CustomAfterMicrosoftCommonProps=$(Join-Path $repository 'packaging\StoreRestore.props')"
}
function Invoke-CheckedDotNet([string[]] $Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}
Push-Location $repository
try {
    $env:DOTNET_ROOT = $sdkRoot
    $env:DOTNET_ROOT_X64 = $sdkRoot
    Invoke-CheckedDotNet (@('restore', 'CodexAccountMonitor.sln', '--locked-mode') + $properties)
    Invoke-CheckedDotNet (@('build', 'CodexAccountMonitor.sln', '-c', 'Release', '--no-restore') + $properties)
    Invoke-CheckedDotNet (@('test', 'CodexAccountMonitor.sln', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx', '--results-directory', $ResultsDirectory) + $properties)
    if (-not $Store) {
        Invoke-CheckedDotNet @('restore', 'packaging/Installer.Checks/Installer.Checks.csproj', '--locked-mode', "-p:ArtifactsPath=$artifacts")
        Invoke-CheckedDotNet @('run', '--project', 'packaging/Installer.Checks', '-c', 'Release', '--no-restore', "-p:ArtifactsPath=$artifacts")
        & (Join-Path $PSScriptRoot 'Test-ReleaseChannels.ps1')
    }
    Write-Host "Validated $mode. Test results: $ResultsDirectory"
} finally {
    $env:DOTNET_ROOT = $previousRoot
    $env:DOTNET_ROOT_X64 = $previousX64
    Pop-Location
}
