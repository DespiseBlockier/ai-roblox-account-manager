using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace LocalAccountManager;

public sealed class RobloxApi : IDisposable
{
    private readonly HttpClient client;
    public RobloxApi(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalAccountManager/1.0");
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? cookie = null, string? csrf = null)
    {
        var uri = new Uri(url);
        if (!Security.IsApiUrl(uri)) throw new UserError("Blocked an unexpected API destination.");
        using var request = new HttpRequestMessage(method, uri);
        if (cookie is not null)
        {
            if (cookie.Length == 0 || cookie.Length > 20000 || cookie.Any(c => char.IsControl(c) || c == ';')) throw new UserError("The Roblox session is invalid. Sign in again.");
            request.Headers.Add("Cookie", ".ROBLOSECURITY=" + cookie);
        }
        if (method == HttpMethod.Post) request.Content = new StringContent("");
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Headers.Referrer = new Uri("https://www.roblox.com/");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new RobloxApiError((int)response.StatusCode, response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "This Roblox session expired. Open the account browser and sign in again.",
            HttpStatusCode.Forbidden => "Roblox requires you to verify this action. Open the account browser and complete any verification there.",
            HttpStatusCode.TooManyRequests => "Roblox is rate limiting requests. Wait a little before trying again.",
            _ => $"Roblox could not complete the request (HTTP {(int)response.StatusCode})."
        });
    }

    private static async Task<JsonDocument> Json(HttpResponseMessage response)
    {
        Check(response);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, timeout.Token)) > 0)
        {
            if (buffer.Length + read > 2 * 1024 * 1024) throw new UserError("Roblox returned a response that was too large.");
            buffer.Write(block, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    public async Task<RobloxIdentity> Identity(string cookie)
    {
        using var response = await Send(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie);
        using var json = await Json(response);
        var r = json.RootElement;
        long id = r.GetProperty("id").GetInt64();
        string name = r.GetProperty("name").GetString() ?? "";
        string display = r.GetProperty("displayName").GetString() ?? name;
        if (id <= 0 || name.Length is < 1 or > 100 || display.Length > 100) throw new UserError("Roblox returned an invalid account identity.");
        return new(id, name, display);
    }

    public async Task<ModerationRestriction?> Restriction(string cookie)
    {
        using var response = await Send(HttpMethod.Get, "https://usermoderation.roblox.com/v1/not-approved", cookie);
        using var json = await Json(response);
        return Moderation.Parse(json.RootElement);
    }

    public async Task<AgeGroupResult> AgeGroup(string cookie)
    {
        using var response = await Send(HttpMethod.Get, "https://apis.roblox.com/user-settings-api/v1/account-insights/age-group", cookie);
        using var json = await Json(response);
        return AgeGroups.Parse(json.RootElement);
    }

    public async Task<bool> IsGroupMember(string cookie, long userId, long groupId)
    {
        GroupTarget.Validate(groupId);
        if (userId <= 0) throw new UserError("Invalid account user ID.");
        using var response = await Send(HttpMethod.Get, $"https://groups.roblox.com/v2/users/{userId}/groups/roles", cookie);
        using var json = await Json(response);
        var data = json.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array) throw new UserError("Roblox returned an unexpected membership response.");
        return data.EnumerateArray().Any(item => item.GetProperty("group").GetProperty("id").GetInt64() == groupId);
    }

    public async Task<string> Ticket(string cookie)
    {
        const string url = "https://auth.roblox.com/v1/authentication-ticket/";
        using var first = await Send(HttpMethod.Post, url, cookie);
        if (first.IsSuccessStatusCode && first.Headers.TryGetValues("rbx-authentication-ticket", out var tickets)) return tickets.Single();
        if (first.StatusCode != HttpStatusCode.Forbidden || !first.Headers.TryGetValues("x-csrf-token", out var tokens))
        { Check(first); throw new UserError("Roblox did not return a launch ticket."); }
        using var second = await Send(HttpMethod.Post, url, cookie, tokens.Single());
        Check(second);
        if (second.Headers.TryGetValues("rbx-authentication-ticket", out tickets)) return tickets.Single();
        throw new UserError("Roblox did not return a launch ticket. Try playing from the account browser.");
    }

    public async Task<ServerPage> Servers(long place, string? cursor, bool smallest)
    {
        if (place <= 0 || cursor?.Length > 2048) throw new UserError("Invalid server query.");
        string url = $"https://games.roblox.com/v1/games/{place}/servers/Public?sortOrder={(smallest ? "Asc" : "Desc")}&excludeFullGames=true&limit=100";
        if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor);
        using var response = await Send(HttpMethod.Get, url);
        using var json = await Json(response);
        var servers = new List<GameServer>();
        foreach (var item in json.RootElement.GetProperty("data").EnumerateArray())
        {
            string id = GameTarget.ValidateJob(item.GetProperty("id").GetString() ?? "");
            if (id.Length == 0) continue;
            servers.Add(new(id, item.GetProperty("playing").GetInt32(), item.GetProperty("maxPlayers").GetInt32(),
                item.TryGetProperty("ping", out var ping) && ping.ValueKind == JsonValueKind.Number ? ping.GetInt32() : null,
                item.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Number ? fps.GetDouble() : null));
        }
        string? next = json.RootElement.TryGetProperty("nextPageCursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return new(servers, next);
    }
    public void Dispose() => client.Dispose();
}
