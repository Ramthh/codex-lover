[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PayloadZip,
    [Parameter(Mandatory)] [string] $DotNetPath,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $CertificateThumbprint,
    [string] $SignToolPath,
    [switch] $LocalTest,
    [switch] $UnsignedBeta
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $repository 'packaging\Signing.ps1')
Assert-PublisherSigning -CertificateThumbprint $CertificateThumbprint -SignToolPath $SignToolPath -LocalTest:$LocalTest -UnsignedBeta:$UnsignedBeta
$payload = (Resolve-Path -LiteralPath $PayloadZip).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadArchive = [IO.Compression.ZipFile]::OpenRead($payload)
try {
    $entry = $payloadArchive.GetEntry('publish-manifest.json')
    if ($null -eq $entry) { throw 'The payload has no release manifest.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    Assert-PayloadReleaseChannel -Manifest $manifest -LocalTest:$LocalTest -UnsignedBeta:$UnsignedBeta
    if (-not $LocalTest -and -not $UnsignedBeta) {
        $checkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('CodexLover-SignatureCheck-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($checkDirectory) | Out-Null
        $checkedFiles = [Collections.Generic.List[string]]::new()
        try {
            $publisherEntries = @($payloadArchive.Entries | Where-Object { $_.FullName -match '^CodexAccountMonitor[^/\\]*\.(exe|dll)$' })
            if (-not ($publisherEntries | Where-Object { $_.FullName -eq 'CodexAccountMonitor.exe' })) { throw 'The signed application executable is missing from the payload.' }
            foreach ($publisherEntry in $publisherEntries) {
                $checkFile = Join-Path $checkDirectory $publisherEntry.FullName
                [IO.Compression.ZipFileExtensions]::ExtractToFile($publisherEntry, $checkFile, $false)
                $checkedFiles.Add($checkFile)
                $signature = Get-AuthenticodeSignature -LiteralPath $checkFile
                if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint.Replace(' ', '')) {
                    throw 'A publisher-owned payload file has an invalid or unexpected signature. Public packaging stopped.'
                }
            }
        } finally {
            foreach ($checkFile in $checkedFiles) { Remove-Item -LiteralPath $checkFile -ErrorAction SilentlyContinue }
            Remove-Item -LiteralPath $checkDirectory -ErrorAction SilentlyContinue
        }
    }
} finally { $payloadArchive.Dispose() }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new installer output directory.' }
$project = Join-Path $repository 'packaging\Installer\CodexAccountMonitor.Setup.csproj'
& $DotNetPath publish $project -c Release -r win-x64 --self-contained true -o $OutputDirectory '-p:PublishSingleFile=true' '-p:RestoreLockedMode=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' "-p:PayloadZip=$payload" "-p:Version=$($manifest.version)" "-p:AssemblyVersion=$($manifest.version).0" "-p:FileVersion=$($manifest.version).0" "-p:UnsignedBeta=$([bool]$UnsignedBeta)" '-p:DebugType=None'
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
$setup = Join-Path $OutputDirectory 'CodexAccountMonitor.Setup.exe'
if ($CertificateThumbprint) {
    Invoke-PublisherSigning -Path $setup -CertificateThumbprint $CertificateThumbprint -SignToolPath $SignToolPath
}
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($setup + '.sha256', "$hash *CodexAccountMonitor.Setup.exe" + [Environment]::NewLine)
Write-Output $setup
