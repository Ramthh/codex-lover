[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Load only the pure manifest-reader function. Never run the lifecycle harness on
# this host; these fixtures cannot invoke installation or touch Windows settings.
$harness = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging\Invoke-CleanWindowsLifecycle.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($harness, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Lifecycle harness has a PowerShell parse error.' }
$reader = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Read-BetaCandidateBuild' }, $false)
if ($null -eq $reader) { throw 'Candidate proof reader was not found.' }
. ([scriptblock]::Create($reader.Extent.Text))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('CodexLover-BetaProof-' + [Guid]::NewGuid().ToString('N') + '.json')
$expected = [ordered]@{ product='Codex Account Monitor'; version='1.7.4'; releaseChannel='unsigned-beta'; signatureStatus='NotSigned'; configuration='Release'; deployment='self-contained'; runtime='win-x64' }
$passed = 0
function Check-Rejection([string] $Label, $Manifest) {
    $Manifest | ConvertTo-Json | Set-Content -LiteralPath $fixture -Encoding UTF8
    $rejected = $false
    try { $null = Read-BetaCandidateBuild $fixture '1.7.4' } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid candidate metadata: $Label" }
    Write-Output "PASS $Label"
}
try {
    $expected | ConvertTo-Json | Set-Content -LiteralPath $fixture -Encoding UTF8
    $actual = Read-BetaCandidateBuild $fixture '1.7.4'
    if ($actual.Count -ne 6 -or $actual.releaseChannel -cne 'unsigned-beta') { throw 'Valid candidate proof changed shape.' }
    $passed++
    Write-Output 'PASS correctly marked candidate yields six proof fields'
    foreach ($field in $expected.Keys) {
        $changed = [ordered]@{}
        foreach ($key in $expected.Keys) { $changed[$key] = $expected[$key] }
        $changed[$field] = switch ($field) { 'releaseChannel' { 'local-test' } 'version' { '1.7.3' } 'signatureStatus' { 'Valid' } 'runtime' { 'win-arm64' } 'configuration' { 'Debug' } 'deployment' { 'framework-dependent' } default { 'Other product' } }
        Check-Rejection "reject unexpected $field" $changed
        $passed++
        $changed.Remove($field)
        Check-Rejection "reject missing $field" $changed
        $passed++
    }
    Write-Output "$passed beta candidate proof checks passed. Only a synthetic temporary JSON fixture was written."
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture }
}
