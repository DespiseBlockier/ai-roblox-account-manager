using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public sealed class SolverRequest
{
    public int Id { get; set; }
    public string Provider { get; set; } = "";
    public string Action { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public JsonElement Task { get; set; }
    public override string ToString() => "SolverRequest [redacted]";
}

public static class BrowserSolverRuntime
{
    public static string Source
    {
        get
        {
            using var stream = typeof(BrowserSolverRuntime).Assembly.GetManifestResourceStream("LocalAccountManager.BrowserSolvers.js") ?? throw new UserError("The solver helpers are missing.");
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
    }
}

public sealed class BrowserSolverClient : IDisposable
{
    private readonly HttpClient client;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    public BrowserSolverClient(HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
        this.delay = delay ?? Task.Delay;
    }
    public async Task<object> Execute(SolverRequest request, CancellationToken cancel)
    {
        bool status = request.Action == "status";
        try
        {
            if (request.Id < 1 || request.ApiKey is null || request.ApiKey.Length is < 1 or > 512 || request.ApiKey.Any(char.IsControl)) throw new UserError("INVALID_API_KEY");
            string host = request.Provider switch { "capsolver" => "api.capsolver.com", "solvex" => "api.solvex.run", _ => throw new UserError("UNSUPPORTED_PROVIDER") };
            if (status)
            {
                var balance = await Post(host, "getBalance", new { clientKey = request.ApiKey }, cancel);
                return !HasError(balance) && balance.TryGetProperty("balance", out var amount) && amount.TryGetDecimal(out _);
            }
            if (request.Action != "solve") throw new UserError("UNSUPPORTED_ACTION");
            var task = ValidateTask(request.Task, request.Provider);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var result = await Post(host, "createTask", new { clientKey = request.ApiKey, task }, deadline.Token);
            if (HasError(result)) return Failure(ErrorCode(result, request.ApiKey));
            if (!Ready(result))
            {
                if (!result.TryGetProperty("taskId", out var taskId) || taskId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) || taskId.ToString().Length is < 1 or > 200) return Failure("INVALID_PROVIDER_RESPONSE");
                for (int poll = 0; poll < 120; poll++)
                {
                    await delay(TimeSpan.FromMilliseconds(request.Provider == "solvex" ? 500 : 2000), deadline.Token);
                    result = await Post(host, "getTaskResult", new { clientKey = request.ApiKey, taskId = taskId.Clone() }, deadline.Token);
                    if (HasError(result)) return Failure(ErrorCode(result, request.ApiKey));
                    if (Ready(result)) break;
                    if (!result.TryGetProperty("status", out var phase) || phase.GetString() != "processing") return Failure("INVALID_PROVIDER_RESPONSE");
                }
            }
            if (!Ready(result) || !result.TryGetProperty("solution", out var solution) || solution.ValueKind != JsonValueKind.Object) return Failure("SOLVE_TIMEOUT");
            if (solution.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.False) return Failure("CAPTCHA_UNSOLVABLE");
            var clean = new Dictionary<string, JsonElement>();
            foreach (var field in solution.EnumerateObject())
                if (field.Name is "token" or "gRecaptchaResponse" or "text" or "userAgent" or "expireTime" or "challengeType" or "verified") clean[field.Name] = field.Value.Clone();
            string? token = clean.TryGetValue("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() :
                clean.TryGetValue("gRecaptchaResponse", out t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrEmpty(token) && !(clean.TryGetValue("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString()?.Length > 0)) return Failure("EMPTY_SOLUTION");
            return new { success = true, token, solution = clean, errorCode = (string?)null, message = "Solved by the provider. The website has not independently confirmed acceptance." };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (UserError ex) { return status ? false : Failure(ex.Message); }
        catch (OperationCanceledException) { return status ? false : Failure("SOLVE_TIMEOUT"); }
        catch { return status ? false : Failure("PROVIDER_UNAVAILABLE"); }
        finally { request.ApiKey = ""; }
    }
    public static Dictionary<string, JsonElement> ValidateTask(JsonElement task, string provider)
    {
        if (task.ValueKind != JsonValueKind.Object || task.GetRawText().Length > 64000) throw new UserError("INVALID_CAPTCHA");
        var fields = new Dictionary<string, JsonElement>();
        foreach (var field in task.EnumerateObject())
        {
            if (field.Name is not ("type" or "websiteURL" or "websiteKey" or "websitePublicKey" or "data" or "funcaptchaApiJSSubdomain" or "isInvisible" or "pageAction" or "minScore" or "enterprisePayload" or "metadata" or "recaptchaDataSValue" or "apiDomain" or "proxy" or "proxyType" or "proxyAddress" or "proxyPort" or "proxyLogin" or "proxyPassword" or "userAgent" or "body" or "browser" or "useHttp3")) throw new UserError("UNSUPPORTED_TASK_FIELD");
            if (!fields.TryAdd(field.Name, field.Value.Clone())) throw new UserError("INVALID_CAPTCHA");
        }
        if (!fields.TryGetValue("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String || typeValue.GetString() is not string type || !Regex.IsMatch(type, "^[A-Za-z0-9]{1,80}$")) throw new UserError("INVALID_CAPTCHA_TYPE");
        if (!fields.TryGetValue("websiteURL", out var url) || url.ValueKind != JsonValueKind.String || !Security.IsBrowserPage(url.GetString() ?? "")) throw new UserError("INVALID_WEBSITE_URL");
        if (provider == "solvex")
        {
            if (type != "FunCaptchaTask") throw new UserError("UNSUPPORTED_CAPTCHA_TYPE");
            if (!fields.TryGetValue("proxyType", out var proxyType) || proxyType.ValueKind != JsonValueKind.String || proxyType.GetString() is not ("http" or "https" or "socks4" or "socks5") ||
                !fields.TryGetValue("proxyAddress", out var address) || address.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(address.GetString()) ||
                !fields.TryGetValue("proxyPort", out var port) || port.ValueKind != JsonValueKind.Number || !port.TryGetInt32(out int number) || number is < 1 or > 65535) throw new UserError("PROXY_REQUIRED");
            if (!fields.TryGetValue("data", out var blob) || blob.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(blob.GetString())) throw new UserError("CAPTCHA_DATA_REQUIRED");
            if (!fields.TryGetValue("websitePublicKey", out var publicKey) || publicKey.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(publicKey.GetString())) throw new UserError("CAPTCHA_KEY_REQUIRED");
        }
        else if (type.StartsWith("FunCaptcha", StringComparison.OrdinalIgnoreCase)) throw new UserError("CAPSOLVER_FUNCAPTCHA_UNSUPPORTED");
        return fields;
    }
    private async Task<JsonElement> Post(string host, string action, object payload, CancellationToken cancel)
    {
        using var response = await client.PostAsJsonAsync(new Uri("https://" + host + "/" + action), payload, cancel);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 128000) throw new UserError("PROVIDER_UNAVAILABLE");
        using var input = await response.Content.ReadAsStreamAsync(cancel); using var output = new MemoryStream();
        byte[] buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, cancel)) != 0)
        { if (output.Length + read > 128000) throw new UserError("INVALID_PROVIDER_RESPONSE"); await output.WriteAsync(buffer.AsMemory(0, read), cancel); }
        using var json = JsonDocument.Parse(output.ToArray()); return json.RootElement.Clone();
    }
    private static bool HasError(JsonElement result) => !result.TryGetProperty("errorId", out var error) || !error.TryGetInt32(out int id) || id != 0;
    private static string ErrorCode(JsonElement result, string key)
    {
        if (result.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() is string value &&
            Regex.IsMatch(value, "^[A-Z0-9_]{1,80}$") && !value.Contains(key, StringComparison.Ordinal)) return value;
        return result.TryGetProperty("errorId", out var number) && number.TryGetInt32(out int id) ? id switch
        { 1 => "ERROR_KEY_REJECTED", 10 => "ERROR_ZERO_BALANCE", 12 => "ERROR_CAPTCHA_UNSOLVABLE", 16 => "ERROR_TASK_NOT_FOUND", 21 => "ERROR_INCORRECT_JSON", 22 => "ERROR_TASK_NOT_SUPPORTED", 23 => "ERROR_SITE_KEY_NOT_SUPPORTED", 31 => "ERROR_NO_SLOT_AVAILABLE", _ => "PROVIDER_REJECTED_TASK" } : "PROVIDER_REJECTED_TASK";
    }
    private static bool Ready(JsonElement result) => result.TryGetProperty("status", out var phase) && phase.ValueKind == JsonValueKind.String && phase.GetString() == "ready";
    public static object Failure(string code) => new { success = false, token = (string?)null, solution = (object?)null, errorCode = code, message = "Solve failed: " + code + "." };
    public void Dispose() => client.Dispose();
}
