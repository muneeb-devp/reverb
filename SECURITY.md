# Security

## Reporting a problem

Please don't report security problems in public issues. Use GitHub's private reporting instead: on the repository's **Security** tab, choose **Report a vulnerability**. Only the maintainer can see the report.

Include what you found, how to reproduce it, and what an attacker could do with it. I'll reply within a week, keep you posted while it's being fixed, and credit you in the release notes unless you'd rather not be named.

## Supported versions

Fixes go into the latest release of `Reverb.Notebooks`, `Reverb.Runtime` and the VS Code extension. Older versions don't get patches, so please update before reporting.

## What Reverb is meant to protect

Notebooks run arbitrary code, so opening a notebook from someone you don't trust is like running their program. Reverb doesn't try to sandbox that. It does try to make sure nobody else can run code through you:

- The browser editor listens on 127.0.0.1 only. The address it opens contains a random token, and connections without it are refused.
- WebSocket connections from other websites are refused (Origin check), and so are requests whose Host isn't a loopback name (DNS rebinding).
- `reverb run --host` serves beyond localhost only when you ask for it, and prints a warning when it does.
- `reverb serve --stdio`, used by the VS Code extension, talks over stdin and stdout and opens no network port.
- In a VS Code workspace you haven't trusted, the extension starts no kernel, restores no packages and offers no language services.
- Reverb sends no telemetry.

A way around any of these is a security problem, and I'd like to hear about it.
