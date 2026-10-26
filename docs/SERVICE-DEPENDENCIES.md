# Service dependencies and privacy

## What is open source

The WPF desktop client, Core library, tests and build tooling in this repository
are MIT-licensed. codexlover.com, its membership/billing backend and operational
data are a separate hosted service and are not part of this source release.

## What can run locally

Build, unit/integration tests and --demo run without a membership or real Codex
credentials. The tests use synthetic files and a fake local App Server.

Normal startup currently validates an online website membership. The shipped
client uses the fixed HTTPS codexlover.com origin for membership, news, feedback
and update metadata. Hosted Free membership supports two saved accounts; paid
service terms are published on the website and may change independently of the
MIT source license. A source build is not an offline membership entitlement.

Real account monitoring also needs official Codex software and a supported
ChatGPT account. Codex service limits and terms apply to each account. Switching
does not transfer a running task or pool allowances.

## Data boundaries

Website identity and membership are separate from Codex identity. The desktop
does not send enrolled Codex tokens, account email addresses or quota snapshots
to the website. Feedback intentionally sends the text you submit. Opening a
website link creates an ordinary browser request to that website.

Codex-managed credentials remain in ACL-protected local profile directories.
Membership sessions and switch recovery records use Windows CurrentUser DPAPI.
The Codex auth file itself follows Codex's storage format. Same-user malware and
administrators are outside this protection boundary.

For endpoint contracts see [MEMBERSHIP-INTEGRATION.md](MEMBERSHIP-INTEGRATION.md).
For threat assumptions and reporting see [SECURITY.md](../SECURITY.md).

## Known limits

The public source snapshot has automated regression coverage, not a fresh
end-to-end sign-in or account-switch certification against every current Codex
build. Protocols, desktop activation and service behavior can change. Report
versions and sanitized symptoms; never submit a real credential directory.
