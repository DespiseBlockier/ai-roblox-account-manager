# Security design and limits

This app reduces the attack surface of the reference workflow. It is not an independent security audit or a guarantee against compromise.

## Credentials

Users can sign in manually or provide username/password pairs and an optional local group to a login queue. Provided passwords and group changes are committed only after matching identity verification. They are encrypted in the portable master-password vault. Saved passwords can be edited or forgotten with Password…. The app does not capture password fields typed in the browser, use keyboard hooks, or enable browser password autosave.

The fixed autofill script only writes the supplied username/password to the known login inputs after both native code and the script verify the exact HTTPS `www.roblox.com/login` origin/path and default port. Values are JSON-encoded to prevent script injection. It does not submit login, solve CAPTCHA, handle one-time codes, or run configurable scripts. DOM fields naturally become visible to Roblox's own page code. Queues are sequential, stop on browser close, and release unsaved password references; managed strings cannot be reliably zeroed. Input dialogs clear their fields and bulk input disables undo history. Clipboard contents are not modified.

The manager reads `.ROBLOSECURITY` from its own selected profile and retains a master-password-encrypted copy in the vault. Cookies are full account credentials and are sent only to exact required Roblox API hosts. Saved cookies restore matching browser sessions, with identity verification before actions. There is no credential clipboard export, plaintext backup, or access to regular-browser cookies. WebView2 uses its normal Windows cookie protection; not every browser cache/local-storage file is encrypted by this manager. All profile storage is inside the private Data folder.

Before a launch ticket is requested, the selected profile is checked using `https://users.roblox.com/v1/users/authenticated`. A different identity blocks the launch. New connections also reject duplicate accounts. The manager cannot bind an entry to another account without removing and re-adding it.

## Data at rest

v1.3 stores account details, passwords, and cookies in `Data/accounts.vault` beside the executable using AES-256-GCM. The `RAMPV003` format uses a random 16-byte salt, PBKDF2-HMAC-SHA256 with 600,000 iterations, a 32-byte derived key, fresh random 12-byte nonce per save, and a 16-byte authentication tag. The format magic is authenticated. Master passwords must be 12–1024 characters. The salt and ciphertext travel with the app; no Windows identity or device secret is used to decrypt this vault. The derived key is held only while the app is unlocked and zeroed on exit/disposal. Managed password strings cannot be reliably zeroed.

The complete Data folder can be copied to another PC and unlocked with the same master password. WebView2 caches are kept per PC/Windows-user fingerprint inside Data because browser protection is machine/user-specific. On a different PC, a fresh profile can restore the selected account's cookie from the portable vault. Roblox may invalidate or challenge the session independently; the saved account/password remains available for reconnecting.

Writes flush a same-directory temporary file, preserve the prior encrypted save, and replace the current file. The prior copy can retain removed credentials until another save. A wrong password, corrupt file, or unsupported format is preserved, never silently reset. Files use the drive's normal filesystem permissions. The app does not protect an unlocked session from same-user malware, administrators, or OS compromise. Browser cache artifacts remain sensitive even when the vault is locked.

Older Windows-DPAPI vaults can be explicitly converted on their original Windows account or imported through the first-run screen. DPAPI is used only to read that legacy data. Building a release never imports or reads it.

Layout backups use a random 16-byte salt, PBKDF2-HMAC-SHA256 with 600,000 iterations, a 32-byte key, random 12-byte nonce, and AES-GCM with a 16-byte tag. Format magic is authenticated as additional data. Passwords must contain at least 12 characters; choose a strong unique passphrase. Key and serialization buffers are cleared where possible. There are no session credentials in these backups. File size, schema version, IDs, collection sizes, and string lengths are checked before data is accepted. Import creates fresh profile identifiers to prevent binding a restored entry to an existing browser session.

Layout exports explicitly construct a credential-free snapshot before encryption; passwords/cookies are also discarded on layout import. Neither pending manual logins nor unprocessed queued credentials are written to the vault. A save failure rolls back the pending account's insertion and credentials.

Grouped imports support semicolon-delimited quoted fields and an explicit legacy format. Error messages include line numbers without echoing passwords. Bulk editing validates every expanded alias/value before mutation, writes once, and restores all selected metadata on failure. Passwords/cookies are not editable through the bulk metadata dialog.

The release builder publishes to a fresh generated staging folder and copies only compiler output plus named public files. It rejects Data, BrowserData, vaults, backups, profiles, and development files, then zips that staging output. Existing local Data is preserved and excluded. Share the generated ZIP; a manually copied used folder intentionally contains your private vault for USB portability.

Session checks run on startup, every five minutes, and on request. Only HTTP 401 is labeled expired; 403 is a verification requirement, and network/timeout/rate-limit failures do not mean invalid credentials. A valid label reflects the last check and cannot guarantee a cookie stays valid afterward. The app does not automatically submit stored passwords when a check fails.

## Network and browser boundaries

- The direct HTTP client only permits HTTPS on exact `users.roblox.com`, `auth.roblox.com`, and `games.roblox.com` hosts with default ports and no URI user information. Redirect following and automatic cookie storage are disabled. Public server requests carry no credentials.
- v1.2 additionally permits exact `groups.roblox.com` for a read-only membership confirmation call. Built-in native code never submits a group join or payout request; the reviewed group example performs its join through the account's browser.
- API errors show sanitized status messages instead of raw response bodies. Responses have bounded sizes. Requests time out and the ticket CSRF retry happens once. No retry loop ignores Roblox challenges or rate limiting.
- Browser top-level navigation only permits HTTPS `roblox.com` and `www.roblox.com`. Roblox pages can load their normal third-party subresources, including verification components. External popups, downloads, browser extensions, permission requests, host-object bridges, and web messages are blocked/disabled. Certificate errors are not bypassed. Normal TLS and Chromium sandbox protections remain enabled.
- An external protocol requested by the browser must be `roblox-player:` from a Roblox origin while the top-level page is on Roblox. Other custom protocols are canceled. Generated launches use validated numeric place IDs and GUID Job IDs; parameters are escaped. Windows' registered Roblox handler performs the launch.
- Game/share links are reconstructed from approved paths and validated fields. Arbitrary URLs or executables cannot be entered as a game target.

Built-in behavior has no HTTP listener, WebSocket listener, remote-control port, telemetry SDK, automatic executable download, app updater, automatic post-login script loading, plugin loader, process injection, or Roblox-file patcher. The optional legacy mutex does not alter existing Roblox processes. Windows' registered protocol handler and Microsoft's installed runtime are trusted dependencies outside this app's control.

## Explicit browser-code runs (v1.2)

The user can run JavaScript on selected signed-in Roblox pages through the code editor. This is a powerful feature: user code has the website session's permissions and can issue web requests under normal browser rules. It can perform account actions or expose page-accessible data if untrusted code is pasted. There is no claim that arbitrary browser code is safe.

The helper receives only username and user ID; it is not passed saved passwords or cookie values. Host objects and web-message bridges remain disabled. The native app polls a per-run page-local status object instead of exposing a native command endpoint. Only the documented done/stop/status phases are accepted. The status object is not a security boundary against page JavaScript; group completion is independently checked through Roblox's native identity and membership API requests.

Code starts only after identity verification, on Roblox HTTPS pages other than login. A new document loses the run state and stops the queue; the app never automatically replays code after navigation. Timeout, Stop, browser close, or script failure disposes the current browser and stops remaining accounts. This cannot undo requests already processed remotely. The default timeout is ten minutes per account, configurable up to two hours. Editor text is kept in memory, cleared when closed, and not automatically persisted or downloaded.

The built-in group example checks the account identity, avoids duplicate joins, retries an ordinary CSRF handshake at most once, stops on rate limits, and waits for actual membership. Pending approval is never marked joined. Verification challenges remain for the user to complete in Roblox. Native confirmation must also succeed before the queue advances when its group-confirmation option is enabled. The example makes no payouts. See [BROWSER_CODE.md](BROWSER_CODE.md).

## Verification scope

Security tests cover target/host validation, injection escaping, encrypted credential persistence, export redaction, pending-login rollback, queue cancellation/order, API status classification, authenticated backup tamper detection, corrupt-data preservation, schema limits, HTTP cookie scoping, CSRF retry, pagination escaping, and sanitized failures. Integration tests exercise real WebView2 with synthetic profiles and credentials, locally intercepted test HTML, and a fake HTTP service. They check autofill, automatic verified-login detection, no placeholder creation, expired cookies, identity mismatch rejection, browser isolation, local sign out, and disposal.

This does not verify the safety of the original project's released binaries or establish that the original author included a backdoor. Live Roblox login/game joining and anti-cheat/client compatibility require user testing. Keep Windows, Roblox, .NET, and WebView2 updated from their official sources.
