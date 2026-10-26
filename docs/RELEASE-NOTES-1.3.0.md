# Version 1.3.0 — 5 September 2026

This release implements the app-review improvements: safer account/reset handling, recoverable single-account maintenance, accurate health summaries, reset countdowns, actionable errors, configurable alerts, saved window placement, account organization, readable appearance options, onboarding tools, and a verified installer with Windows startup support.

The runtime is now .NET 10 LTS. The original profile data and earlier release folders are preserved. API fallback remains unimplemented and its misleading enable/key-entry controls are removed; legacy key removal remains available.

Distribution status: local unsigned build. Signing hooks are ready, but a trusted publisher certificate is required before a signed public release. No automatic hosted updater is configured.

Validation details are saved alongside the release in the 1.3.0-validation review folder. Browser sign-in and reset-credit consumption are intentionally not exercised against real accounts during verification.
