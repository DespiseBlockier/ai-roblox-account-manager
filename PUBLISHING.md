# Publishing the source

Use the contents of the clean source ZIP as the repository root. The surrounding `apps` workspace contains unrelated projects and should not be uploaded as this application's repository.

The public project consists of the source, tests, build scripts, example JavaScript, app icon, documentation, and dependency lockfile. Test usernames, passwords, and cookies are synthetic fixtures, not real accounts.

Never commit a used application folder's `Data`, browser profiles, vaults, credential exports, logs, backups, build outputs, or development caches. `.gitignore` excludes these files; it does not protect a private file that you manually force-add. Check the staged file list before publishing.

`Build.cmd` recreates the application and its clean binary release. Account data is created only when the app is used and is excluded from release packaging. A new source checkout requires a .NET 10 SDK to build; Microsoft's development SDK is not included in the source ZIP.

This distribution is GPL-3.0-only. Keep LICENSE, NOTICE.md, supplied third-party notices, and matching application source available to recipients alongside binary releases. See NOTICE.md for attribution and the limits of the licensing review.
