[CmdletBinding()]
param(
    [string] $DestinationDirectory,
    [switch] $Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot 'codex-runtime-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$licenseRoot = Join-Path $PSScriptRoot 'codex-runtime-licenses'
if ($manifest.schemaVersion -ne 1 -or $manifest.target -ne 'x86_64-pc-windows-msvc' -or
    $manifest.version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') {
    throw 'Unsupported pinned Codex runtime manifest.'
}

function Assert-PinnedFile([string] $Path, $Metadata) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required pinned file is missing: $Path" }
    $file = Get-Item -LiteralPath $Path -Force
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Pinned file must not be a link: $Path" }
    if ($file.Length -ne $Metadata.bytes -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Metadata.sha256) {
        throw "Pinned Codex runtime checksum/size mismatch: $Path"
    }
}

function Assert-RuntimeDirectory([string] $Directory, [bool] $WithDocumentation = $false) {
    $allItems = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    if ($allItems | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
        throw 'Pinned runtime must contain no symbolic links or junctions.'
    }
    $allowed = @($manifest.files | ForEach-Object { $_.path })
    if ($WithDocumentation) {
        $allowed += 'runtime-manifest.json'
        $allowed += @($manifest.notices | ForEach-Object { 'licenses/' + $_.path })
    }
    $actual = @($allItems | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
        $_.FullName.Substring($Directory.TrimEnd('\').Length + 1).Replace('\', '/')
    })
    if (@(Compare-Object -ReferenceObject $allowed -DifferenceObject $actual).Count -ne 0) {
        throw 'Pinned runtime file inventory differs from the reviewed manifest.'
    }
    foreach ($file in $manifest.files) {
        $path = Join-Path $Directory $file.path
        Assert-PinnedFile $path $file
        if ($file.openAiSigned) {
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
                $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -cne $manifest.signerName -or
                $signature.SignerCertificate.Thumbprint -ine $manifest.signerCertificateThumbprint) {
                throw "Pinned OpenAI Authenticode signature verification failed: $path"
            }
        }
    }
    $package = Get-Content -LiteralPath (Join-Path $Directory 'codex-package.json') -Raw | ConvertFrom-Json
    if ($package.layoutVersion -ne 1 -or $package.version -ne $manifest.version -or
        $package.target -ne $manifest.target -or $package.variant -ne 'codex' -or
        $package.entrypoint -ne 'bin/codex.exe' -or $package.resourcesDir -ne 'codex-resources' -or
        $package.pathDir -ne 'codex-path') {
        throw 'Pinned vendor package metadata is inconsistent.'
    }
    if ($WithDocumentation) {
        foreach ($notice in $manifest.notices) { Assert-PinnedFile (Join-Path $Directory ('licenses/' + $notice.path)) $notice }
        if ((Get-FileHash -LiteralPath (Join-Path $Directory 'runtime-manifest.json') -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash) { throw 'Staged runtime manifest differs from the reviewed manifest.' }
    }
}

foreach ($notice in $manifest.notices) { Assert-PinnedFile (Join-Path $licenseRoot $notice.path) $notice }
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA ('Codex\Caches\CodexLover\official-codex\rust-v' + $manifest.version + '\win-x64')))
[IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
$archive = Join-Path $cacheRoot $manifest.archive.name
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    if ($Offline) { throw 'Pinned runtime is not cached and Offline mode forbids downloading.' }
    $download = Join-Path $cacheRoot ('download-' + [Guid]::NewGuid().ToString('N') + '.partial')
    Invoke-WebRequest -Uri $manifest.archive.url -OutFile $download
    Assert-PinnedFile $download $manifest.archive
    Move-Item -LiteralPath $download -Destination $archive
}
Assert-PinnedFile $archive $manifest.archive
$expanded = Join-Path $cacheRoot 'expanded'
if (-not (Test-Path -LiteralPath $expanded -PathType Container)) {
    # Exact archive hash is required before listing or extracting. Only reviewed
    # relative file/directory entries are allowed; extraction never uses user data.
    $entries = @(& tar -tzf $archive)
    if ($LASTEXITCODE -ne 0) { throw 'Could not list pinned Codex runtime archive.' }
    $expectedEntries = @($manifest.files | ForEach-Object { $_.path }) + @('bin/', 'codex-path/', 'codex-resources/')
    if (@(Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $entries).Count -ne 0) {
        throw 'Pinned runtime archive layout differs from the reviewed file list.'
    }
    $staging = Join-Path $cacheRoot ('extract-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    & tar -xzf $archive -C $staging
    if ($LASTEXITCODE -ne 0) { throw 'Pinned runtime extraction failed; staging is preserved for inspection.' }
    Assert-RuntimeDirectory $staging
    # Both absolute paths must remain in this exact runtime cache before moving.
    foreach ($movePath in @($staging, $expanded)) {
        if (-not [IO.Path]::GetFullPath($movePath).StartsWith($cacheRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Runtime cache move escaped the intended directory.'
        }
    }
    Move-Item -LiteralPath $staging -Destination $expanded
}
Assert-RuntimeDirectory $expanded

$resultRoot = $expanded
if (-not [string]::IsNullOrWhiteSpace($DestinationDirectory)) {
    $destination = [IO.Path]::GetFullPath($DestinationDirectory)
    if (Test-Path -LiteralPath $destination) { throw 'Runtime destination already exists; existing files are preserved.' }
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    foreach ($file in $manifest.files) {
        $target = Join-Path $destination $file.path
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $expanded $file.path) -Destination $target
    }
    $targetLicenses = Join-Path $destination 'licenses'
    [IO.Directory]::CreateDirectory($targetLicenses) | Out-Null
    foreach ($notice in $manifest.notices) { Copy-Item -LiteralPath (Join-Path $licenseRoot $notice.path) -Destination (Join-Path $targetLicenses $notice.path) }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $destination 'runtime-manifest.json')
    Assert-RuntimeDirectory $destination $true
    $resultRoot = $destination
}

[pscustomobject]@{
    version = $manifest.version
    root = $resultRoot
    executable = Join-Path $resultRoot 'bin\codex.exe'
    archiveSha256 = $manifest.archive.sha256
    manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    signerName = $manifest.signerName
    signerCertificateThumbprint = $manifest.signerCertificateThumbprint
}
