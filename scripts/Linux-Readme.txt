AI Roblox Account Manager for Linux x86-64

Extract this archive and keep its folders together. Open a terminal in the
RobloxAccountManager folder and run:

    ./RobloxAccountManager

The .NET runtime is included. The app uses X11 or XWayland, plus normal
desktop libraries including X11, ICE/SM, fontconfig, ICU, OpenSSL 3 and zlib.
On Arch Linux with OpenSSL 4, install the openssl-3 compatibility package:

    sudo pacman -S openssl-3

Install your distribution's Chromium package to use browser sign-in and
account browsing. An alternative installed Chromium executable can be set
with the RAM_CHROMIUM_PATH environment variable.

The portable vault is Data/accounts.vault. To move a protected Windows
portable vault, close both apps and copy accounts.vault into the Linux
Data folder. The same password and authenticator unlock it. Older
Windows-bound vaults must be converted on their original PC first. The
--data-dir option points the launcher at an alternative data folder.

This preview includes account import, saved logins, notes and bulk edits,
session and moderation checks, age groups, Robux, batch game access checks,
game bookmarks and browsing. Complete Roblox verification in Chromium
when prompted. The Windows player launcher, browser scripting, Gmail
automation and in-app updater are Windows-only in this version.

To update this preview, extract the new archive to a different folder and
move the vault after closing both versions.

Source and release files:
https://github.com/DespiseBlockier/ai-roblox-account-manager

See Docs for the license and third-party notices.
