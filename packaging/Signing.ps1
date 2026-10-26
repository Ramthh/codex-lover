Set-StrictMode -Version Latest

function Assert-PublisherSigning {
    param([string] $CertificateThumbprint, [string] $SignToolPath, [switch] $LocalTest, [switch] $UnsignedBeta)
    if ($LocalTest -and $UnsignedBeta) { throw 'Choose either -LocalTest or -UnsignedBeta, not both.' }
    if ($UnsignedBeta) {
        if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -or -not [string]::IsNullOrWhiteSpace($SignToolPath)) {
            throw 'Unsigned beta mode cannot be combined with signing credentials.'
        }
        return
    }
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        if ($LocalTest) { return }
        throw 'Stable public releases require a trusted code-signing certificate and SignToolPath. Choose -LocalTest for local evaluation or -UnsignedBeta for an explicitly disclosed unsigned beta.'
    }
    if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) { throw 'The signing tool was not found. Provide -SignToolPath.' }
    $thumbprint = $CertificateThumbprint.Replace(' ', '')
    $certificate = Get-Item -LiteralPath ("Cert:\CurrentUser\My\" + $thumbprint) -ErrorAction SilentlyContinue
    if ($null -eq $certificate) { $certificate = Get-Item -LiteralPath ("Cert:\LocalMachine\My\" + $thumbprint) -ErrorAction SilentlyContinue }
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey) { throw 'The selected signing certificate and its private key must be available in the Windows certificate store.' }
    if ($certificate.NotBefore -gt [DateTime]::Now -or $certificate.NotAfter -le [DateTime]::Now) { throw 'The selected signing certificate is not currently valid.' }
    if ($certificate.Subject -eq $certificate.Issuer) { throw 'A self-signed certificate cannot sign a public release.' }
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
        if (-not $chain.Build($certificate)) { throw 'The code-signing certificate did not validate to a trusted, non-revoked chain. Public packaging stopped.' }
    }
    finally { $chain.Dispose() }
}

function Assert-PayloadReleaseChannel {
    param([Parameter(Mandatory)] $Manifest, [switch] $LocalTest, [switch] $UnsignedBeta)
    if ($LocalTest -and $UnsignedBeta) { throw 'Choose either -LocalTest or -UnsignedBeta, not both.' }
    if ($LocalTest) { return }
    if ($UnsignedBeta) {
        if ($Manifest.releaseChannel -ne 'unsigned-beta' -or $Manifest.signatureStatus -ne 'NotSigned') {
            throw 'Unsigned beta installers require a payload explicitly published with -UnsignedBeta. Local evaluation payloads cannot be promoted.'
        }
        if ($Manifest.configuration -ne 'Release' -or $Manifest.runtime -ne 'win-x64' -or $Manifest.deployment -ne 'self-contained') {
            throw 'Unsigned beta installers require a Release, self-contained Windows x64 payload.'
        }
        return
    }
    if ($Manifest.releaseChannel -ne 'public' -or $Manifest.signatureStatus -ne 'Valid') {
        throw 'Stable public installers require a payload published in public mode with a valid publisher signature.'
    }
}

function Invoke-PublisherSigning {
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $CertificateThumbprint, [Parameter(Mandatory)] [string] $SignToolPath)
    $thumbprint = $CertificateThumbprint.Replace(' ', '')
    $signArguments = @('sign', '/sha1', $thumbprint, '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
    if (-not (Test-Path -LiteralPath ("Cert:\CurrentUser\My\" + $thumbprint))) { $signArguments += '/sm' }
    & $SignToolPath @signArguments $Path
    if ($LASTEXITCODE -ne 0) { throw 'Publisher signing failed.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
        throw 'The artifact lacks the expected trusted, timestamped publisher signature.'
    }
}
