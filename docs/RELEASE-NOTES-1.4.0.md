# Version 1.4.0 — 5 September 2026

**Switch** replaces **Live updates** on account cards. It opens a dialog for selecting which saved account the Codex desktop app will use. Finish your tasks, quit Codex and other Codex CLI sessions, then choose **Switch and open Codex**. The monitor verifies the saved account with the service, changes the local Codex sign-in, and opens the verified signed desktop app. New requests use the selected account's available quota.

This requires quitting and reopening Codex. The monitor never force-closes it or transfers a running task to another account. The change affects CLI sessions sharing the same local Codex home. It changes only the desktop home's `auth.json`; projects, Codex settings, and the monitor's Windows startup preference remain in place.

The **Selected** label means the linked saved account matches the desktop login on disk. It does not prove the identity used by a process that was already running. Check the account shown in Codex after it reopens.

Switching supports local file-based ChatGPT credentials. A configured keyring/`auto` credential store, incompatible forced sign-in method, or incompatible required workspace is refused without changing the configuration. A current-user DPAPI recovery journal protects the prior login, and recovery refuses to overwrite independently changed credentials. Source credential provenance prevents stale desktop saveback after a saved profile is reauthenticated.

For the account linked to desktop Codex, the monitor reads usage through a temporary access-token-only App Server. This avoids competing with the desktop to rotate its refresh token. Other account monitoring, reset-credit safeguards, alerts, and account organization remain available.

Account menus, tooltips, the Refresh button's hover state, and settings dropdowns now use readable dark surfaces. Selected, keyboard-highlighted, and disabled values have explicit contrasting text colors, including high-contrast popup colors. Disabled quiet-hour values remain readable.

Historical source folders, account data, and previous releases are preserved. Distribution remains a local unsigned build; trusted publisher signing and a hosted automatic updater are not configured.

The complete switch with the user's real desktop account is left for user testing after their current work finishes. Closing Codex during development would interrupt that work. Check the account displayed by Codex after reopening before starting a new request.
