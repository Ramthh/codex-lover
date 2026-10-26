# Compiled clean-Windows lifecycle harness

This self-contained Windows x64 console executable preserves the nine checks in
the existing `Invoke-CleanWindowsLifecycle.ps1` kit harness and adds two custom-folder
checks, without invoking any
script engine or modifying execution policy. It refuses to run unless the current
user is `WDAGUtilityAccount` and the profile is `C:\Users\WDAGUtilityAccount`.
It also requires no existing app installation, saved data, shortcut, startup
registration, or running app process.

Publish with the project's .NET 10 SDK using Release, self-contained `win-x64`,
and single-file publishing (defaults in the project file). Put the resulting
`CodexLover.CleanWindowsTest.exe` alongside `current-setup.exe`,
`previous-setup.exe`, and a generated `kit-manifest.json`. The manifest must declare
`"harnessKind": "compiled-v2"` and exactly those three named inputs with their
SHA-256 hashes and byte lengths. The harness verifies all three inputs, including
that the running executable is the manifest-bound executable, before installation.

Inside a fresh, disposable Sandbox only:

```text
CodexLover.CleanWindowsTest.exe --input-directory C:\CodexLoverTest\Input --results-directory C:\CodexLoverTest\Results
```

The result uses the existing lifecycle schema and original nine check names, plus
`custom installation folder preserves earlier releases and user data` and
`reinstall discovers and preserves the registered custom folder`. The manifest may
declare `previousShortcutName` as `Codex Account Monitor.lnk` (default) or
`Codex Lover.lnk` for its baseline. The candidate always requires `Codex Lover.lnk`
and absence of the legacy shortcut. Added
`harnessKind` and `environment` fields identify the compiled harness and guest
user/profile. Result writes are atomic within the results directory; failures
are reported with a nonzero exit code. A timed-out child process is never killed.
Existing results or diagnostic files are refused without modification. Diagnostic
evidence must identify the installed executable and a timestamp within its launch.

The test verifies actual installer verification, install/upgrade/reinstall,
diagnostic app startup, startup registry and Start shortcut, damage recovery,
uninstall, and data retention. It additionally tests installation into
`C:\CodexLoverTest\Custom App\CodexLover`, discovery of that registered folder,
correct maintenance-wrapper checksums, and retention of earlier release files
and synthetic user data. It does not exercise OAuth, payment services,
interactive usage, SmartScreen approval, or startup after a guest reboot.

The final reinstall after uninstall expects startup enabled: after registration is
removed, the new installer applies its fresh-install default. Earlier steps still
verify explicit opt-out and preservation of existing disabled preferences.
