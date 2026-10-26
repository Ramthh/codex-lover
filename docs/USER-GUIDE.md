# Using Codex Lover Desktop 1.7.7

Before the widget opens, choose **Continue with Google** and complete sign-in in your browser. The app opens automatically when sign-in succeeds. Your Codex Lover account manages membership; it is separate from the Codex accounts you add below. The app checks which sign-in providers are available. **X sign-in · unavailable** is disabled while the website's X sign-in is not configured; use Google for now. If sign-in options cannot load, check your connection and reopen Membership.

**Free** supports two Codex accounts indefinitely, with no three-day trial. **Pro** costs **$9.99 USD per month**, renews automatically through PayPal and supports unlimited saved accounts. Both plans include quota monitoring, switching eligible accounts, news, feedback and the published reset calendar. Each Codex account keeps its own usage limits; Pro does not add OpenAI quota or combine limits.

Select **Membership → Manage membership / Get Pro** to manage billing on the website. After a confirmed payment, choose **Retry membership check** to apply the updated access immediately. You can cancel renewal from the website and retain access through the paid expiry. Earlier paid access remains valid through its promised expiry. If Pro expires, extra profiles stay saved; the first two saved accounts remain usable on Free. Reorder accounts in Settings to choose those two.

Internet access is required for first sign-in and normal membership checks. If a network error, service outage or temporary rate limit prevents verification, a previously verified membership can remain usable for **up to one hour from its last successful verification**. Repeated failures do not restart that hour, and cached access cannot outlast the saved sign-in session or Pro expiry. A rejected sign-in or inactive membership does not receive this grace. The Membership window explains when cached access is being used. Reconnect and choose **Retry membership check** when needed; local Codex credentials remain saved. This grace concerns membership only: browser sign-in, live quota requests and feedback still require their services to be reachable.

Without valid membership access, protected operations such as refreshing, switching, reauthentication, reset redemption and enrollment are blocked. Saved credentials can still be removed through Settings.

The bottom news bar shows published Codex Lover news. Headlines move left to right; hover/focus pauses motion. Click or press Enter to open the article. Press Space to pause/resume. Windows reduced animation settings show static headlines.

## Open and start automatically

Open **Codex Account Monitor** from the Windows Start menu. Double-click its tray icon to reopen a hidden widget. In **Settings → Widget**, enable **Start with Windows** to start quietly in the tray whenever you sign in.

The installer uses your Windows account's `%LOCALAPPDATA%\Programs\CodexLover` folder. It does not require a D: drive or administrator elevation and preserves account data and earlier releases.

## Connect accounts

The Store package includes the official Codex connection runtime. Adding accounts and viewing usage do not require a separate desktop installation. Membership sign-in does not import or connect a Codex account.

1. Choose **Add account** and wait for **Check connection support** to finish. It verifies the included component automatically. **Retry check** and **Use automatic detection** help recover after a failed check or a stale custom path. If the included component is missing or damaged, repair or reinstall Codex Lover.
2. Enter an **Account name** (such as Personal), choose browser or device-code sign-in, and select **Connect account**.
3. Complete sign-in yourself. Use **Reopen browser** or **Copy code** if needed.
4. The monitor verifies the identity before saving the profile. Free supports two accounts; Pro has no product cap on saved accounts. Only two refreshes run concurrently.

Use **Settings → Accounts** to rename a profile, move it up or down, reconnect, or remove it. Removal deletes only its isolated monitor profile; it does not sign out Codex if that account was selected there. An idle monitor-owned session stops safely for maintenance; running managed work remains protected.

## Switch the account used by Codex

This feature requires the official OpenAI Codex desktop app installed separately. The included connection runtime does not replace the desktop app.

1. Finish your running Codex tasks and save edits. Quit any separate Codex CLI sessions; the monitor will not stop those sessions for you.
2. Choose **Switch** on the account you want to use, then **Switch & reopen** in the dialog. Leave Codex open: the monitor requests its normal Quit action automatically.
3. Follow the progress through checking, closing Codex, switching the saved sign-in, and reopening Codex. Keep the desktop unlocked and release keyboard modifiers while the Quit command is sent.
4. Check the account shown in Codex after it opens. New requests use that account's available usage limit.

This is a quit-and-reopen operation. **Quitting Codex can stop running tasks**, so let them finish before confirming. The monitor does not automatically save editor changes, resume interrupted turns, or transfer running work to another identity. It requests Codex's normal Quit command and never force-kills a process. Closing a Codex window alone may only hide it in the tray.

You can cancel while the app checks or waits for Codex to exit. Cancellation becomes unavailable during the short credential replacement and reopening step. If Codex refuses to exit, another CLI session remains open, or Windows cannot verify the process, the switch stops and leaves the desktop sign-in untouched. If verification fails after Codex has exited, the app attempts to reopen the previous login only when it can verify that recovery is safe.

The change also affects Codex CLI sessions that share the same local Codex home. Projects, Codex settings, saved monitor accounts, and your **Start with Windows** preference stay in place. Quota remains separate for each account.

**Selected** means the monitor's linked account matches the saved Codex login. It is not confirmation of the identity used by a process that was already running. If you sign in again elsewhere, the monitor may stop showing the label until the saved login can be verified.

Switching requires a supported local Codex home and file-based ChatGPT credentials. If the dialog reports a keyring/`auto` credential store, a required different sign-in method, or an incompatible required workspace, it leaves the current login and configuration unchanged. Refresh or sign in to an expired saved account before trying again.

If a switch reports that it needs recovery, quit Codex and other Codex CLI sessions, then retry **Switch**. The monitor uses its protected recovery record and will not overwrite a login that changed independently. Do not delete recovery files or copy credentials manually to bypass that message.

## Read the dashboard

Percentages show remaining quota. Each row uses the actual period reported by the service. A dash means unavailable, not zero. Reset countdowns show relative time; hover for the exact local/UTC timestamp. Quota is not assumed to be restored merely because a countdown reaches zero.

The header counts saved accounts and indicates accounts needing attention. The footer shows the oldest successful update. A failed refresh keeps last-known quota while displaying the failure and a Retry action. Stale status follows your configured refresh interval.

Accounts allowed by your membership receive periodic usage checks. For the account selected in Codex, the monitor reads usage with a temporary access-token-only session; it does not run tasks or rotate Codex's shared refresh token. If that login is unavailable or expired, refresh or sign in through Codex and retry the monitor refresh.

## Reset credits

**Use reset** requires confirmation and verifies identity again before the service action. If its result is uncertain, use **Check reset result**. This resolves the existing attempt with the same operation key rather than creating a new redemption.

## Codex resets calendar and feedback

The **Codex resets** footer displays today and the previous 29 days in UTC. Gray circles mean no reset notice was marked for that day; green circles indicate a date marked by the Codex Lover administrator. Hover over a day for its full date and any published note. The calendar is shared editorial information, not a record of your individual account's quota resets, and it does not consume reset credits.

The app checks the calendar and update notice about every two minutes while running. If the service is temporarily unavailable, it keeps the last fetched calendar. Hover over the summary for the last check time. Before the first successful fetch, it shows **Waiting for the published calendar**.

Choose **Share feedback** below the news bar to send an idea or problem to the private admin inbox. You must be signed in. Write between 10 and 4,000 characters; the message is submitted with your membership name. If sending fails, the text stays in the open feedback window so you can retry. The app confirms when the team has received it.

## Appearance and alerts

**Settings → Widget** controls comfortable text density, opaque appearance, background opacity, always-on-top, and click-through. Position, width and column choice are remembered. With **Minimize to tray** off, minimize uses the taskbar and close exits subject to the running-work guard.

**Settings → Notifications** controls low-quota, expired-sign-in and recovered-quota alerts, a threshold, and quiet hours. Alerts are deduplicated during a session. Windows notification settings still control whether tray notifications are shown.

Account menus and settings dropdowns use solid dark backgrounds so their text stays readable over the desktop. Arrow keys and Enter select dropdown values; Escape closes the list. Disabled quiet-hour fields keep their saved times visible.

## Updates

When the administrator publishes a newer release, a small colored circle and **Update** button appear near the bottom of the app. Hover for the version and release notes, then click to open its Codex Lover download page. The button opens the published page; it does not install software automatically. If there is no newer published release, the button stays hidden.

Run the downloaded installer. It verifies package hashes and preserves account data and older releases. Newer app versions respond to setup's safe-exit request after checking for active work. If the app cannot exit, finish the operation and follow setup's Retry instructions. For older versions, exit the widget from its tray yourself. Setup never force-closes Codex or the monitor and opens the updated app after a successful interactive installation.

This repository is a source release. LocalTest builds are unsigned developer builds. Existing downloaded installers have their own version and signing status. See ../packaging/README.md before producing or distributing binaries.

For rollback, use `scripts\Rollback.ps1 -PreviousExecutable '<earlier executable>' -VerifyOnly` to check a preserved release without changing anything. After verification, close the widget and omit `-VerifyOnly` to update its shortcut and enabled startup entry and open the earlier release. Rollback validates the release's files and does not delete account data or release folders.

To uninstall, use **Windows Settings → Apps → Installed apps → Codex Lover → Uninstall**. This removes the Start shortcut and Windows startup/Installed Apps entries while retaining saved account data, settings and release history. Uninstalling does not cancel a paid website subscription; manage renewal from the billing dashboard.

From version 1.7.3, the app starts independently of Codex so it can finish a switch after Codex quits. Your saved accounts and membership are kept in `%USERPROFILE%\.codex-lover\appdata\CodexAccountMonitor`; the upgrade preserves the old data folders. If the independent launch cannot be verified, Codex is left open and the app reports the problem.

Test switching with your own accounts after your current Codex tasks have finished. Confirm the account shown by Codex after it reopens.
