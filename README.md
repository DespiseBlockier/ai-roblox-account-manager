# AI Roblox Account Manager

A native Windows account manager with the familiar account-list and join-server workflow of [ic3w0lf22's Roblox Account Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager). This is a fresh implementation, not a repackaged upstream executable. No original release binaries or bundled assets are used.

Unofficial third-party software, unaffiliated with Roblox Corporation or the reference project's author. This distribution uses **GPL-3.0-only** and includes corresponding source with releases. See [NOTICE.md](NOTICE.md) for attribution and third-party licensing, and [LICENSE](LICENSE) for the license terms. The app icon is original artwork for this project.

## Start

Close any older manager window, then double-click **Start.cmd**, or open **dist/v1.3.1/RobloxAccountManager.exe**. For the shareable build, extract `releases/RobloxAccountManager-1.3.1-win-x64.zip` and open `RobloxAccountManager.exe` inside its `RobloxAccountManager` folder. Keep the entire folder together.

On first launch, create a **master password** of at least 12 characters. Your accounts, saved passwords, cookies, groups, and game data are encrypted in `Data/accounts.vault` beside the executable. Every launch unlocks this file with the master password. Keep that password safe; the app cannot recover it.

To use a USB or another PC, close the manager, copy the **whole used application folder including Data**, and unlock it on the other PC with the same master password. Browser caches stay under Data in separate per-PC/per-Windows-user directories. The portable vault restores saved cookies into the matching account's new browser profile; Roblox may still require a fresh login or verification on another PC. All account entries and saved passwords remain in the vault.

The first-run screen can import your older Windows-bound manager vault from this PC. This happens only when you create/unlock the app, never while building the release. The original old installation is preserved. If directly opening an old vault through `--data-dir`, convert it on the original PC using the conversion screen. Layout backups still exclude credentials; copying your private Data folder carries the complete vault.

The release targets **Windows x64** and includes the .NET runtime. [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) must be installed on the PC. The manager runs as a normal user and needs a writable application folder.

1. Click **Add account ▾** and choose **Browser login**, **Username and password**, or **Bulk username;password;group**.
2. For a bulk queue, enter `username;password;group` per line. The group is optional and is a **local account-manager group**, not a Roblox community. Missing/blank groups preserve an existing account's group; new accounts use Default. For example: `account_one;password_here;Farm`. Quote passwords containing semicolons: `account_one;"pass;word";Farm`. Within quoted fields, `""` represents a quote. Select the legacy format to treat everything after the first separator as the password. Single-account import has a separate optional group field. The batch limit is 500 unique usernames.
3. Each queued account opens the actual `https://www.roblox.com/login` page. Provided credentials are filled automatically. Click **Log In** and complete Roblox verification yourself. A verified session is detected automatically and the next queued account opens. **Connect account** remains available as a manual retry.
4. A new account row and its encrypted credentials are saved only after Roblox confirms the intended username. Closing the sign-in browser stops the queue and discards remaining unsaved logins. Successfully connected accounts stay saved. Repeated Add clicks resume the current login instead of adding placeholders. Old default placeholders from v1.0 are removed on startup.
5. Select an account, enter a place ID or game link, and click **Join server**. Roblox must already be installed. A launch request is not proof that the player successfully joined. Each account has its own browser profile, separate from your regular browser.

The bulk entry box displays the pasted passwords while you edit it and clears when the queue starts or the dialog closes. It has no undo history. No import list is written to disk.

**Session validity:** saved cookies are checked on startup, every five minutes, and with **Check session**. Rows distinguish valid sessions, expired cookies, verification requirements, and transient check failures. Hover the session text for its last-check time. Network errors and rate limits do not prove that a cookie has expired. Checks pause while adding accounts and stop a batch if Roblox rate limits it.

**Reconnect:** select an expired account and click **Sign in · fill saved login**. The browser fills its username and saved password, and the refreshed cookie is stored after a verified login. **Password…** lets you edit or forget a locally saved password without changing it on Roblox. Browser-only login cannot save a password that was never provided to the manager; use Password… to add one afterward. Browsers never capture manually typed password fields.

**Bulk edit:** select accounts with Ctrl/Shift and click **Bulk edit…** (or Edit with multiple accounts selected). Check the fields to change: group, alias, notes, and favorite. Preview the results before applying. Alias templates accept `{username}`, `{displayName}`, and `{index}`. Notes can be replaced or appended. Unchecked fields and credentials stay unchanged; a failed save rolls the whole edit back. Use the existing Save target action to set a game target for multiple selected accounts.

**Browser code (v1.2):** select accounts, click **Open browser with code…**, and enter JavaScript or generate the join-group example using your community ID. `ram.done()` closes the current browser and advances; `ram.stop()` stops the queue. The generated example waits for actual membership, and the manager independently confirms it before advancing. See [BROWSER_CODE.md](BROWSER_CODE.md) for the helper commands, group workflow, and code permissions. Run only code you trust: custom JavaScript has the selected website session's permissions.

## Features

- Multiple account entries, aliases, notes, editable groups, search, starred accounts, and manual ordering.
- Separate persistent browser sessions, verified against the selected Roblox user ID before every managed launch.
- Place IDs, game URLs, optional public-server Job IDs, and per-account saved game targets.
- Public server browser with pagination, player counts, server-reported ping/FPS, and smallest-first sorting.
- Game favorites and the last 30 requested game targets.
- Multi-selection for session checks, launch requests, group-independent edits to favorites, saved targets, local sign out, and removal. Select rows using Ctrl/Shift.
- Portable AES-256-GCM master-password encryption for account details, explicitly provided passwords, and Roblox session cookies. A flushed temporary file replaces the vault while retaining the previous encrypted save.
- Password-encrypted layout export/import using PBKDF2-SHA256 and AES-256-GCM. Imported accounts get fresh profile IDs and must sign in again. These backups **do not contain login sessions**.
- Optional, off-by-default legacy multi-instance mutex. Enable with Roblox closed. Modern Roblox clients may ignore this method or limit simultaneous clients. It does not modify Roblox files, inject code, close other processes, or bypass client protections.

Private server links (including `/share?code=...&type=Server`) open in the selected account's browser. Click **Play** on Roblox there. Private-server access permissions still apply. Multiple accounts launch sequentially; Roblox may replace an existing client when multi-instance is unavailable.

## What differs from the reference

The interface follows the same workflow with a new dark layout. This is not an exact feature-for-feature clone. It omits the updater, network control API, WebSocket account control, password-field capture, cookie/ticket clipboard export, CAPTCHA services, plugin loading, client patching, and automated changes to account credentials. Login autofill only writes explicitly supplied credentials to Roblox's official login page. Browser code runs only when explicitly started in its editor; it is never downloaded or run automatically. See [SOURCE_REVIEW.md](SOURCE_REVIEW.md) and [SECURITY.md](SECURITY.md).

## Local data

All new manager data is under **Data beside the executable**:

- `accounts.vault`: master-password-encrypted account details, saved passwords, session cookies, groups, favorites, recent targets, and saved links.
- `accounts.vault.previous`: the previous encrypted save. Removed account details or credentials can remain in this encrypted previous copy until another successful save.
- `BrowserData/<device key>`: WebView2's browser storage for each PC/Windows user, including browser-protected cookies. Treat the entire Data folder as private.

**Sign out** clears the selected browser profile and its saved cookie; the saved password is kept for reconnecting. **Remove** clears that profile and removes its entry and credentials from the current vault. Neither revokes sessions on other devices or stops a Roblox player already running. To revoke sessions, use Roblox's own security settings. A canceled new login's temporary profile is cleared; old v1.0 orphan profiles are not automatically deleted during placeholder migration.

The app does not read your regular browser, the original third-party manager's account files, or Roblox's files. Its own older local vault can be imported through the first-run option. Master-password encryption protects the stored vault; malware on an unlocked PC can potentially read process memory. Do not put recovery codes or cookies in notes. Do not share your used Data folder.

Startup and periodic session checks contact Roblox's identity API using saved cookies. Other requests happen when opening browsers, signing in, launching, or browsing servers. The embedded browser loads Roblox's normal resources and may make Microsoft runtime traffic. No app telemetry is implemented.

## Build and verify

Run **Build.cmd** to create a self-contained Windows x64 app in `dist/v1.3.1` and a shareable ZIP in **releases/RobloxAccountManager-1.3.1-win-x64.zip**. The EXE bundles the app and .NET runtime and contains the custom app icon. The extracted folder contains only the EXE, `START-HERE.txt`, `SHA256SUMS.txt`, and `Docs` (guides, examples, and licenses). `Data` is created beside the EXE when you use the app. Native runtime libraries use the standard per-user .NET temporary cache; your account data stays in the portable Data folder. The script uses `.tools/dotnet` if present, otherwise a .NET 10 SDK on PATH. NuGet is restricted to nuget.org and the direct dependency is locked. Rebuild releases to update the bundled .NET runtime; WebView2 updates independently through Microsoft.

**The build does not copy your accounts.** It publishes into a fresh GUID staging directory, copies only named public docs/assets, rejects vault/profile/development files, and archives that fresh output. It never reads the active vault or old local account storage. Existing `dist/v1.3.1/Data` is preserved when app files are updated, and is never included in the ZIP. Share the generated ZIP, not a used application folder. `tests/PackageChecks.py` verifies the ZIP's checksums, runtime, icon resources, and absence of private data.

Run **Check.cmd** for the security and WPF/WebView2 integration checks in a normal Windows session. Tests use synthetic passwords/cookies, a fake HTTP handler, and locally intercepted login-page HTML. They never sign in to a real Roblox account. Test windows can appear briefly; fake test data stays under `test-results`.

Verification passed **148 security checks and 41 UI/WebView2 checks**, plus the archive checks (including the single-file bundle and clean folder layout), covering grouped import, quoted passwords, bulk preview/rollback, portable-vault copying and unlock, legacy conversion, export redaction, first-run password confirmation, the embedded icon, and existing login/browser/code behavior. Tests use fake credentials and intercepted responses; live Roblox login, group joining, game joining, and multi-instance behavior still need live account testing. Native API failures are sanitized; script messages are shown locally without log files.

For an isolated preview, run `dist\v1.3.1\RobloxAccountManager.exe --data-dir D:\path\to\empty-preview-folder`. Normal startup needs no arguments. Only one manager instance may use a given data directory.

To upgrade a used portable folder, close the app and replace the EXE, text files, and Docs with those from the new ZIP, keeping your existing Data folder. Do not share that used folder.

The application source is in `src/LocalAccountManager`; tests are in `tests`. Build output and the development SDK are excluded from source control. No publishing, installer, scheduled task, startup registration, antivirus exclusion, or automatic app update is performed.
