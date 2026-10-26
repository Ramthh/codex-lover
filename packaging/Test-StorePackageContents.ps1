[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $CandidatePackage,
    [Parameter(Mandatory)] [string] $ExpectedVersion,
    [Parameter(Mandatory)] [string] $ExpectedSha256,
    [Parameter(Mandatory)] [string] $EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Output paths are supplied explicitly by the caller.
if ($ExpectedSha256 -notmatch '\A[0-9A-Fa-f]{64}\z') { throw 'ExpectedSha256 must contain exactly 64 hexadecimal characters.' }
$version = $null
if ($ExpectedVersion -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z' -or
    -not [Version]::TryParse($ExpectedVersion, [ref]$version) -or
    $version.Major -lt 1 -or $version.Major -gt 65535 -or $version.Minor -gt 65535 -or
    $version.Build -gt 65535 -or $version.Revision -gt 65535) { throw 'ExpectedVersion must be a valid four-part MSIX version.' }
$candidate = (Get-Item -LiteralPath $CandidatePackage).FullName
if ([IO.Path]::GetExtension($candidate) -ine '.msix') { throw 'CandidatePackage must be an MSIX file.' }
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if ($evidence -eq [IO.Path]::GetPathRoot($evidence)) { throw 'Evidence directory cannot be a drive root.' }
if (Test-Path -LiteralPath $evidence) { throw 'Evidence directory already exists; use a new directory.' }
$candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
if ($candidateHash -ine $ExpectedSha256) { throw 'Candidate does not match the expected exact MSIX SHA-256.' }

# Static inspection only. No package/app/CLI is executed, installed or signed.
# Temporary extracted files remain in a unique LocalAppData validation cache.
$cache = Join-Path $env:LOCALAPPDATA ('Codex\Validation\StoreContents-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($cache) | Out-Null
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$runtimeChecks = [Collections.Generic.List[object]]::new()
$archive = $null
$hashAlgorithm = [Security.Cryptography.SHA256]::Create()
$result = [ordered]@{
    status = 'running'; candidate = $candidate; version = $ExpectedVersion; sha256 = $candidateHash
    bytes = (Get-Item -LiteralPath $candidate).Length; cache = $cache
    scope = 'Static exact-MSIX identity, block-map content, assembly-version and pinned-runtime verification only. No installed lifecycle, live sign-in, account/usage retrieval, switching or Store certification is established.'
    checks = @(); runtimeFiles = @(); error = $null
}

function Read-EntryXml($Entry) {
    $stream = $Entry.Open()
    try {
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
            return $document
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-EntryHash($Entry) {
    $stream = $Entry.Open()
    try { return ([BitConverter]::ToString($hashAlgorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function Copy-EntryForInspection($Entry, [string] $RelativePath) {
    $target = Join-Path $cache $RelativePath
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    $inputStream = $Entry.Open()
    try {
        $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
    return $target
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($candidate)
    $entries = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name.Contains(':') -or $name -match '(^|/)\.\.?(/|$)' -or $entries.ContainsKey($name)) {
            throw 'MSIX contains an unsafe or duplicate entry name.'
        }
        $entries.Add($name, $entry)
    }
    foreach ($required in @('AppxManifest.xml', 'AppxBlockMap.xml', '[Content_Types].xml', 'app/CodexAccountMonitor.exe', 'app/CodexAccountMonitor.dll', 'app/codex-runtime/runtime-manifest.json')) {
        if (-not $entries.ContainsKey($required)) { throw "Required package entry is absent: $required" }
    }
    if ($entries.ContainsKey('AppxSignature.p7x')) { throw 'Inspect the original unsigned submission candidate, not a locally re-signed test copy.' }
    $checks.Add('Exact unsigned submission hash and safe, unique entry names')

    $manifest = Read-EntryXml $entries['AppxManifest.xml']
    $identity = $manifest.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Identity"]')
    if ($null -eq $identity -or $identity.GetAttribute('Name') -cne 'RamiforIT.CodexLover' -or
        $identity.GetAttribute('Publisher') -cne 'CN=F6236B59-AAB3-46EA-BBF5-A90C296BCE3F' -or
        $identity.GetAttribute('Version') -cne $ExpectedVersion -or $identity.GetAttribute('ProcessorArchitecture') -cne 'x64') {
        throw 'MSIX identity, publisher, architecture or version is incorrect.'
    }
    $application = $manifest.SelectSingleNode('//*[local-name()="Application" and @Id="App"]')
    $startup = $manifest.SelectSingleNode('//*[local-name()="Extension" and @Category="windows.startupTask"]')
    if ($null -eq $application -or $application.GetAttribute('Executable') -cne 'app\CodexAccountMonitor.exe' -or
        $application.GetAttribute('EntryPoint') -cne 'Windows.FullTrustApplication' -or $null -eq $startup -or
        $startup.GetAttribute('Executable') -cne 'app\CodexAccountMonitor.exe' -or
        $null -eq $manifest.SelectSingleNode('//*[local-name()="Capability" and @Name="runFullTrust"]')) {
        throw 'Required desktop entry point, startup declaration or capability is missing.'
    }
    $checks.Add('Registered Store identity, exact version, x64 desktop entry point and startup declaration')

    $blockMap = Read-EntryXml $entries['AppxBlockMap.xml']
    if ($blockMap.DocumentElement.GetAttribute('HashMethod') -cne 'http://www.w3.org/2001/04/xmlenc#sha256') { throw 'Unsupported MSIX block hash algorithm.' }
    $mapped = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $buffer = [byte[]]::new(65536)
    foreach ($file in $blockMap.SelectNodes('/*[local-name()="BlockMap"]/*[local-name()="File"]')) {
        $name = $file.GetAttribute('Name').Replace('\', '/')
        if (-not $mapped.Add($name) -or -not $entries.ContainsKey($name)) { throw 'Block map has a duplicate or absent file.' }
        $entry = $entries[$name]
        if ($entry.Length -ne [long]$file.GetAttribute('Size')) { throw "Block-map size mismatch: $name" }
        $stream = $entry.Open()
        try {
            $remaining = $entry.Length
            $blocks = @($file.SelectNodes('*[local-name()="Block"]'))
            if ($entry.Length -gt 0 -and $blocks.Count -ne [Math]::Ceiling($entry.Length / 65536.0)) { throw "Block count mismatch: $name" }
            foreach ($block in $blocks) {
                $wanted = [int][Math]::Min(65536, $remaining)
                $read = 0
                while ($read -lt $wanted) {
                    $amount = $stream.Read($buffer, $read, $wanted - $read)
                    if ($amount -eq 0) { throw "Premature end of MSIX entry: $name" }
                    $read += $amount
                }
                $actualHash = [Convert]::ToBase64String($hashAlgorithm.ComputeHash($buffer, 0, $wanted))
                if ($actualHash -cne $block.GetAttribute('Hash')) { throw "MSIX block hash mismatch: $name" }
                $remaining -= $wanted
            }
            if ($remaining -ne 0 -or $stream.ReadByte() -ne -1) { throw "Unread or extra bytes in MSIX entry: $name" }
        } finally { $stream.Dispose() }
    }
    foreach ($name in $entries.Keys) {
        if (-not $mapped.Contains($name) -and $name -notin @('AppxBlockMap.xml', '[Content_Types].xml')) { throw "Package file is absent from its block map: $name" }
    }
    $result.blockMapFileCount = $mapped.Count
    $checks.Add('Every declared MSIX payload block matches its SHA-256; no unlisted payload files')

    $runtimeManifestPath = Join-Path $PSScriptRoot 'codex-runtime-manifest.json'
    $runtime = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json
    if ((Get-EntryHash $entries['app/codex-runtime/runtime-manifest.json']) -ine (Get-FileHash -LiteralPath $runtimeManifestPath -Algorithm SHA256).Hash) {
        throw 'Embedded runtime manifest differs from the reviewed packaging manifest.'
    }
    Copy-EntryForInspection $entries['app/codex-runtime/runtime-manifest.json'] 'codex-runtime/runtime-manifest.json' | Out-Null
    $expectedRuntimeNames = @($runtime.files | ForEach-Object { 'app/codex-runtime/' + $_.path }) +
        @($runtime.notices | ForEach-Object { 'app/codex-runtime/licenses/' + $_.path }) + 'app/codex-runtime/runtime-manifest.json'
    $actualRuntimeNames = @($entries.Keys | Where-Object { $_.StartsWith('app/codex-runtime/', [StringComparison]::OrdinalIgnoreCase) })
    if (@(Compare-Object $expectedRuntimeNames $actualRuntimeNames).Count -ne 0) { throw 'Embedded runtime inventory differs from the pinned inventory.' }
    foreach ($metadata in @($runtime.files) + @($runtime.notices)) {
        $vendorFile = $metadata.PSObject.Properties.Name -contains 'openAiSigned'
        $relative = if ($vendorFile) { $metadata.path } else { 'licenses/' + $metadata.path }
        $entry = $entries['app/codex-runtime/' + $relative]
        if ($entry.Length -ne $metadata.bytes -or (Get-EntryHash $entry) -ine $metadata.sha256) { throw "Pinned runtime content mismatch: $relative" }
        $path = Copy-EntryForInspection $entry ('codex-runtime/' + $relative)
        $signatureStatus = 'Not required; exact vendor/notice hash verified'
        if ($vendorFile -and $metadata.openAiSigned) {
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
                $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -cne $runtime.signerName -or
                $signature.SignerCertificate.Thumbprint -ine $runtime.signerCertificateThumbprint) { throw "Pinned vendor signature validation failed: $relative" }
            $signatureStatus = 'Valid pinned OpenAI Authenticode signer'
        }
        $runtimeChecks.Add([ordered]@{ path = $relative; sha256 = $metadata.sha256; bytes = $metadata.bytes; signature = $signatureStatus })
    }
    $result.runtimeVersion = $runtime.version
    $result.extractedRuntime = Join-Path $cache 'codex-runtime'
    $checks.Add('Complete pinned runtime and notices, with four valid pinned OpenAI executable signatures')
    $assembly = Copy-EntryForInspection $entries['app/CodexAccountMonitor.dll'] 'CodexAccountMonitor.dll'
    $assemblyVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).FileVersion
    if ($assemblyVersion -cne $ExpectedVersion) { throw 'Application file version does not match the MSIX version.' }
    $checks.Add('Application file version agrees with package version')
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ine $candidateHash) { throw 'Original candidate changed during inspection.' }
    $result.status = 'passed'
} catch {
    $result.status = 'failed'
    $result.error = $_.Exception.Message
    throw
} finally {
    if ($null -ne $archive) { $archive.Dispose() }
    $hashAlgorithm.Dispose()
    $result.checks = @($checks)
    $result.runtimeFiles = @($runtimeChecks)
    $result.completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $evidence 'package-content-result.json') -Encoding utf8
}
[pscustomobject]@{ Status = $result.status; Version = $ExpectedVersion; Sha256 = $candidateHash; Evidence = (Join-Path $evidence 'package-content-result.json') }
