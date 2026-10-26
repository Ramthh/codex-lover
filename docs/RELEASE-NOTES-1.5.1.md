# 1.5.1 login handoff fix

- After browser sign-in returns a valid desktop membership, the login window closes and the account monitor opens automatically. There is no extra Continue to app step.
- A successful Retry membership check also continues automatically. Expired, cancelled and unverified sessions remain locked.
- The account window is initialized and its membership controls are set before it is shown. Explicit sign-in returns to the visible app even if the process originally launched minimized.
- Google and X remain the supported providers. Google is connected in production; X setup is still pending.

This update preserves the existing Windows startup preference, local Codex accounts and membership session. Website downloads and checkout remain disabled.
