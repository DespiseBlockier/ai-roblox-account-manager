using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAccountManager;

public sealed record GoogleTokens(string AccessToken, string RefreshToken, DateTimeOffset Expires)
{
    public override string ToString() => "GoogleTokens [redacted]";
}

public sealed class GoogleOAuth(HttpClient client)
{
    public const string Scope = "https://www.googleapis.com/auth/gmail.readonly";
    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(48));
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async Task<GoogleTokens> Connect(GoogleDesktopClient config, Action<string> openBrowser, CancellationToken cancel)
    {
        config.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        string state = Random(), verifier = Random();
        string redirect = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/oauth/callback/";
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string url = "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new Dictionary<string,string>
        {
            ["client_id"] = config.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = Scope,
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        });
        try
        {
            openBrowser(url); // System browser, never an embedded Google sign-in page.
            for (int attempt = 0; attempt < 16; attempt++)
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var stream = socket.GetStream();
                string header;
                try { header = await Header(stream, requestDeadline.Token); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { continue; }
                var line = header.Split("\r\n", 2)[0].Split(' ');
                if (line.Length != 3 || line[0] != "GET" || !line[1].StartsWith("/oauth/callback/?", StringComparison.Ordinal)) { await Reply(stream, 400, "Invalid sign-in callback.", deadline.Token); continue; }
                var values = ParseQuery(new Uri(redirect).GetLeftPart(UriPartial.Authority) + line[1]);
                if (!values.TryGetValue("state", out var actual) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(state)))
                { await Reply(stream, 400, "Invalid sign-in state. Return to the original sign-in window.", deadline.Token); continue; }
                await Reply(stream, 200, "Sign-in received. Return to Roblox Account Manager. You can close this tab.", deadline.Token);
                if (values.ContainsKey("error")) throw new UserError("Google sign-in was canceled or access was not granted.");
                if (!values.TryGetValue("code", out string? code) || code.Length is < 1 or > 4096) throw new UserError("Google did not return a valid sign-in code.");
                return await Exchange(config, new Dictionary<string,string>
                {
                    ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier, ["grant_type"] = "authorization_code"
                }, deadline.Token, requireRefresh: true);
            }
            throw new UserError("Too many invalid local sign-in callbacks. Start Google sign-in again.");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new UserError("Google sign-in timed out. Click Connect Gmail to try again."); }
        finally { listener.Stop(); }
    }

    public Task<GoogleTokens> Refresh(GoogleDesktopClient config, string refreshToken, CancellationToken cancel) =>
        Exchange(config, new Dictionary<string,string> { ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token" }, cancel, false, refreshToken);

    private async Task<GoogleTokens> Exchange(GoogleDesktopClient config, Dictionary<string,string> values, CancellationToken cancel, bool requireRefresh, string previousRefresh = "")
    {
        config.Validate(); values["client_id"] = config.ClientId; values["client_secret"] = config.ClientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token") { Content = new FormUrlEncodedContent(values) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        using var json = await ReadJson(response, 64000, cancel);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new UserError("Google returned an invalid token response.");
        if (!response.IsSuccessStatusCode || root.TryGetProperty("error", out _)) throw new UserError("Google rejected the connection. Reconnect Gmail; check the OAuth client, test users, and Gmail API setup.");
        if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || !Secret(access.GetString(), 16384) ||
            !root.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out int seconds) || seconds is < 30 or > 86400)
            throw new UserError("Google returned an invalid token response.");
        if (requireRefresh && !root.TryGetProperty("scope", out _)) throw new UserError("Google did not confirm the requested Gmail scope.");
        if (root.TryGetProperty("scope", out var scopes) && (scopes.ValueKind != JsonValueKind.String || !scopes.GetString()!.Split(' ').Contains(Scope)))
            throw new UserError("Read-only Gmail access was not granted.");
        string refresh = root.TryGetProperty("refresh_token", out var refreshed) && refreshed.ValueKind == JsonValueKind.String ? refreshed.GetString()! : previousRefresh;
        if ((requireRefresh || refresh.Length > 0) && !Secret(refresh, 8192)) throw new UserError("Google did not provide offline access. Remove the previous app grant in Google Account permissions and reconnect.");
        return new(access.GetString()!, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    public static bool Secret(string? value, int max) => value is { Length: > 0 } && value.Length <= max && !value.Any(char.IsControl);
    public static async Task<JsonDocument> ReadJson(HttpResponseMessage response, int max, CancellationToken cancel)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancel); using var buffer = new MemoryStream(); byte[] block = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(block, cancel)) > 0) { if (buffer.Length + read > max) throw new UserError("The mail provider returned too much data."); buffer.Write(block, 0, read); }
        try { return JsonDocument.Parse(buffer.ToArray()); } catch (JsonException) { throw new UserError("The mail provider returned an invalid response."); }
    }
    private static async Task<string> Header(NetworkStream stream, CancellationToken cancel)
    {
        var bytes = new List<byte>(); byte[] one = new byte[1];
        while (bytes.Count < 8192)
        {
            if (await stream.ReadAsync(one, cancel) != 1) break; bytes.Add(one[0]);
            int n = bytes.Count; if (n >= 4 && bytes[n-4] == 13 && bytes[n-3] == 10 && bytes[n-2] == 13 && bytes[n-1] == 10) break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
    private static async Task Reply(NetworkStream stream, int status, string body, CancellationToken cancel)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        string headers = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Bad Request") + "\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: " + content.Length + "\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancel); await stream.WriteAsync(content, cancel);
    }
    public static string Query(IReadOnlyDictionary<string,string> values) => string.Join('&', values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    private static Dictionary<string,string> ParseQuery(string url)
    {
        var result = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (string pair in new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        { var parts = pair.Split('=', 2); if (parts.Length != 2 || !result.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1].Replace('+', ' ')))) throw new UserError("Google returned an invalid sign-in callback."); }
        return result;
    }
}
