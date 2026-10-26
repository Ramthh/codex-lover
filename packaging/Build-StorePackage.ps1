[CmdletBinding()]
param(
    [string]$Version = '1.7.16',
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [string]$Dotnet = 'dotnet',
    [string]$MakeAppx = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part version.' }
if (-not (Test-Path -LiteralPath $MakeAppx -PathType Leaf)) { throw 'Supply -MakeAppx with the Windows SDK makeappx.exe path.' }
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
    throw 'The output directory already contains files. Choose a new directory to preserve previous evidence.'
}
$source = Split-Path $PSScriptRoot
$stage = Join-Path $env:LOCALAPPDATA ('Codex\Builds\StorePackage-' + [Guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $stage 'artifacts'
$storeRestoreProps = Join-Path $PSScriptRoot 'StoreRestore.props'
$payload = Join-Path $stage 'payload'
$assets = Join-Path $payload 'Assets'
New-Item -ItemType Directory -Path $payload,$assets,$OutputDirectory -Force | Out-Null
& $Dotnet publish (Join-Path $source 'src\CodexAccountMonitor\CodexAccountMonitor.csproj') -c Release -r win-x64 --self-contained true -p:StoreBuild=true "-p:CustomAfterMicrosoftCommonProps=$storeRestoreProps" "-p:ArtifactsPath=$artifacts" -p:RestoreLockedMode=true "-p:Version=$Version" "-p:AssemblyVersion=$Version.0" "-p:FileVersion=$Version.0" -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $payload 'app') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Store build failed.' }
$runtime = & (Join-Path $PSScriptRoot 'Get-PinnedCodexRuntime.ps1') -DestinationDirectory (Join-Path $payload 'app\codex-runtime')
& node (Join-Path $PSScriptRoot 'store-assets.cjs') $assets
if ($LASTEXITCODE -ne 0) { throw 'Asset generation failed.' }
@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap uap10 desktop rescap">
 <Identity Name="RamiforIT.CodexLover" Publisher="CN=F6236B59-AAB3-46EA-BBF5-A90C296BCE3F" Version="$Version.0" ProcessorArchitecture="x64" />
 <Properties><DisplayName>Codex Lover</DisplayName><PublisherDisplayName>Rami for IT</PublisherDisplayName><Description>Your Codex accounts, usage and resets together.</Description><Logo>Assets\StoreLogo.png</Logo></Properties>
 <Resources><Resource Language="en-us" /></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
 <Applications>
  <Application Id="App" Executable="app\CodexAccountMonitor.exe" EntryPoint="Windows.FullTrustApplication">
   <uap:VisualElements DisplayName="Codex Lover" Description="Your Codex accounts, usage and resets together." BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png">
    <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" Square310x310Logo="Assets\Square310x310Logo.png" Square71x71Logo="Assets\Square71x71Logo.png" />
   </uap:VisualElements>
   <Extensions>
    <desktop:Extension Category="windows.startupTask" Executable="app\CodexAccountMonitor.exe" EntryPoint="Windows.FullTrustApplication" uap10:Parameters="--minimized">
     <desktop:StartupTask TaskId="CodexLoverStartup" Enabled="true" DisplayName="Codex Lover" />
    </desktop:Extension>
   </Extensions>
  </Application>
 </Applications>
 <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@ | Set-Content -LiteralPath (Join-Path $payload 'AppxManifest.xml') -Encoding utf8
$package = Join-Path $OutputDirectory "CodexLover-$Version-store-x64.msix"
if (Test-Path -LiteralPath $package) { throw 'This package already exists. Choose a new output directory to preserve it.' }
& $MakeAppx pack /d $payload /p $package *> (Join-Path $OutputDirectory 'makeappx.log')
if ($LASTEXITCODE -ne 0) { throw 'MSIX validation failed.' }
Copy-Item -LiteralPath (Join-Path $payload 'AppxManifest.xml') -Destination $OutputDirectory
[ordered]@{ version="$Version.0"; package=$package; sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant(); payload=$payload; artifacts=$artifacts; restoreLockedMode=$true; codexRuntime=$runtime; builtUtc=[DateTimeOffset]::UtcNow.ToString('o'); signing='Unsigned submission package; Microsoft signs accepted Store packages. Bundled Codex executables retain their verified OpenAI signatures.' } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build.json') -Encoding utf8
Write-Output $package
