# Codex Account Monitor 1.1.0

## Widget fixes

- Shows connected accounts plus exactly one next **Add account** card; the add card disappears at the ten-account limit.
- Uses the canonical account-wide `codex` quota bucket and never combines independent meters. Missing or ambiguous windows display an em dash instead of a fabricated `100%`.
- Labels quota values as **5h left** and **Weekly left**.
- Runs as a compact notification-area widget, hidden from the taskbar and Alt+Tab.
- Defaults **Keep above other windows** to off, including a one-time migration from earlier builds, so normal applications cover the widget.
- Adds adjustable background opacity from 35% to 95%, defaulting to 72%, while text remains fully opaque.
- Provides compact one-column and expanded two-column layouts with content-driven height and work-area scrolling.

## Install

1. Extract the self-contained ZIP to a new folder.
2. Run `CodexAccountMonitor.exe`.
3. The package includes the required .NET runtime; the separate Windows Desktop Runtime installer is not required for this build.

Existing enrolled profiles remain under `%LOCALAPPDATA%\CodexAccountMonitor`. This release migrates only widget presentation settings; it does not remove account data.

## Verification

- Release tests: 100 passed, 0 failed.
- NuGet direct and transitive vulnerability audit: no vulnerable packages reported.
- Visual QA: compact real-profile state, ten-account expanded demo, layered transparency, tray ownership, and non-topmost behavior.
