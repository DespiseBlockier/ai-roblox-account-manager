using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public sealed class SignupProxy
{
    public string Type { get; set; } = "http";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string? Login { get; set; }
    public string? Password { get; set; }
    public override string ToString() => "SignupProxy [redacted]";
}

public sealed class SignupRequest
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Birthday { get; set; } = "";
    public bool AcceptTerms { get; set; }
    public string ApiKey { get; set; } = "";
    public string Provider { get; set; } = "solvex"; // Preserve existing explicitly run Solvex scripts.
    public SignupProxy? Proxy { get; set; }
    public override string ToString() => "SignupRequest [redacted]";
    public void Validate()
    {
        if (Username is null || !Regex.IsMatch(Username, "^[A-Za-z0-9_]{3,20}$") || Username.StartsWith("YOUR_", StringComparison.Ordinal)) throw new UserError("Enter a Roblox username of 3–20 letters, numbers, or underscores.");
        BrowserAccountStore.ValidatePassword(Password);
        if (Password is null || Password.Length < 8 || Password.StartsWith("YOUR_", StringComparison.Ordinal)) throw new UserError("Enter a Roblox password of at least eight characters.");
        if (!DateOnly.TryParseExact(Birthday, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birthday) || birthday > DateOnly.FromDateTime(DateTime.UtcNow) || birthday.Year < 1900) throw new UserError("Enter your real birthday as YYYY-MM-DD.");
        if (!AcceptTerms) throw new UserError("Read Roblox's terms and privacy policy, then set acceptTerms to true to sign up.");
        _ = BrowserSolverClient.ProviderHost(Provider);
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length > 512 || ApiKey.Any(char.IsControl) || ApiKey.StartsWith("YOUR_", StringComparison.Ordinal)) throw new UserError("Enter your own " + BrowserSolverClient.ProviderName(Provider) + " API key.");
        if (Proxy is null)
        {
            if (Provider == "solvex") throw new UserError("Solvex requires your own proxy. Use 2Captcha or Anti-Captcha for proxyless mode.");
            return;
        }
        if (Provider != "solvex" && Proxy.Type == "https") throw new UserError("This provider supports http or socks5 proxies in signup. Use type http for an HTTP CONNECT proxy.");
        if (Proxy.Type is not ("http" or "https" or "socks5") || Proxy.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(Proxy.Address) || Proxy.Address.Length > 253 || Proxy.Address.StartsWith("YOUR_", StringComparison.Ordinal) ||
            Uri.CheckHostName(Proxy.Address) == UriHostNameType.Unknown || Proxy.Address.Any(c => c is '/' or '\\' or '@' or ':' || char.IsWhiteSpace(c) || char.IsControl(c))) throw new UserError("Enter a valid http, https, or socks5 proxy host and port.");
        if (Provider == "anticaptcha")
        {
            var probe = JsonSerializer.SerializeToElement(new { type = "FunCaptchaTask", websiteURL = "https://www.roblox.com/", websitePublicKey = "validation", proxyType = Proxy.Type, proxyAddress = Proxy.Address, proxyPort = Proxy.Port, userAgent = "Validation/1.0" });
            _ = BrowserSolverClient.ValidateTask(probe, Provider);
        }
        foreach (string? value in new[] { Proxy.Login, Proxy.Password })
            if (value is not null && (value.Length > 512 || value.Any(char.IsControl))) throw new UserError("Invalid proxy credentials.");
    }
    public void ClearSecrets() { Password = ""; ApiKey = ""; if (Proxy is not null) Proxy.Password = Proxy.Login = null; }
}

// One explicit signup, in a fresh cookie jar. Roblox credentials never go to the solver.
// An optional proxy is shared by Roblox requests and the CAPTCHA task. No proxy means direct Roblox requests.
public sealed class BrowserSignupClient
{
    private readonly Func<CookieContainer, SignupProxy?, HttpMessageHandler> handler;
    private readonly Func<SolverRequest, CancellationToken, Task<object>> solve;
    public BrowserSignupClient(Func<SolverRequest, CancellationToken, Task<object>> solve,
        Func<CookieContainer, SignupProxy?, HttpMessageHandler>? handler = null)
    {
        this.solve = solve;
        this.handler = handler ?? ((cookies, proxy) => new HttpClientHandler
        {
            CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = false,
            Proxy = proxy is null ? null : new WebProxy(new Uri($"{proxy.Type}://{proxy.Address}:{proxy.Port}"))
            { Credentials = string.IsNullOrEmpty(proxy.Login) ? null : new NetworkCredential(proxy.Login, proxy.Password) }, UseProxy = proxy is not null
        });
    }

    public async Task<object> CreateAndSave(SignupRequest call, string userAgent,
        Func<RobloxIdentity, string, string?, CancellationToken, Task<Account>> save, Action<string> progress, CancellationToken cancel)
    {
        bool submitted = false, created = false;
        try
        {
            call.Validate(); cancel.ThrowIfCancellationRequested();
            if (userAgent.Length is < 1 or > 1000 || userAgent.Any(char.IsControl)) throw new UserError("Invalid browser user agent.");
            var cookies = new CookieContainer();
            using var client = new HttpClient(handler(cookies, call.Proxy)) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            client.DefaultRequestHeaders.Referrer = new Uri("https://www.roblox.com/");
            client.DefaultRequestHeaders.Add("Origin", "https://www.roblox.com");
            string? csrf = null;
            async Task<HttpResponseMessage> Send(string path, object? payload = null, Dictionary<string, string>? proof = null)
            {
                // Paths are supplied only by this implementation; no script-selected endpoint.
                string host = path == "/v2/signup" ? "auth.roblox.com" : path == "/v1/users/authenticated" ? "users.roblox.com" : "apis.roblox.com";
                for (int attempt = 0; ; attempt++)
                {
                    cancel.ThrowIfCancellationRequested();
                    using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, "https://" + host + path);
                    if (payload is not null) request.Content = JsonContent.Create(payload);
                    if (csrf is not null) request.Headers.Add("x-csrf-token", csrf);
                    foreach (var pair in proof ?? []) request.Headers.Add(pair.Key, pair.Value);
                    if (path == "/v2/signup") submitted = true;
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                    string? updated = Header(response, "x-csrf-token");
                    if (updated is not null) csrf = updated;
                    if (payload is not null && attempt == 0 && response.StatusCode == HttpStatusCode.Forbidden && updated is not null && Header(response, "rblx-challenge-id") is null)
                    { response.Dispose(); continue; }
                    return response;
                }
            }
            progress("Preparing one account signup…");
            using var nonceResponse = await Send("/hba-service/v1/getServerNonce");
            if (!nonceResponse.IsSuccessStatusCode) throw new UserError("Roblox could not prepare signup. No signup was submitted.");
            var nonceJson = await Read(nonceResponse, cancel);
            string nonce = nonceJson.ValueKind == JsonValueKind.String ? nonceJson.GetString() ?? "" : "";
            if (nonce.Length is < 1 or > 4000) throw new UserError("Roblox returned an unsupported signup nonce.");
            using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo());
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string signature = Convert.ToBase64String(signing.SignData(Encoding.UTF8.GetBytes(publicKey + "|" + timestamp.ToString(CultureInfo.InvariantCulture) + "|" + nonce), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            var payload = new { username = call.Username, password = call.Password, birthday = call.Birthday + "T00:00:00.000Z", gender = 1, isTosAgreementBoxChecked = true, signupType = 0,
                secureAuthenticationIntent = new { clientPublicKey = publicKey, clientEpochTimestamp = timestamp, serverNonce = nonce, saiSignature = signature } };
            using var initial = await Send("/v2/signup", payload);
            JsonElement result;
            if (initial.IsSuccessStatusCode) result = await Read(initial, cancel);
            else if (initial.StatusCode == HttpStatusCode.Forbidden && Header(initial, "rblx-challenge-id") is string challengeId)
            {
                string? challengeType = Header(initial, "rblx-challenge-type");
                if (!string.Equals(challengeType?.Trim(), "captcha", StringComparison.OrdinalIgnoreCase)) throw UnsupportedVerification(challengeType, call, afterCaptcha: false);
                string encoded = Header(initial, "rblx-challenge-metadata") ?? throw new UserError("Roblox did not provide CAPTCHA metadata.");
                JsonElement metadata;
                try { using var json = JsonDocument.Parse(Convert.FromBase64String(encoded)); metadata = json.RootElement.Clone(); }
                catch { throw new UserError("Roblox returned unreadable CAPTCHA metadata."); }
                string blob = Text(metadata, "dataExchangeBlob", 50000), captchaId = Text(metadata, "unifiedCaptchaId", 512);
                if (Text(metadata, "actionType", 40) != "Signup") throw new UserError("Roblox returned a different CAPTCHA action. Signup stopped.");
                using var metadataResponse = await Send("/captcha/v1/metadata");
                if (!metadataResponse.IsSuccessStatusCode) throw new UserError("Roblox CAPTCHA configuration is unavailable.");
                var config = await Read(metadataResponse, cancel);
                string siteKey = Text(config.GetProperty("funCaptchaPublicKeys"), "ACTION_TYPE_WEB_SIGNUP", 100);
                if (!Guid.TryParse(siteKey, out _)) throw new UserError("Roblox returned an unsupported CAPTCHA key.");
                string providerName = BrowserSolverClient.ProviderName(call.Provider);
                progress("CAPTCHA detected. Requesting one " + providerName + " solution…");
                var taskFields = new Dictionary<string, object?>
                {
                    ["type"] = call.Proxy is null ? "FunCaptchaTaskProxyless" : "FunCaptchaTask",
                    ["websiteURL"] = "https://www.roblox.com/", ["websitePublicKey"] = siteKey,
                    ["data"] = call.Provider == "solvex" ? blob : JsonSerializer.Serialize(new { blob })
                };
                if (call.Provider == "2captcha" || call.Provider == "anticaptcha" && call.Proxy is not null) taskFields["userAgent"] = userAgent;
                if (call.Proxy is SignupProxy proxy)
                {
                    taskFields["proxyType"] = proxy.Type; taskFields["proxyAddress"] = proxy.Address; taskFields["proxyPort"] = proxy.Port;
                    if (!string.IsNullOrEmpty(proxy.Login)) taskFields["proxyLogin"] = proxy.Login;
                    if (!string.IsNullOrEmpty(proxy.Password)) taskFields["proxyPassword"] = proxy.Password;
                }
                var task = JsonSerializer.SerializeToElement(taskFields);
                var solved = JsonSerializer.SerializeToElement(await solve(new SolverRequest { Id = 1, Provider = call.Provider, Action = "solve", ApiKey = call.ApiKey, Task = task }, cancel));
                if (!solved.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) throw new UserError(providerName + " could not solve this CAPTCHA. Signup stopped without another paid attempt.");
                string token = Text(solved, "token", 20000);
                var solution = solved.GetProperty("solution");
                string solverAgent = solution.TryGetProperty("userAgent", out var returnedAgent) && returnedAgent.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(returnedAgent.GetString())
                    ? Text(solution, "userAgent", 1000)
                    : call.Provider == "2captcha" || call.Provider == "anticaptcha" && call.Proxy is not null ? userAgent
                    : throw new UserError(providerName + " did not return the user agent needed to submit this solution.");
                if (solverAgent.Any(char.IsControl)) throw new UserError(providerName + " returned an invalid user agent.");
                client.DefaultRequestHeaders.UserAgent.Clear(); client.DefaultRequestHeaders.UserAgent.ParseAdd(solverAgent);
                string proofMetadata = JsonSerializer.Serialize(new { unifiedCaptchaId = captchaId, captchaToken = token, actionType = "Signup" });
                progress("Submitting the solution to Roblox…");
                using var continued = await Send("/challenge/v1/continue", new { challengeID = challengeId, challengeType = "captcha", challengeMetadata = proofMetadata });
                if (!continued.IsSuccessStatusCode) throw new UserError("Roblox rejected the CAPTCHA solution. Signup stopped.");
                var continuation = await Read(continued, cancel);
                if (continuation.TryGetProperty("challengeType", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(next.GetString())) throw UnsupportedVerification(next.GetString(), call, afterCaptcha: true);
                using var completed = await Send("/v2/signup", payload, new() { ["rblx-challenge-id"] = challengeId, ["rblx-challenge-type"] = "captcha", ["rblx-challenge-metadata"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(proofMetadata)) });
                if (!completed.IsSuccessStatusCode) throw Rejected(completed.StatusCode);
                result = await Read(completed, cancel);
            }
            else throw Rejected(initial.StatusCode);
            created = true;
            if (!result.TryGetProperty("userId", out var id) || !id.TryGetInt64(out long userId) || userId <= 0) throw new UserError("Roblox reported signup success without an account ID. Check your username before retrying.");
            string cookie = cookies.GetCookies(new Uri("https://www.roblox.com/"))[".ROBLOSECURITY"]?.Value ?? "";
            if (string.IsNullOrEmpty(cookie)) throw new UserError("The account was created, but Roblox did not return a usable session. Sign in through Add account to save it.");
            progress("Verifying the new account before saving…");
            using var identityResponse = await Send("/v1/users/authenticated");
            if (!identityResponse.IsSuccessStatusCode) throw new UserError("The account was created, but its session could not be verified. Sign in through Add account to save it.");
            var identityJson = await Read(identityResponse, cancel);
            if (!identityJson.TryGetProperty("id", out var current) || !current.TryGetInt64(out long currentId) || currentId != userId || !string.Equals(Text(identityJson, "name", 20), call.Username, StringComparison.OrdinalIgnoreCase)) throw new UserError("The new session identity did not match. Nothing was saved; check the username before retrying.");
            var identity = new RobloxIdentity(userId, Text(identityJson, "name", 20), Text(identityJson, "displayName", 100));
            cancel.ThrowIfCancellationRequested();
            Account saved;
            try { saved = await save(identity, cookie, call.Password, cancel); }
            catch (OperationCanceledException) { throw; }
            catch { throw new UserError("The account was created, but saving to the vault failed. Sign in through Add account to save it; do not create it again."); }
            progress("Created and saved @" + saved.Username);
            return new { success = true, saved = true, userId = saved.UserId.ToString(CultureInfo.InvariantCulture), username = saved.Username };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (UserError) { throw; }
        catch { throw new UserError(created ? "The account was created, but could not be saved. Sign in through Add account to save it." : submitted ? "Signup's outcome could not be confirmed. Check the username before retrying; no automatic retry was made." : "Signup preparation failed. Check your connection or optional proxy and try again."); }
        finally { call.ClearSecrets(); }
    }
    private static UserError Rejected(HttpStatusCode code) => new(code == HttpStatusCode.TooManyRequests ? "Roblox rate limited signup. Stop and try later." : "Roblox rejected signup. Check the username, password, birthday, and verification requirements before trying again.");
    private static UserError UnsupportedVerification(string? type, SignupRequest call, bool afterCaptcha)
    {
        string name = type?.Trim() ?? "";
        // Show only a short type label, never the challenge ID, metadata, or credentials.
        if (string.IsNullOrWhiteSpace(name)) name = "missing type header";
        else if (!Regex.IsMatch(name, @"^[a-zA-Z][a-zA-Z0-9_-]{0,63}$") ||
            new[] { call.ApiKey, call.Password, call.Proxy?.Password, call.Proxy?.Login }.Any(secret => !string.IsNullOrEmpty(secret) && name.Contains(secret, StringComparison.OrdinalIgnoreCase))) name = "unrecognized";
        return new UserError("Roblox verification: " + name + ". " +
            (afterCaptcha ? "Roblox requested another verification after the CAPTCHA solution. No extra solver task was sent. " : "This signup helper handles CAPTCHA only. No solver task was sent. ") +
            "Complete signup through Roblox's regular browser flow. The verification type can be shared for troubleshooting; keep keys, passwords, and cookies private.");
    }
    private static string Text(JsonElement json, string name, int max) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is string text && text.Length > 0 && text.Length <= max ? text : throw new UserError("Roblox or the solver returned incomplete signup data.");
    private static string? Header(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        string value = values.Single();
        if (value.Length is < 1 or > 80000 || value.Any(char.IsControl)) throw new UserError("Roblox returned invalid verification headers.");
        return value;
    }
    private static async Task<JsonElement> Read(HttpResponseMessage response, CancellationToken cancel)
    {
        if (response.Content.Headers.ContentLength > 128000) throw new UserError("Roblox returned too much signup data.");
        using var input = await response.Content.ReadAsStreamAsync(cancel); using var output = new MemoryStream();
        byte[] buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, cancel)) != 0)
        { if (output.Length + read > 128000) throw new UserError("Roblox returned too much signup data."); await output.WriteAsync(buffer.AsMemory(0, read), cancel); }
        using var document = JsonDocument.Parse(output.ToArray()); return document.RootElement.Clone();
    }
}
