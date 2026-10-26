[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [ValidateSet('FrameworkDependent', 'SelfContained')]
    [string] $Deployment = 'SelfContained',

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $DotNetPath,

    [string] $OutputRoot,

    [string] $SupportedCodexVersion = '0.153.4',

    [string] $CertificateThumbprint,

    [string] $SignToolPath,

    [switch] $LocalTest,

    [switch] $UnsignedBeta,

    [switch] $SingleFile,

    [switch] $SkipTests,

    [switch] $IncludeCodexRuntime,

    [switch] $NoArchive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($IncludeCodexRuntime -and $Runtime -ne 'win-x64') {
    throw 'The pinned Codex account connection runtime currently supports Windows x64 only.'
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & $script:DotNetExecutable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE while running: dotnet $($Arguments -join ' ')"
    }
}

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
. (Join-Path $repositoryRoot 'packaging\Signing.ps1')
Assert-PublisherSigning -CertificateThumbprint $CertificateThumbprint -SignToolPath $SignToolPath -LocalTest:$LocalTest -UnsignedBeta:$UnsignedBeta
if ($UnsignedBeta -and ($Configuration -ne 'Release' -or $Runtime -ne 'win-x64' -or $Deployment -ne 'SelfContained')) {
    throw 'Unsigned beta publication currently supports Release, self-contained Windows x64 only.'
}
$solutionPath = Join-Path $repositoryRoot 'CodexAccountMonitor.sln'
$projectPath = Join-Path $repositoryRoot 'src\CodexAccountMonitor\CodexAccountMonitor.csproj'
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $versionProps = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
    $Version = [string] $versionProps.Project.PropertyGroup.Version
}

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution not found: $solutionPath"
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Application project not found: $projectPath"
}

$script:DotNetExecutable = $null
$dotnetCommand = $null
if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
    $dotnetCandidate = if ([System.IO.Path]::IsPathRooted($DotNetPath)) {
        [System.IO.Path]::GetFullPath($DotNetPath)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $DotNetPath))
    }

    if (-not (Test-Path -LiteralPath $dotnetCandidate -PathType Leaf)) {
        throw "The requested dotnet executable does not exist: $dotnetCandidate"
    }

    $script:DotNetExecutable = $dotnetCandidate
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
}

if ([string]::IsNullOrWhiteSpace($script:DotNetExecutable) -and $null -ne $dotnetCommand) {
    $script:DotNetExecutable = $dotnetCommand.Source
}
elseif ([string]::IsNullOrWhiteSpace($script:DotNetExecutable)) {
    $standardDotNetPath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $standardDotNetPath -PathType Leaf) {
        $script:DotNetExecutable = $standardDotNetPath
    }
}

if ([string]::IsNullOrWhiteSpace($script:DotNetExecutable)) {
    throw 'The dotnet host was not found. Install the .NET 10 SDK or pass -DotNetPath.'
}

$installedSdks = @(& $script:DotNetExecutable --list-sdks)
if ($LASTEXITCODE -ne 0 -or -not ($installedSdks | Where-Object { $_ -match '^10\.0\.' })) {
    throw 'A .NET 10 SDK is required. Installing only the .NET Desktop Runtime is not sufficient to publish.'
}
$env:DOTNET_ROOT = Split-Path -Parent $script:DotNetExecutable
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $resolvedOutputRoot = Join-Path $repositoryRoot 'artifacts\publish'
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    $resolvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}

$deploymentLabel = if ($Deployment -eq 'SelfContained') { 'self-contained' } else { 'framework-dependent' }
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff', [Globalization.CultureInfo]::InvariantCulture)
$runDirectory = Join-Path $resolvedOutputRoot (Join-Path $Version (Join-Path $Runtime (Join-Path $deploymentLabel $timestamp)))
$publishDirectory = Join-Path $runDirectory 'app'

if (Test-Path -LiteralPath $runDirectory) {
    throw "Refusing to overwrite an existing publish directory: $runDirectory"
}

[System.IO.Directory]::CreateDirectory($publishDirectory) | Out-Null

Write-Host "Repository: $repositoryRoot"
Write-Host "Output:     $runDirectory"
Write-Host "Runtime:    $Runtime ($deploymentLabel)"

Invoke-DotNet -Arguments @('restore', $solutionPath, '--locked-mode', '--nologo')

if (-not $SkipTests) {
    Invoke-DotNet -Arguments @(
        'test',
        $solutionPath,
        '--configuration', $Configuration,
        '--no-restore',
        '--nologo'
    )
}

$selfContainedValue = if ($Deployment -eq 'SelfContained') { 'true' } else { 'false' }
$assemblyVersion = "$Version.0"
$publishArguments = @(
    'publish',
    $projectPath,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', $selfContainedValue,
    '--output', $publishDirectory,
    '--nologo',
    "-p:Version=$Version",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    "-p:InformationalVersion=$Version",
    '-p:ContinuousIntegrationBuild=true',
    '-p:RestoreLockedMode=true',
    '-p:DebugSymbols=false',
    '-p:DebugType=None'
)

if ($SingleFile) {
    $publishArguments += @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true'
    )
}

Invoke-DotNet -Arguments $publishArguments

$bundledRuntime = $null
if ($IncludeCodexRuntime) {
    $bundledRuntime = & (Join-Path $repositoryRoot 'packaging\Get-PinnedCodexRuntime.ps1') -DestinationDirectory (Join-Path $publishDirectory 'codex-runtime')
    $SupportedCodexVersion = $bundledRuntime.version
}

$applicationPath = Join-Path $publishDirectory 'CodexAccountMonitor.exe'
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw "Publish completed without the expected executable: $applicationPath"
}

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    foreach ($publisherFile in Get-ChildItem -LiteralPath $publishDirectory -File | Where-Object { $_.Name -like 'CodexAccountMonitor*.dll' -or $_.Name -eq 'CodexAccountMonitor.exe' }) {
        Invoke-PublisherSigning -Path $publisherFile.FullName -CertificateThumbprint $CertificateThumbprint -SignToolPath $SignToolPath
    }
}

$publishedFiles = @(
    Get-ChildItem -LiteralPath $publishDirectory -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($publishDirectory.Length).TrimStart(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
            [ordered]@{
                path = $relativePath.Replace('\', '/')
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)

$gitRoot = (& git -C $repositoryRoot rev-parse --show-toplevel 2>$null | Select-Object -First 1)
$revision = if ($gitRoot -and [System.IO.Path]::GetFullPath($gitRoot) -eq $repositoryRoot) { & git -C $repositoryRoot rev-parse HEAD 2>$null | Select-Object -First 1 } else { $null }
$manifest = [ordered]@{
    product = 'Codex Account Monitor'
    version = $Version
    configuration = $Configuration
    runtime = $Runtime
    deployment = $deploymentLabel
    singleFile = [bool] $SingleFile
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    dotnetSdk = (& $script:DotNetExecutable --version).Trim()
    supportedCodexVersion = $SupportedCodexVersion
    bundledCodexRuntime = $bundledRuntime
    sourceRevision = $revision
    sourceTreeSha256 = ''
    signatureStatus = [string] (Get-AuthenticodeSignature -LiteralPath $applicationPath).Status
    releaseChannel = $(if ($LocalTest) { 'local-test' } elseif ($UnsignedBeta) { 'unsigned-beta' } else { 'public' })
    files = $publishedFiles
}

$sourceHashes = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Sort-Object FullName |
    ForEach-Object { ($_.FullName.Substring($repositoryRoot.Length).Replace('\', '/')) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
$sourceBytes = [System.Text.Encoding]::UTF8.GetBytes(($sourceHashes -join "`n"))
$sourceHasher = [System.Security.Cryptography.SHA256]::Create()
try { $manifest.sourceTreeSha256 = ([BitConverter]::ToString($sourceHasher.ComputeHash($sourceBytes))).Replace('-', '').ToLowerInvariant() }
finally { $sourceHasher.Dispose() }

$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
$manifestPath = Join-Path $publishDirectory 'publish-manifest.json'
$manifestJson = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($manifestPath, $manifestJson + [Environment]::NewLine, $utf8WithoutBom)

if (-not $NoArchive) {
    $archiveName = "CodexAccountMonitor-$Version-$Runtime-$deploymentLabel.zip"
    $archivePath = Join-Path $runDirectory $archiveName
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDirectory,
        $archivePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$archivePath.sha256"
    [System.IO.File]::WriteAllText(
        $checksumPath,
        "$archiveHash *$archiveName$([Environment]::NewLine)",
        $utf8WithoutBom)

    Write-Host "Archive:    $archivePath"
    Write-Host "SHA-256:    $archiveHash"
}

Write-Host "Published:  $publishDirectory"
