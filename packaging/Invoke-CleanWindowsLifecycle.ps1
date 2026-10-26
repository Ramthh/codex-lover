[CmdletBinding()]
param(
    [string] $InputDirectory = $PSScriptRoot,
    [string] $ResultsDirectory = 'C:\CodexLoverTest\Results'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This harness deliberately refuses ordinary accounts. It installs/uninstalls inside
# the disposable Sandbox only; do not weaken this guard to run it on a real PC.
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or
    [Environment]::GetFolderPath('UserProfile') -ne 'C:\Users\WDAGUtilityAccount') {
    throw 'This lifecycle harness runs only inside a disposable Windows Sandbox. No installation was changed.'
}

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\CodexLover'
$pointerPath = Join-Path $installRoot 'current.json'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexLover'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Account Monitor.lnk'
if ((Test-Path -LiteralPath $installRoot) -or (Test-Path -LiteralPath $uninstallKey) -or
    (Test-Path -LiteralPath $shortcutPath) -or (Get-Process -Name CodexAccountMonitor -ErrorAction SilentlyContinue)) {
    throw 'The Sandbox is not clean. Close it and start a new disposable session.'
}
$manifest = Get-Content -LiteralPath (Join-Path $InputDirectory 'kit-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files) {
    if ($file.name -ne [IO.Path]::GetFileName($file.name)) { throw 'Unsafe kit input name.' }
    $item = Get-Item -LiteralPath (Join-Path $InputDirectory $file.name)
    if ($item.Length -ne $file.bytes -or (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ne $file.sha256) {
        throw 'A kit input changed. Regenerate the kit before testing.'
    }
}
[IO.Directory]::CreateDirectory($ResultsDirectory) | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$result = [ordered]@{
    status = 'running'; startedUtc = [DateTimeOffset]::UtcNow.ToString('o'); completedUtc = $null
    os = (Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture)
    previousVersion = $manifest.previousVersion; currentVersion = $manifest.currentVersion
    inputs = $manifest.files
    inputManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $InputDirectory 'kit-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    candidateBuild = $null
    scope = 'Actual installers in a fresh offline Windows Sandbox; no account enrollment or live services.'
    checks = $checks; error = $null
}
function Save-Result { $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'result.json') -Encoding UTF8 }
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Check([string] $Name, [scriptblock] $Action) {
    & $Action
    $checks.Add([ordered]@{ name = $Name; passed = $true; checkedUtc = [DateTimeOffset]::UtcNow.ToString('o') })
    Save-Result
}
function Run-Program([string] $Executable, [string[]] $Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(90000)) { throw 'The test program did not exit within 90 seconds; it was not force-closed.' }
        $process.Refresh()
        if ($process.ExitCode -ne 0) { throw "Test program exited with code $($process.ExitCode)." }
    } finally { $process.Dispose() }
}
function Read-Receipt { Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json }
function Read-Startup { Get-ItemPropertyValue -LiteralPath $runKey -Name CodexAccountMonitor -ErrorAction SilentlyContinue }
function Read-BetaCandidateBuild([string] $ManifestPath, [string] $ExpectedVersion) {
    $build = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $expected = [ordered]@{
        product = 'Codex Account Monitor'; version = $ExpectedVersion
        releaseChannel = 'unsigned-beta'; signatureStatus = 'NotSigned'
        configuration = 'Release'; deployment = 'self-contained'; runtime = 'win-x64'
    }
    foreach ($name in $expected.Keys) {
        if ($build.PSObject.Properties.Name -notcontains $name -or $build.$name -cne $expected[$name]) {
            throw "Installed candidate manifest has unexpected $name; it is not the approved unsigned beta build."
        }
    }
    [ordered]@{
        version = $build.version; releaseChannel = $build.releaseChannel; signatureStatus = $build.signatureStatus
        configuration = $build.configuration; deployment = $build.deployment; runtime = $build.runtime
    }
}
function Assert-Activation([string] $Version, [bool] $Startup) {
    $receipt = Read-Receipt
    Assert ($receipt.version -eq $Version) 'The active receipt has the wrong version.'
    Assert ((Get-ItemPropertyValue -LiteralPath $uninstallKey -Name DisplayVersion) -eq $Version) 'Installed Apps has the wrong version.'
    Assert ($receipt.startWithWindows -eq $Startup) 'The receipt has the wrong startup preference.'
    $command = Read-Startup
    Assert (($Startup -and $command -eq ('"' + $receipt.executable + '" --minimized')) -or (-not $Startup -and $null -eq $command)) 'The startup registration is inconsistent.'
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        try { Assert ($shortcut.TargetPath -eq $receipt.executable) 'Start shortcut targets the wrong release.' }
        finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
}

$previousSetup = Join-Path $InputDirectory 'previous-setup.exe'
$currentSetup = Join-Path $InputDirectory 'current-setup.exe'
$retainedRoot = Join-Path $env:LOCALAPPDATA 'CodexAccountMonitor'
$sentinel = Join-Path $retainedRoot 'synthetic-retained-data.txt'
try {
    Check 'verify both installer payloads without installation' {
        Run-Program $previousSetup @('--verify-only','--silent')
        Run-Program $currentSetup @('--verify-only','--silent')
        Assert (-not (Test-Path -LiteralPath $installRoot)) 'Verification unexpectedly installed a release.'
    }
    Check 'fresh per-user install with startup disabled' {
        Run-Program $previousSetup @('--silent','--no-start-with-windows')
        Assert-Activation $manifest.previousVersion $false
    }
    $previousReceipt = Read-Receipt
    [IO.Directory]::CreateDirectory($retainedRoot) | Out-Null
    [IO.File]::WriteAllText($sentinel, 'Synthetic data only: preserve across installer upgrade and uninstall.')
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
    Check 'upgrade preserves startup choice and earlier release' {
        Run-Program $currentSetup @('--silent')
        Assert-Activation $manifest.currentVersion $false
        Assert (Test-Path -LiteralPath $previousReceipt.executable) 'The previous release was deleted.'
        Assert ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $sentinelHash) 'Retained application data changed.'
    }
    Check 'self-contained app starts in diagnostic mode without a preinstalled runtime' {
        $installedExecutable = (Read-Receipt).executable
        $installedManifest = Join-Path (Split-Path -Parent $installedExecutable) 'publish-manifest.json'
        $result.candidateBuild = Read-BetaCandidateBuild $installedManifest $manifest.currentVersion
        $reportPath = Join-Path $ResultsDirectory 'app-diagnostics.json'
        Run-Program $installedExecutable @('--diagnostics="' + $reportPath + '"')
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        Assert ($report.version -eq ($manifest.currentVersion + '.0')) 'Application version differs from installer.'
        Assert ($report.accountCount -eq 0) 'A clean machine unexpectedly contained saved accounts.'
    }
    Check 'reinstall enables startup when explicitly requested' {
        Run-Program $currentSetup @('--silent','--start-with-windows')
        Assert-Activation $manifest.currentVersion $true
    }
    Check 'reinstall explicitly disables startup' {
        Run-Program $currentSetup @('--silent','--no-start-with-windows')
        Assert-Activation $manifest.currentVersion $false
    }
    Check 'damaged installed release is preserved and replaced by a verified fresh copy' {
        $before = Read-Receipt
        [IO.File]::AppendAllText((Join-Path (Split-Path -Parent $before.executable) 'CodexAccountMonitor.Core.dll'), 'synthetic-damage')
        Run-Program $currentSetup @('--silent')
        $after = Read-Receipt
        Assert ($before.executable -ne $after.executable) 'Setup reused a damaged release.'
        Assert (Test-Path -LiteralPath $before.executable) 'Setup deleted the older damaged release.'
        Assert-Activation $manifest.currentVersion $false
    }
    $lastReceipt = Read-Receipt
    Check 'uninstall removes Windows registration and preserves release plus user data' {
        $command = Get-ItemPropertyValue -LiteralPath $uninstallKey -Name QuietUninstallString
        Assert ($command -match '^"([^"]+)" --uninstall --silent$') 'Unexpected uninstall command.'
        $uninstaller = $Matches[1]
        Assert ($uninstaller.StartsWith(($installRoot + '\maintenance\'), [StringComparison]::OrdinalIgnoreCase)) 'Unexpected maintenance path.'
        Run-Program $uninstaller @('--uninstall','--silent')
        Assert (-not (Test-Path -LiteralPath $pointerPath)) 'The active pointer remains after uninstall.'
        Assert (-not (Test-Path -LiteralPath $shortcutPath)) 'The Start shortcut remains after uninstall.'
        Assert (-not (Test-Path -LiteralPath $uninstallKey)) 'Installed Apps registration remains after uninstall.'
        Assert ($null -eq (Read-Startup)) 'The Windows startup entry remains after uninstall.'
        Assert (Test-Path -LiteralPath $lastReceipt.executable) 'The retained release disappeared.'
        Assert ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $sentinelHash) 'Retained application data changed.'
    }
    Check 'reinstall after uninstall restores a valid activation' {
        Run-Program $currentSetup @('--silent')
        Assert-Activation $manifest.currentVersion $false
    }
    $result.status = 'passed'
} catch {
    $result.status = 'failed'
    $result.error = $_.Exception.Message
} finally {
    $result.completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Save-Result
}
if ($result.status -ne 'passed') { throw $result.error }
