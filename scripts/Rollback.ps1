[CmdletBinding()]
param([Parameter(Mandatory)] [string] $PreviousExecutable, [switch] $VerifyOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $PreviousExecutable).Path
$defaultInstallRoot = Join-Path $env:LOCALAPPDATA 'Programs\CodexLover'
$installRoot = $defaultInstallRoot
$registeredRoot = Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexLover' -Name InstallLocation -ErrorAction SilentlyContinue
if ($registeredRoot -is [string] -and -not [string]::IsNullOrWhiteSpace($registeredRoot)) {
    if ($registeredRoot -notmatch '^[A-Za-z]:[\\/]' -or $registeredRoot.Substring(2).Contains(':')) {
        throw 'The registered Codex Lover installation folder is not a valid local path. Repair the installation before rollback.'
    }
    $installRoot = [IO.Path]::GetFullPath($registeredRoot).TrimEnd('\')
    if ($installRoot -eq [IO.Path]::GetPathRoot($installRoot).TrimEnd('\')) {
        throw 'The registered Codex Lover installation folder cannot be a drive root.'
    }
    for ($ancestor = [IO.DirectoryInfo]::new($installRoot); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Rollback does not follow a linked installation folder.'
        }
    }
}
$allowedRoots = @($installRoot, $defaultInstallRoot)
$approvedRoot = $allowedRoots | Where-Object { $executable.StartsWith(([IO.Path]::GetFullPath($_).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
if (-not $approvedRoot -or [IO.Path]::GetFileName($executable) -ne 'CodexAccountMonitor.exe') {
    throw 'Choose a previously packaged Codex Lover executable from the registered or default installation folder.'
}
$releaseDirectory = Split-Path -Parent $executable
$manifestPath = Join-Path $releaseDirectory 'publish-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Rollback requires the release manifest beside the executable.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.product -ne 'Codex Account Monitor' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'This is not a supported Codex Lover release manifest.' }
$listedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files) {
    if ([IO.Path]::IsPathRooted($file.path) -or $file.path.Contains(':')) { throw 'The release manifest contains an unsafe path.' }
    $filePath = [IO.Path]::GetFullPath((Join-Path $releaseDirectory $file.path))
    if (-not $filePath.StartsWith(($releaseDirectory.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or -not $listedFiles.Add($filePath)) { throw 'The release manifest contains an unsafe or duplicate path.' }
    $item = Get-Item -LiteralPath $filePath
    $ancestor = $item
    while ($null -ne $ancestor -and $ancestor.FullName.StartsWith(([IO.Path]::GetFullPath($approvedRoot)), [StringComparison]::OrdinalIgnoreCase)) {
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Rollback does not follow linked release files or folders.' }
        $ancestor = if ($ancestor -is [IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent }
    }
    if ($item.PSIsContainer -or $item.Length -ne $file.bytes -or (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $file.sha256) { throw 'A release file failed its checksum. Rollback stopped without changing startup or shortcuts.' }
}
if (-not $listedFiles.Contains($executable)) { throw 'The manifest does not cover the application executable.' }
foreach ($actualFile in Get-ChildItem -LiteralPath $releaseDirectory -File -Recurse) {
    if (-not $listedFiles.Contains($actualFile.FullName) -and $actualFile.FullName -ne $manifestPath -and $actualFile.FullName -ne (Join-Path $releaseDirectory 'installation.json')) {
        throw 'The release folder contains an unlisted file. Rollback stopped before activation.'
    }
}
if ($manifest.PSObject.Properties.Name -contains 'releaseChannel' -and $manifest.releaseChannel -eq 'public') {
    if ((Get-AuthenticodeSignature -LiteralPath $executable).Status -ne 'Valid') { throw 'The public release publisher signature is invalid.' }
}
if ($VerifyOnly) { Write-Output "Verified rollback candidate $($manifest.version). No settings were changed."; return }
$currentSession = (Get-Process -Id $PID).SessionId
if (Get-Process -Name CodexAccountMonitor -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $currentSession }) { throw 'Finish active operations and exit the widget using its tray menu before rollback.' }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startup = Get-ItemPropertyValue -LiteralPath $runKey -Name CodexAccountMonitor -ErrorAction SilentlyContinue
$programs = [Environment]::GetFolderPath('Programs')
$shortcutPath = Join-Path $programs 'Codex Lover.lnk'
$legacyShortcutPath = Join-Path $programs 'Codex Account Monitor.lnk'
$pointerPath = Join-Path $installRoot 'current.json'
$previousShortcut = if (Test-Path -LiteralPath $shortcutPath) { [IO.File]::ReadAllBytes($shortcutPath) } else { $null }
$previousLegacyShortcut = if (Test-Path -LiteralPath $legacyShortcutPath) { [IO.File]::ReadAllBytes($legacyShortcutPath) } else { $null }
$previousPointer = if (Test-Path -LiteralPath $pointerPath) { [IO.File]::ReadAllBytes($pointerPath) } else { $null }
$stageShortcut = Join-Path $programs ('CodexLover-rollback-' + [Guid]::NewGuid().ToString('N') + '.lnk')
$legacyShortcutRemoved = $false
try {
    [IO.Directory]::CreateDirectory($programs) | Out-Null
    [IO.Directory]::CreateDirectory($installRoot) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($stageShortcut)
        try { $shortcut.TargetPath = $executable; $shortcut.WorkingDirectory = $releaseDirectory; $shortcut.Description = 'Codex Lover account quota monitor'; $shortcut.Save() }
        finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    if ($null -ne $previousPointer) {
        $history = Join-Path $installRoot 'activation-history'
        [IO.Directory]::CreateDirectory($history) | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $history ('rollback-' + [Guid]::NewGuid().ToString('N') + '.json')), $previousPointer)
    }
    [IO.File]::Copy($stageShortcut, $shortcutPath, $true)
    if ($startup -is [string]) { Set-ItemProperty -LiteralPath $runKey -Name CodexAccountMonitor -Value ('"' + $executable + '" --minimized') }
    @{ version = $manifest.version; executable = $executable; activatedUtc = [DateTimeOffset]::UtcNow.ToString('o'); startWithWindows = ($startup -is [string]); rollback = $true } | ConvertTo-Json | Set-Content -LiteralPath $pointerPath -Encoding utf8
    if ($null -ne $previousLegacyShortcut) {
        Remove-Item -LiteralPath $legacyShortcutPath
        $legacyShortcutRemoved = $true
    }
} catch {
    $activationError = $_.Exception
    $restoreErrors = [Collections.Generic.List[Exception]]::new()
    try { if ($startup -is [string]) { Set-ItemProperty -LiteralPath $runKey -Name CodexAccountMonitor -Value $startup } } catch { $restoreErrors.Add($_.Exception) }
    try {
        if ($null -eq $previousShortcut) { if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath } } else { [IO.File]::WriteAllBytes($shortcutPath, $previousShortcut) }
    } catch { $restoreErrors.Add($_.Exception) }
    try { if ($legacyShortcutRemoved) { [IO.File]::WriteAllBytes($legacyShortcutPath, $previousLegacyShortcut) } } catch { $restoreErrors.Add($_.Exception) }
    try {
        if ($null -eq $previousPointer) { if (Test-Path -LiteralPath $pointerPath) { Remove-Item -LiteralPath $pointerPath } } else { [IO.File]::WriteAllBytes($pointerPath, $previousPointer) }
    } catch { $restoreErrors.Add($_.Exception) }
    if ($restoreErrors.Count -gt 0) {
        $restoreErrors.Insert(0, $activationError)
        throw [AggregateException]::new('Rollback activation failed and Windows prevented some recovery steps. Earlier releases and account data were preserved.', $restoreErrors)
    }
    throw $activationError
} finally { if (Test-Path -LiteralPath $stageShortcut) { Remove-Item -LiteralPath $stageShortcut } }
Start-Process -FilePath $executable -WindowStyle Hidden
Write-Output "Started verified release $($manifest.version). Account data and release folders were preserved."
