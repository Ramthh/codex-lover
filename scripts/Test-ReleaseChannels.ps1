[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging\Signing.ps1')
$script:passed = 0
function Check([string] $Name, [scriptblock] $Action) {
    & $Action
    $script:passed++
    Write-Output "PASS $Name"
}
function Reject([scriptblock] $Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'An unsupported release configuration was accepted.' }
}
function Payload([string] $Channel, [string] $Signature) {
    [pscustomobject]@{ releaseChannel = $Channel; signatureStatus = $Signature; configuration = 'Release'; runtime = 'win-x64'; deployment = 'self-contained' }
}
Check 'default stable packaging rejects missing signing credentials' { Reject { Assert-PublisherSigning } }
Check 'explicit unsigned beta requires no certificate' { Assert-PublisherSigning -UnsignedBeta }
Check 'local evaluation remains available' { Assert-PublisherSigning -LocalTest }
Check 'beta cannot silently become local evaluation' { Reject { Assert-PublisherSigning -UnsignedBeta -LocalTest } }
Check 'beta rejects a conflicting certificate' { Reject { Assert-PublisherSigning -UnsignedBeta -CertificateThumbprint 'synthetic' } }
Check 'beta rejects a conflicting signing tool' { Reject { Assert-PublisherSigning -UnsignedBeta -SignToolPath 'synthetic' } }
Check 'stable rejects unsigned beta payload' { Reject { Assert-PayloadReleaseChannel -Manifest (Payload 'unsigned-beta' 'NotSigned') } }
Check 'stable rejects local evaluation payload' { Reject { Assert-PayloadReleaseChannel -Manifest (Payload 'local-test' 'NotSigned') } }
Check 'beta rejects local evaluation promotion' { Reject { Assert-PayloadReleaseChannel -UnsignedBeta -Manifest (Payload 'local-test' 'NotSigned') } }
Check 'beta accepts an explicitly marked unsigned release payload' { Assert-PayloadReleaseChannel -UnsignedBeta -Manifest (Payload 'unsigned-beta' 'NotSigned') }
Check 'beta rejects a claimed signed payload' { Reject { Assert-PayloadReleaseChannel -UnsignedBeta -Manifest (Payload 'unsigned-beta' 'Valid') } }
Check 'beta rejects debug payload' { $m = Payload 'unsigned-beta' 'NotSigned'; $m.configuration = 'Debug'; Reject { Assert-PayloadReleaseChannel -UnsignedBeta -Manifest $m } }
Check 'beta rejects unsupported architecture' { $m = Payload 'unsigned-beta' 'NotSigned'; $m.runtime = 'win-arm64'; Reject { Assert-PayloadReleaseChannel -UnsignedBeta -Manifest $m } }
Check 'beta rejects a runtime-dependent payload' { $m = Payload 'unsigned-beta' 'NotSigned'; $m.deployment = 'framework-dependent'; Reject { Assert-PayloadReleaseChannel -UnsignedBeta -Manifest $m } }
Write-Output "$script:passed release-channel checks passed. No certificate store, trust, registry, or installed application was changed."
