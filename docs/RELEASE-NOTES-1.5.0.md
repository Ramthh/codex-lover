# 1.5.0 release candidate

- Mandatory Codex Lover browser sign-in using Google or X device authorization with PKCE.
- A three-day trial supports two saved accounts. Annual Pro supports unlimited saved accounts; the former ten-account cap is removed and refresh concurrency remains bounded at two.
- Online verification at startup and every two minutes; expired/unverified accounts cannot enroll, refresh, switch, reset or reauthenticate. Credential removal remains available.
- Membership management/renewal opens the website billing dashboard. The desktop trusts server-confirmed entitlements, never browser checkout success alone.
- Opaque website sessions are encrypted with Windows DPAPI. Local Codex sign-ins, usage and profile emails never leave the PC for the website.
- Published website news appears in a slow left-to-right ticker. Click/Enter opens the validated article URL; hover/focus/Space and the Windows reduced animation preference control motion.
- Existing Codex switch recovery, readable menus, notification preferences and Windows startup behavior remain intact.

This candidate was built and tested in a new source folder. It was not installed or used to switch any real account. Live provider sign-in, PayPal purchase/renewal, production device authorization and production news publication require end-to-end verification before distribution. Keep the website download disabled until that verification is complete.
