using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);
public sealed record SignedUpdate(UpdateManifest Manifest, byte[] Bytes, byte[] Signature, string ReleaseNotes = "");

public static class UpdateTrust
{
    public const string Repository = "DespiseBlockier/ai-roblox-account-manager";
    public const long MaxDownload = 512L * 1024 * 1024;
    public static string CurrentVersion => typeof(UpdateTrust).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    public static byte[] PublicKey
    {
        get
        {
            using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("LocalAccountManager.UpdatePublicKey") ?? throw new UserError("This build has no trusted update key.");
            using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
        }
    }
    public static Version ParseVersion(string value)
    {
        if (value is null || value.Length > 32 || !Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant) || !Version.TryParse(value, out var version))
            throw new UserError("The update version is invalid.");
        return version;
    }
    public static SignedUpdate Verify(byte[] bytes, byte[] signature, string expectedVersion, byte[]? publicKey = null)
    {
        if (bytes.Length is < 1 or > 4096 || signature.Length != 64) throw new UserError("The update signature is missing or invalid.");
        using var signer = ECDsa.Create(); signer.ImportSubjectPublicKeyInfo(publicKey ?? PublicKey, out _);
        if (!signer.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new UserError("Publisher signature verification failed. No update was installed.");
        UpdateManifest manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes) ?? throw new JsonException(); }
        catch (JsonException) { throw new UserError("The signed update information is invalid."); }
        ParseVersion(manifest.Version);
        if (manifest.Version != expectedVersion || manifest.FileName != $"RobloxAccountManager-{manifest.Version}-win-x64.zip" || manifest.Size is < 1 or > MaxDownload || manifest.Sha256 is null || !Regex.IsMatch(manifest.Sha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new UserError("The signed update does not match this release.");
        return new(manifest, bytes, signature);
    }
    public static Uri AssetUrl(string version, string file)
    {
        ParseVersion(version);
        if (file is not ("update-manifest.json" or "update-manifest.sig") && file != $"RobloxAccountManager-{version}-win-x64.zip") throw new UserError("Invalid update asset.");
        return new Uri($"https://github.com/{Repository}/releases/download/v{version}/{file}");
    }
    public static bool AllowedUrl(Uri uri, bool redirect = false) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (redirect ? uri.IdnHost is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" :
         uri.IdnHost == "api.github.com" && uri.AbsolutePath == $"/repos/{Repository}/releases/latest" && uri.Query.Length == 0 ||
         uri.IdnHost == "github.com" && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/v", StringComparison.Ordinal) && uri.Query.Length == 0);
}

public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient http;
    private readonly byte[]? publicKey;
    public UpdateClient(HttpMessageHandler? handler = null, byte[]? publicKey = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AIRobloxAccountManager/" + UpdateTrust.CurrentVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        this.publicKey = publicKey;
    }
    public async Task<SignedUpdate?> CheckAsync(string currentVersion, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        byte[] metadata = await Bytes(new Uri($"https://api.github.com/repos/{UpdateTrust.Repository}/releases/latest"), 512 * 1024, timeout.Token);
        using var json = JsonDocument.Parse(metadata);
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v')) throw new UserError("The release tag is invalid.");
        string version = tag[1..];
        if (UpdateTrust.ParseVersion(version) <= UpdateTrust.ParseVersion(currentVersion)) return null;
        byte[] bytes = await Bytes(UpdateTrust.AssetUrl(version, "update-manifest.json"), 4096, timeout.Token);
        byte[] signature = await Bytes(UpdateTrust.AssetUrl(version, "update-manifest.sig"), 64, timeout.Token);
        string notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
        if (notes.Length > 20000) notes = notes[..20000] + "\n\n(Release log truncated.)";
        return UpdateTrust.Verify(bytes, signature, version, publicKey) with { ReleaseNotes = notes };
    }
    public async Task<string> PrepareAsync(SignedUpdate update, string appRoot, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        update = UpdateTrust.Verify(update.Bytes, update.Signature, update.Manifest.Version, publicKey);
        string folder = Path.Combine(Path.GetFullPath(appRoot), ".updates", Guid.NewGuid().ToString("N"));
        UpdateInstaller.NoLinks(Path.GetFullPath(appRoot), folder);
        Directory.CreateDirectory(folder);
        string zip = Path.Combine(folder, "release.zip");
        await File.WriteAllBytesAsync(Path.Combine(folder, "manifest.json"), update.Bytes, cancel);
        await File.WriteAllBytesAsync(Path.Combine(folder, "manifest.sig"), update.Signature, cancel);
        using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await Download(UpdateTrust.AssetUrl(update.Manifest.Version, update.Manifest.FileName), output, update.Manifest.Size, cancel, progress);
        UpdateInstaller.ExtractVerified(zip, Path.Combine(folder, "preview"), update, publicKey);
        return folder;
    }
    private async Task<byte[]> Bytes(Uri url, long max, CancellationToken cancel)
    {
        using var result = new MemoryStream(); await Download(url, result, max, cancel); return result.ToArray();
    }
    private async Task Download(Uri url, Stream output, long max, CancellationToken cancel, IProgress<double>? progress = null)
    {
        bool redirect = false;
        for (int hop = 0; hop < 4; hop++)
        {
            if (!UpdateTrust.AllowedUrl(url, redirect)) throw new UserError("An update download redirected to an untrusted location.");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (url.IdnHost == "api.github.com") throw new UserError("The update service unexpectedly redirected.");
                url = response.Headers.Location is { } next ? new Uri(url, next) : throw new UserError("Invalid update redirect."); redirect = true; continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new UserError("GitHub update checks are rate limited. Try again later.");
            if (!response.IsSuccessStatusCode) throw new UserError("The update service is unavailable. Try again later.");
            if (response.Content.Headers.ContentLength > max) throw new UserError("The update download exceeds its allowed size.");
            using var input = await response.Content.ReadAsStreamAsync(cancel);
            byte[] buffer = new byte[64 * 1024]; long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancel)) != 0)
            {
                total += count; if (total > max) throw new UserError("The update download exceeds its allowed size.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancel); progress?.Report((double)total / max);
            }
            return;
        }
        throw new UserError("Too many update redirects.");
    }
    public void Dispose() => http.Dispose();
}
