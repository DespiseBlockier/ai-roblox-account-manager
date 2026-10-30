# AI Roblox Account Manager

A native Windows account manager with the familiar account-list and join-server workflow of [ic3w0lf22's Roblox Account Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager). This is a fresh implementation, not a repackaged upstream executable. No original release binaries or bundled assets are used.

Unofficial third-party software, unaffiliated with Roblox Corporation or the reference project's author. This distribution uses **GPL-3.0-only** and includes corresponding source with releases. See [NOTICE.md](NOTICE.md) for attribution and third-party licensing, and [LICENSE](LICENSE) for the license terms. The app icon is original artwork for this project.

## Start

Close any older manager window, then double-click **Start.cmd**, or open **dist/v1.4.1/RobloxAccountManager.exe**. For the shareable build, extract `releases/RobloxAccountManager-1.4.1-win-x64.zip` and open `RobloxAccountManager.exe` inside its `RobloxAccountManager` folder. Keep the entire folder together.

On first launch, create a **master password** of at least 12 characters (recommended). With password protection, your accounts, saved passwords, cookies, groups, and game data are encrypted in `Data/accounts.vault` beside the executable. Protected vaults require that password each launch. Keep it safe; the app cannot recover it.

You can instead select **No master password (Not recommended)** and acknowledge its warning. This stores saved passwords, cookies, and account data in readable form and opens the app without asking for a password. Anyone who obtains the folder can access those accounts. This option is off by default. **Vault security…** changes the setting later; removing protection requires the current master password. Enabling protection encrypts both the current vault and its known previous-save file. This cannot securely erase plaintext that already existed on the drive or in other copies.

To use a USB or another PC, close the manager, copy the **whole used application folder including Data**, and open it on the other PC. Protected vaults use the same master password; no-password vaults open automatically. Browser caches stay under Data in separate per-PC/per-Windows-user directories. The portable vault restores saved cookies into the matching account's new browser profile; Roblox may still require a fresh login or verification on another PC. All account entries and saved passwords remain in the vault.

The first-run screen can import your older Windows-bound manager vault from this PC. This happens only when you create/unlock the app, never while building the release. The original old installation is preserved. If directly opening an old vault through `--data-dir`, convert it on the original PC using the conversion screen. Layout backups still exclude credentials; copying your private Data folder carries the complete vault.

The release targets **Windows x64** and includes the .NET runtime. [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) must be installed on the PC. The manager runs as a normal user and needs a writable application folder.

1. Click **Add account ▾** and choose **Browser login**, **Username and password**, or **Bulk username;password;group**.
2. For a bulk queue, enter `username;password;group` per line. The group is optional and is a **local account-manager group**, not a Roblox community. Missing/blank groups preserve an existing account's group; new accounts use Default. For example: `account_one;password_here;Farm`. Quote passwords containing semicolons: `account_one;"pass;word";Farm`. Within quoted fields, `""` represents a quote. Select the legacy format to treat everything after the first separator as the password. Single-account import has a separate optional group field. The batch limit is 500 unique usernames.
3. Each queued account opens the actual `https://www.roblox.com/login` page. Provided credentials are filled automatically. Click **Log In** and complete Roblox verification yourself. A verified session is detected automatically and the next queued account opens. **Connect account** remains available as a manual retry.
4. A new account row and its credentials are saved only after Roblox confirms the intended username. Closing the sign-in browser stops the queue and discards remaining unsaved logins. Successfully connected accounts stay saved. Repeated Add clicks resume the current login instead of adding placeholders. Old default placeholders from v1.0 are removed on startup.
5. Select an account, enter a place ID or game link, and click **Join server**. Roblox must already be installed. A launch request is not proof that the player successfully joined. Each account has its own browser profile, separate from your regular browser.

The bulk entry box displays the pasted passwords while you edit it and clears when the queue starts or the dialog closes. It has no undo history. No import list is written to disk.

**Session validity:** saved cookies are checked on startup, every five minutes, and with **Check session**. Rows distinguish valid sessions, expired cookies, verification requirements, and transient check failures. Hover the session text for its last-check time. Network errors and rate limits do not prove that a cookie has expired. Checks pause while adding accounts and stop a batch if Roblox rate limits it.

**Reconnect:** select an expired account and click **Sign in · fill saved login**. The browser fills its username and saved password, and the refreshed cookie is stored after a verified login. **Password…** lets you edit or forget a locally saved password without changing it on Roblox. Browser-only login cannot save a password that was never provided to the manager; use Password… to add one afterward. Browsers never capture manually typed password fields.

**Bulk edit:** select accounts with Ctrl/Shift and click **Bulk edit…** (or Edit with multiple accounts selected). Check the fields to change: group, alias, notes, and favorite. Preview the results before applying. Alias templates accept `{username}`, `{displayName}`, and `{index}`. Notes can be replaced or appended. Unchecked fields and credentials stay unchanged; a failed save rolls the whole edit back. Use the existing Save target action to set a game target for multiple selected accounts.

**Browser code (v1.2):** select accounts, click **Open browser with code…**, and enter JavaScript or generate the join-group example using your community ID. `ram.done()` closes the current browser and advances; `ram.stop()` stops the queue. The generated example waits for actual membership, and the manager independently confirms it before advancing. `await ram.done()` completes an account; `ram.stop()` stops the queue. The generated example handles ordinary CSRF challenges and waits for actual membership. Pending approval and verification may still require you to act in Roblox. Run only code you trust: custom JavaScript has the selected website session's permissions.

## Features

- Multiple account entries, aliases, notes, editable groups, search, starred accounts, and manual ordering.
- Separate persistent browser sessions, verified against the selected Roblox user ID before every managed launch.
- Place IDs, game URLs, optional public-server Job IDs, and per-account saved game targets.
- Public server browser with pagination, player counts, server-reported ping/FPS, and smallest-first sorting.
- Game favorites and the last 30 requested game targets.
- Multi-selection for session checks, launch requests, group-independent edits to favorites, saved targets, local sign out, and removal. Select rows using Ctrl/Shift.
- Optional portable AES-256-GCM master-password encryption for account details, explicitly provided passwords, and Roblox session cookies. No-password mode is clearly marked Not recommended. A flushed temporary file replaces the vault while retaining the previous save under its active protection mode.
- Password-encrypted layout export/import using PBKDF2-SHA256 and AES-256-GCM. Imported accounts get fresh profile IDs and must sign in again. These backups **do not contain login sessions**.
- Optional, off-by-default legacy multi-instance mutex. Enable with Roblox closed. Modern Roblox clients may ignore this method or limit simultaneous clients. It does not modify Roblox files, inject code, close other processes, or bypass client protections.

Private server links (including `/share?code=...&type=Server`) open in the selected account's browser. Click **Play** on Roblox there. Private-server access permissions still apply. Multiple accounts launch sequentially; Roblox may replace an existing client when multi-instance is unavailable.

## What differs from the reference

The interface follows the same workflow with a new dark layout. This is not an exact feature-for-feature clone. It replaces the updater with a publisher-verified, user-started update flow and omits the network control API, WebSocket account control, password-field capture, cookie/ticket clipboard export, CAPTCHA services, plugin loading, client patching, and automated changes to account credentials. Login autofill only writes explicitly supplied credentials to Roblox's official login page. Browser code runs only when explicitly started in its editor; it is never downloaded or run automatically. The reference source review focused on account/session, storage, browser, updater, and network-control workflows; it was not a forensic audit of all original release binaries.

## Local data

All new manager data is under **Data beside the executable**:

- `accounts.vault`: account details, saved passwords, session cookies, groups, favorites, recent targets, and saved links. Encrypted with a master password by default; readable when No master password is enabled.
- `accounts.vault.previous`: the previous save, using the protection mode that was active when it was saved. Removed account details or credentials can remain in this previous copy until another successful save.
- `BrowserData/<device key>`: WebView2's browser storage for each PC/Windows user, including browser-protected cookies. Treat the entire Data folder as private.

**Sign out** clears the selected browser profile and its saved cookie; the saved password is kept for reconnecting. **Remove** clears that profile and removes its entry and credentials from the current vault. Neither revokes sessions on other devices or stops a Roblox player already running. To revoke sessions, use Roblox's own security settings. A canceled new login's temporary profile is cleared; old v1.0 orphan profiles are not automatically deleted during placeholder migration.

The app does not read your regular browser, the original third-party manager's account files, or Roblox's files. Its own older local vault can be imported through the first-run option. Master-password encryption protects the stored vault; malware on an unlocked PC can potentially read process memory. Do not put recovery codes or cookies in notes. Do not share your used Data folder.

Startup and periodic session checks contact Roblox's identity API using saved cookies. Other requests happen when opening browsers, signing in, launching, or browsing servers. The embedded browser loads Roblox's normal resources and may make Microsoft runtime traffic. No app telemetry is implemented.

## Updates

The app checks this repository's latest stable GitHub release at startup. **Check for updates** retries manually. When a newer verified release is available, **Update & restart** downloads, verifies, installs, and restarts the app. No download or installation starts merely because a new version is detected.

The updater verifies an ECDSA P-256 publisher signature using the public key embedded in the app, then checks the signed ZIP size, SHA-256, archive paths, and individual file checksums. It accepts only this project's release files and approved GitHub download hosts. An invalid signature, damaged download, or unexpected file stops installation. There are no GitHub login tokens or Roblox credentials in update requests.

Only application files and Docs are replaced. Data is preserved, and interrupted installation attempts roll back replaced app files where possible. Recovery copies are kept under `.updates`; keep that folder if an update reports a recovery failure. Updating requires a writable ordinary application folder, without symbolic links or junctions. The app may briefly close during installation and restart on the same vault. Offline or rate-limited checks leave the app usable.

Versions before 1.4.0 need one manual download to get this updater. The app is unofficial and is not an independently audited security product.

## Build from source

Run **Build.cmd** with a .NET 10 SDK to create `dist/v1.4.1` and `releases/RobloxAccountManager-1.4.1-win-x64.zip`. The EXE bundles the app and .NET runtime and contains the original icon. WebView2 Runtime is installed separately. The SDK under `.tools/dotnet` is used if present; NuGet uses nuget.org and the dependency lockfile.

The extracted app contains only the EXE, `START-HERE.txt`, `SHA256SUMS.txt`, and `Docs` (this guide, licenses, and the browser-code example). Native runtime components use the standard per-user .NET temporary cache. Your account data stays in Data beside the EXE.

**The build never copies accounts.** It publishes into a fresh staging directory and archives only compiler output and named public files. Local Data, profiles, vaults, backups, tools, update staging, tests, and development notes are ignored by Git and excluded from release packaging. Share the generated ZIP, never a used app folder.

The optional `scripts/ReleaseSigner` tool signs official release manifests using a publisher private key stored locally under `.tools/signing/publisher.dpapi`, protected by the publishing Windows account. The private key is never included in source or releases. A checkout without that key builds an unsigned local ZIP; it cannot publish an update trusted by official clients. The app's public verification key is included in the source.

To upgrade a used portable folder manually, close the app and replace the EXE, text files, and Docs from the new ZIP, keeping your existing private Data folder. For an isolated preview, launch the EXE with `--data-dir` and a separate empty test directory.

Release checks exercise storage migration, warning acknowledgments, browser isolation, signature/hash validation, unsafe-archive rejection, and update rollback using synthetic data. Live Roblox login, group joining, game joining, and multi-instance behavior still require live account testing.
