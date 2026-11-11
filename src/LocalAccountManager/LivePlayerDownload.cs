using System.Buffers;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

internal static class LivePlayerDownload
{
    private const string VersionUrl = "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer/channel/LIVE";
    private const string Cdn = "https://setup-aws.rbxcdn.com/";
    internal const string ExecutableName = "RobloxPlayerBeta.exe";
    private const string InstallerName = "RobloxPlayerInstaller.exe";
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxExtractedBytes = 4L * 1024 * 1024 * 1024;
    // RDD's WindowsPlayer layout. The bootstrapper itself is a separate manifest entry.
    private static readonly Dictionary<string, string> PackageFolders = new(StringComparer.Ordinal)
    {
        ["RobloxApp.zip"] = "",
        ["redist.zip"] = "",
        ["shaders.zip"] = "shaders",
        ["ssl.zip"] = "ssl",
        ["WebView2.zip"] = "",
        ["WebView2RuntimeInstaller.zip"] = "WebView2RuntimeInstaller",
        ["content-avatar.zip"] = "content/avatar",
        ["content-configs.zip"] = "content/configs",
        ["content-fonts.zip"] = "content/fonts",
        ["content-sky.zip"] = "content/sky",
        ["content-sounds.zip"] = "content/sounds",
        ["content-textures2.zip"] = "content/textures",
        ["content-models.zip"] = "content/models",
        ["content-platform-fonts.zip"] = "PlatformContent/pc/fonts",
        ["content-platform-dictionaries.zip"] = "PlatformContent/pc/shared_compression_dictionaries",
        ["content-terrain.zip"] = "PlatformContent/pc/terrain",
        ["content-textures3.zip"] = "PlatformContent/pc/textures",
        ["extracontent-luapackages.zip"] = "ExtraContent/LuaPackages",
        ["extracontent-translations.zip"] = "ExtraContent/translations",
        ["extracontent-models.zip"] = "ExtraContent/models",
        ["extracontent-textures.zip"] = "ExtraContent/textures",
        ["extracontent-places.zip"] = "ExtraContent/places"
    };

    internal static async Task<string> Install(string folder, IProgress<string>? progress, CancellationToken cancel)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var token = deadline.Token;

        using var release = JsonDocument.Parse(await ReadText(client, VersionUrl, token).ConfigureAwait(false));
        string version = release.RootElement.GetProperty("clientVersionUpload").GetString() ?? "";
        if (!Regex.IsMatch(version, @"\Aversion-[0-9a-f]{16}\z", RegexOptions.CultureInvariant))
            throw new UserError("Roblox returned an invalid LIVE player version.");
        var packages = ParseManifest(await ReadText(client, Cdn + version + "-rbxPkgManifest.txt", token).ConfigureAwait(false));
        string download = Path.Combine(folder, ".package");
        long completedBytes = 0;
        long totalBytes = packages.Sum(package => package.DownloadBytes);
        for (int i = 0; i < packages.Count; i++)
        {
            var package = packages[i];
            string status = $"Installing Roblox LIVE ({i + 1}/{packages.Count})";
            using var response = await client.GetAsync(Cdn + version + "-" + package.Name, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != package.DownloadBytes)
                throw new UserError("A Roblox package did not match its download size.");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(download, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
            {
                long lastReported = -1;
                var copied = await CopyBounded(source, output, package.DownloadBytes, token, bytes =>
                {
                    long percent = (completedBytes + bytes) * 100 / totalBytes;
                    if (percent == lastReported) return;
                    lastReported = percent;
                    progress?.Report($"{status} · {percent}% downloaded");
                }).ConfigureAwait(false);
                if (copied != package.DownloadBytes) throw new UserError("A Roblox package download was incomplete.");
                output.Position = 0;
                // The manifest uses MD5 to check package integrity. WinVerifyTrust verifies the publisher separately.
                string hash = Convert.ToHexString(await MD5.HashDataAsync(output, token).ConfigureAwait(false));
                if (!hash.Equals(package.Digest, StringComparison.OrdinalIgnoreCase))
                    throw new UserError("A Roblox package failed its manifest checksum.");
            }
            await Extract(download, Path.Combine(folder, PackageFolders[package.Name]), package.ExtractedBytes, token).ConfigureAwait(false);
            File.Delete(download);
            completedBytes += package.DownloadBytes;
        }

        string executable = Path.Combine(folder, ExecutableName);
        progress?.Report("Verifying the Roblox publisher signature…");
        if (!File.Exists(executable) || !RobloxPublisher.Verify(executable))
            throw new UserError("Windows could not verify the Roblox Corporation signature on the downloaded player.");
        const string settings = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Settings>
              <ContentFolder>content</ContentFolder>
              <BaseUrl>https://www.roblox.com</BaseUrl>
            </Settings>
            """;
        await File.WriteAllTextAsync(Path.Combine(folder, "AppSettings.xml"), settings, token).ConfigureAwait(false);
        return version;
    }

    private static async Task<string> ReadText(HttpClient client, string url, CancellationToken cancel)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyBounded(stream, buffer, 64 * 1024, cancel).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
    }

    internal sealed record Package(string Name, string Digest, long DownloadBytes, long ExtractedBytes);

    internal static List<Package> ParseManifest(string manifest)
    {
        string[] lines = manifest.Trim().Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length < 5 || lines[0] != "v0" || (lines.Length - 1) % 4 != 0)
            throw new UserError("Roblox returned an unsupported LIVE player package manifest.");
        var packages = new List<Package>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length; i += 4)
        {
            string name = lines[i];
            if (name == InstallerName) continue;
            if (!PackageFolders.ContainsKey(name) || !seen.Add(name) ||
                !Regex.IsMatch(lines[i + 1], @"\A[0-9a-fA-F]{32}\z", RegexOptions.CultureInvariant) ||
                !long.TryParse(lines[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out long packed) || packed is <= 0 or > MaxDownloadBytes ||
                !long.TryParse(lines[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out long expanded) || expanded is <= 0 or > MaxExtractedBytes)
                throw new UserError("Roblox returned an unsupported package in the LIVE player manifest.");
            packages.Add(new Package(name, lines[i + 1], packed, expanded));
        }
        if (!seen.Contains("RobloxApp.zip") || packages.Sum(p => p.DownloadBytes) > MaxDownloadBytes ||
            packages.Sum(p => p.ExtractedBytes) > MaxExtractedBytes)
            throw new UserError("The Roblox LIVE player manifest is incomplete or exceeds the install size limit.");
        return packages;
    }

    internal static async Task Extract(string download, string folder, long size, CancellationToken cancel)
    {
        using var archive = ZipFile.OpenRead(download);
        if (archive.Entries.Count > 80_000 || archive.Entries.Sum(entry => entry.Length) > size)
            throw new UserError("A Roblox archive exceeds its manifest size.");
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (var entry in archive.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            bool directory = name.EndsWith('/');
            // Roblox content archives mark directories with a leading backslash, including the archive root.
            if (directory && entry.Length == 0 && name.StartsWith('/')) name = name[1..];
            if (directory && name.Length == 0) { Directory.CreateDirectory(folder); continue; }
            string[] parts = name.TrimEnd('/').Split('/');
            if (parts.Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(invalidChars) >= 0 ||
                Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                throw new UserError("A Roblox package contained an invalid file path.");
            string destination = Path.Combine(folder, Path.Combine(parts));
            if (directory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var contents = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            if (await CopyBounded(contents, output, entry.Length, cancel).ConfigureAwait(false) != entry.Length)
                throw new UserError("A Roblox package contained an incomplete file.");
        }
    }

    private static async Task<long> CopyBounded(Stream source, Stream destination, long limit, CancellationToken cancel, Action<long>? progress = null)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > limit) throw new UserError("A Roblox download exceeded its expected size.");
                await destination.WriteAsync(buffer.AsMemory(0, count), cancel).ConfigureAwait(false);
                progress?.Invoke(total);
            }
            return total;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
