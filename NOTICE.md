# Copyright, licensing, and attribution

AI Roblox Account Manager is an unofficial third-party Windows application. It is not affiliated with, endorsed by, or sponsored by Roblox Corporation or the original Roblox Account Manager author. Roblox and associated marks belong to their respective owners. The app uses its own account-card icon, not the Roblox logo.

## Application license

This distribution is provided under **GNU GPL version 3 only (GPL-3.0-only)**; see LICENSE. Copyright (c) 2026 AI Roblox Account Manager contributors.

The application was written for this project after examining the workflow and security-sensitive features of [ic3w0lf22/Roblox-Account-Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager), reference commit `c2b13d146d5db609d499870145e8d2dff2506581`. That reference project is GPL-3.0 licensed and identifies its author as ic3w0lf/ic3, including the notice "Copyright © ic3 2023" in its assembly metadata. Its authors retain their rights. No original release binaries or original branding assets are redistributed here.

GPL-3.0 was chosen for this distribution to align with the reference project's license rather than rely on a permissive-license characterization of source-informed work. This notice does not claim authorship of any upstream material. The reference inspection focused on account/session, launch, storage, updater, network-control, browser, and main UI workflows; it was not a forensic audit of all released binaries.

The matching application source, build scripts, and original icon assets are available at the release tag and in the source ZIP attached beside the binary release. Recipients of the application must also receive the applicable notices and access to corresponding source under the GPL. Private hosting does not waive license obligations or guarantee protection against copyright or trademark claims.

Ban monitoring uses a new local implementation of Roblox's moderation endpoint after reviewing [Pixeluted/RobloxBanMonitor](https://github.com/Pixeluted/RobloxBanMonitor), reference commit `9e31d676b5d9c71c655fcd37e886136611a7ec04`. No source code or assets from that repository are redistributed.

## Third-party components

- **Microsoft .NET / WPF:** distributed under their respective Microsoft/open-source notices. The self-contained release includes the supplied runtime licenses and third-party notices under `Docs/ThirdPartyNotices`.
- **Microsoft.Web.WebView2 SDK:** distributed under Microsoft's included BSD-style license and notices, retained under `Docs/ThirdPartyNotices`. The installed WebView2 browser runtime is a separate Microsoft component with its own terms.
- **QRCoder 1.8.1:** offline QR generation, MIT license. Copyright (c) 2013–2025 Raffael Herrmann and 2024–2025 Shane Krueger. The supplied license is retained as `Docs/ThirdPartyNotices/QRCoder-LICENSE.txt`.
- **Pillow:** optional development tool for regenerating the project's original icon; it is not bundled into the application.

The application GPL license does not replace third-party component licenses or grant rights to Roblox trademarks. Source, attribution, and license review reduce avoidable problems but do not constitute a legal clearance or a promise that no takedown request can be made.
