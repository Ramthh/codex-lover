[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PreviousSetup,
    [Parameter(Mandatory)] [string] $PreviousVersion,
    [Parameter(Mandatory)] [string] $CurrentSetup,
    [Parameter(Mandatory)] [string] $CurrentVersion,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $CompiledHarness,
    [ValidateSet('Codex Account Monitor.lnk', 'Codex Lover.lnk')]
    [string] $PreviousShortcutName = 'Codex Account Monitor.lnk'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new test-kit directory; previous evidence is preserved.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq [IO.Path]::GetPathRoot($output)) { throw 'Test-kit directory cannot be a drive root.' }
$useCompiledHarness = -not [string]::IsNullOrWhiteSpace($CompiledHarness)
if ($useCompiledHarness -and (-not (Test-Path -LiteralPath $CompiledHarness -PathType Leaf) -or
    [IO.Path]::GetExtension($CompiledHarness) -ine '.exe')) {
    throw 'CompiledHarness must name the compiled-v2 test executable.'
}
$inputFolder = Join-Path $output 'Input'
$resultsFolder = Join-Path $output 'Results'
[IO.Directory]::CreateDirectory($inputFolder) | Out-Null
[IO.Directory]::CreateDirectory($resultsFolder) | Out-Null
Copy-Item -LiteralPath $PreviousSetup -Destination (Join-Path $inputFolder 'previous-setup.exe')
Copy-Item -LiteralPath $CurrentSetup -Destination (Join-Path $inputFolder 'current-setup.exe')
if ($useCompiledHarness) {
    Copy-Item -LiteralPath $CompiledHarness -Destination (Join-Path $inputFolder 'CodexLover.CleanWindowsTest.exe')
    $logonCommand = 'C:\CodexLoverTest\Input\CodexLover.CleanWindowsTest.exe'
} else {
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging\Invoke-CleanWindowsLifecycle.ps1') -Destination $inputFolder
    $logonCommand = 'powershell.exe -NoProfile -File C:\CodexLoverTest\Input\Invoke-CleanWindowsLifecycle.ps1'
}
$files = @(Get-ChildItem -LiteralPath $inputFolder -File | ForEach-Object {
    [ordered]@{ name = $_.Name; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ previousVersion = $PreviousVersion; currentVersion = $CurrentVersion; createdUtc = [DateTimeOffset]::UtcNow.ToString('o'); files = $files }
if ($useCompiledHarness) {
    $manifest.harnessKind = 'compiled-v2'
    $manifest.previousShortcutName = $PreviousShortcutName
}
$manifest |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $inputFolder 'kit-manifest.json') -Encoding UTF8
$escapedInput = [Security.SecurityElement]::Escape($inputFolder)
$escapedResults = [Security.SecurityElement]::Escape($resultsFolder)
@"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$escapedInput</HostFolder><SandboxFolder>C:\CodexLoverTest\Input</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$escapedResults</HostFolder><SandboxFolder>C:\CodexLoverTest\Results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>$logonCommand</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath (Join-Path $output 'Run-CleanWindows.wsb') -Encoding UTF8
$launcherDescription = if ($useCompiledHarness) {
    'This kit uses compiled-v2: a self-contained executable launched directly, without a PowerShell script or any execution-policy change. The baseline shortcut name is declared in the manifest. The candidate must create Codex Lover.lnk and retire the legacy name.'
} else {
    'This is the legacy PowerShell kit for historical installers with the Codex Account Monitor shortcut. It does not test current branded shortcuts or custom installation folders. Use -CompiledHarness with the current compiled-v2 executable for current releases.'
}
$additionalChecks = if ($useCompiledHarness) {
    'The compiled-v2 harness adds two checks for moving to a custom local folder with spaces and discovering that registered folder on reinstall. It verifies branded shortcut migration, InstallLocation, startup paths, current uninstaller checksums, retained earlier files/data, and a diagnostic launch from the custom folder. Require all eleven exact checks. Existing result or diagnostic evidence is refused; create another kit for a rerun.'
} else {
    'The legacy harness requires all nine original checks and does not establish custom-folder or new-shortcut behavior.'
}
@"
# Clean Windows installer lifecycle kit

Prepared only. The kit is not evidence that a clean-machine test passed.

$launcherDescription

When Windows Sandbox is already available, open Run-CleanWindows.wsb. This creates a fresh, offline Windows Sandbox with only Input (read-only) and Results (writable) mapped. It does not expose the source tree, accounts or credentials. The automation must run as the default WDAGUtilityAccount; it refuses ordinary Windows accounts. It never force-closes a process.

The harness verifies all three input checksums, installs the previous release, upgrades to the candidate, validates that the installed candidate manifest retains unsigned-beta / NotSigned / Release / self-contained / win-x64 build metadata, starts the self-contained app in read-only diagnostic mode, changes startup preferences, repairs a deliberately damaged release, uninstalls, and reinstalls. Synthetic user-data retention is checked. Results/result.json must say "passed" and contain every completed check, input hashes/sizes, the input-manifest hash and candidateBuild metadata. App diagnostics are separately saved there. Internal build-channel metadata does not determine the installer display name.

$additionalChecks

If Sandbox is unavailable or local execution policy blocks the script, no clean-machine result exists. Do not disable security protections to manufacture a pass. Enable Sandbox only through an explicitly approved OS setup, or use an existing clean Windows VM and a human-reviewed equivalent lifecycle checklist. No OS features or security settings are changed by generating this kit.

The offline harness does not verify public publisher trust, SmartScreen reputation, Google/X login, billing, Codex enrollment, account switching, tray behavior, or a real Windows sign-in/startup cycle. Online and interactive behavior need corresponding controlled end-to-end tests. An unsigned executable has no verified publisher identity and may be blocked by Windows policies; this harness does not change those policies or establish publisher trust. Do not copy live account data into a clean test machine.
"@ | Set-Content -LiteralPath (Join-Path $output 'README.md') -Encoding UTF8
Write-Output $output
