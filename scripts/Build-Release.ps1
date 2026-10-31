[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $taskRoot 'src\LocalAccountManager\LocalAccountManager.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\packages'
$sdk = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$stagingParent = [IO.Path]::GetFullPath((Join-Path $taskRoot 'dist\.release-staging'))
$staging = Join-Path $stagingParent ([Guid]::NewGuid().ToString('N'))
$package = Join-Path $staging 'RobloxAccountManager'
$releaseDir = Join-Path $taskRoot 'releases'
$localDir = Join-Path $taskRoot ('dist\v' + $version)
$zip = Join-Path $releaseDir ('RobloxAccountManager-' + $version + '-win-x64.zip')
$bundleProperties = @('-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true')
function Assert-GeneratedPath([string]$path, [string]$parent) {
    $full = [IO.Path]::GetFullPath($path)
    $prefix = [IO.Path]::GetFullPath($parent).TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Generated path left its intended directory.' }
}
function Get-Digest([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}
Assert-GeneratedPath $staging $stagingParent
New-Item -ItemType Directory -Force -Path $package,$releaseDir | Out-Null
try {
    & $sdk restore $project --runtime win-x64 -p:SelfContained=true @bundleProperties --configfile (Join-Path $taskRoot 'NuGet.Config') --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed. Install a .NET 10 SDK and check NuGet connectivity.' }
    & $sdk publish $project --no-restore -c Release --runtime win-x64 --self-contained true @bundleProperties -p:PublishTrimmed=false -p:PublishReferencesDocumentationFiles=false -p:DebugType=None -p:DebugSymbols=false -o $package
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $docs = Join-Path $package 'Docs'
    New-Item -ItemType Directory -Path $docs | Out-Null
    foreach ($name in @('README.md','LICENSE','NOTICE.md')) {
        Copy-Item -LiteralPath (Join-Path $taskRoot $name) -Destination (Join-Path $docs $name)
    }
    New-Item -ItemType Directory -Path (Join-Path $docs 'examples') | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'examples\join-group.js') -Destination (Join-Path $docs 'examples\join-group.js')
    @'
Roblox Account Manager - Portable

Extract this entire folder, then open RobloxAccountManager.exe.
The .NET runtime is included. Microsoft Edge WebView2 Runtime must be installed.
Guides, examples and licenses are in Docs. SHA256SUMS.txt lists the file checksums.
Create or unlock your vault on first launch. A master password is recommended.
No master password (Not recommended) stores passwords and cookies in readable form.
Anyone with an unprotected Data folder can access the saved accounts.
All account data is created in Data beside the EXE. Copy the ENTIRE folder to your USB.
Protected vaults use the same master password on another Windows PC; Roblox may require a fresh login.
Keep your master password safe. The app cannot recover it.
Optional authenticator-app unlock checks, with offline QR setup, are in Vault security.
Save recovery codes separately. Short passwords are accepted but are less secure.
Settings has optional automatic updates, off by default. Enabling it permits automatic restarts.
These local checks do not add independent offline encryption; see Docs/README.md.

This release starts empty. It contains no builder accounts, passwords, cookies or browser profiles.
Share the original ZIP, not your used folder containing Data.
Use Check for updates, then Update & restart to install a verified release.
'@ | Set-Content -LiteralPath (Join-Path $package 'START-HERE.txt') -Encoding UTF8
    $notices = Join-Path $docs 'ThirdPartyNotices'
    New-Item -ItemType Directory -Path $notices | Out-Null
    foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
        $notice = Join-Path (Split-Path -Parent $sdk) $name
        if (Test-Path -LiteralPath $notice) { Copy-Item -LiteralPath $notice -Destination (Join-Path $notices ('DotNet-' + $name)) }
    }
    # The published runtime configuration is bundled into the EXE; read the compiler's copy for vendor notices.
    $runtimeConfigPath = Join-Path (Split-Path -Parent $project) 'bin\Release\net10.0-windows\win-x64\RobloxAccountManager.runtimeconfig.json'
    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $packName = switch ($framework.name) { 'Microsoft.NETCore.App' { 'microsoft.netcore.app.runtime.win-x64' } 'Microsoft.WindowsDesktop.App' { 'microsoft.windowsdesktop.app.runtime.win-x64' } default { $null } }
        if ($packName) {
            foreach ($name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
                $notice = Join-Path (Join-Path (Join-Path $env:NUGET_PACKAGES $packName) $framework.version) $name
                if (Test-Path -LiteralPath $notice) { Copy-Item -LiteralPath $notice -Destination (Join-Path $notices ($packName + '-' + $name)) }
            }
        }
    }
    foreach ($name in @('LICENSE.txt','NOTICE.txt')) {
        $wvLicense = Join-Path $env:NUGET_PACKAGES ('microsoft.web.webview2\1.0.4314.35\' + $name)
        if (Test-Path -LiteralPath $wvLicense) { Copy-Item -LiteralPath $wvLicense -Destination (Join-Path $notices ('WebView2-' + $name)) }
    }
    Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES 'qrcoder\1.8.1\LICENSE.txt') -Destination (Join-Path $notices 'QRCoder-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'third-party\AvalonEdit-LICENSE.txt') -Destination (Join-Path $notices 'AvalonEdit-LICENSE.txt')
    $rootFiles = @(Get-ChildItem -LiteralPath $package -File | ForEach-Object Name)
    $rootFolders = @(Get-ChildItem -LiteralPath $package -Directory | ForEach-Object Name)
    if ($rootFiles.Count -ne 2 -or 'RobloxAccountManager.exe' -notin $rootFiles -or 'START-HERE.txt' -notin $rootFiles -or $rootFolders.Count -ne 1 -or $rootFolders[0] -ne 'Docs') {
        throw 'Unexpected loose files in the portable release. Only the bundled EXE, start guide, and Docs folder are allowed before checksums.'
    }
    $files = @(Get-ChildItem -LiteralPath $package -File -Recurse)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($package.Length + 1)
        if ($relative -match '(^|[\\/])(Data|BrowserData|test-results|\.tools|\.git|\.updates)([\\/]|$)' -or $file.Name -match '(?i)\.(vault|previous|ramlayout|backup|env|pdb|dpapi|pfx|key)$') {
            throw 'Private or development data appeared in compiler output. Release refused.'
        }
    }
    $hashes = $files | Sort-Object FullName | ForEach-Object { '{0}  {1}' -f (Get-Digest $_.FullName),$_.FullName.Substring($package.Length + 1) }
    $hashes | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $tempZip = Join-Path $releaseDir ([Guid]::NewGuid().ToString('N') + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($package,$tempZip,[IO.Compression.CompressionLevel]::Optimal,$true)
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    [IO.File]::Move($tempZip,$zip)
    # Preserve any existing local portable Data folder. Copy only fresh package files.
    foreach ($file in (Get-ChildItem -LiteralPath $package -File -Recurse)) {
        $destination = Join-Path $localDir $file.FullName.Substring($package.Length + 1)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    ((Get-Digest $zip) + '  ' + (Split-Path -Leaf $zip)) | Set-Content -LiteralPath ($zip + '.sha256') -Encoding UTF8
    $signingKey = Join-Path $taskRoot '.tools\signing\publisher.dpapi'
    if (Test-Path -LiteralPath $signingKey) {
        & $sdk run --project (Join-Path $taskRoot 'scripts\ReleaseSigner\ReleaseSigner.csproj') -c Release -- sign $signingKey $zip (Join-Path $releaseDir 'update-manifest.json') (Join-Path $releaseDir 'update-manifest.sig') $version
        if ($LASTEXITCODE -ne 0) { throw 'Release manifest signing failed.' }
    }
    else { Write-Output 'Local build is unsigned. The official updater accepts only publisher-signed releases.' }
    Write-Output "Shareable ZIP: $zip"
    Write-Output 'Account data was excluded. Open Start.cmd to launch the local portable app.'
}
finally {
    Assert-GeneratedPath $staging $stagingParent
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
