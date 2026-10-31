using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAccountManager;

public sealed class GmailService : IDisposable
{
    private readonly AppState state;
    private readonly Action save;
    private readonly HttpClient client;
    private readonly GoogleOAuth oauth;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private readonly Dictionary<Guid,GoogleTokens> tokens = [];
    private readonly Func<DateTimeOffset> now;
    private readonly Func<TimeSpan,CancellationToken,Task> delay;
    public bool HasAutomaticMailboxes => state.Gmail.Connections.Any(c => c.AutomaticLoginCodes);
    public GmailService(AppState state, Action save, HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null, Func<TimeSpan,CancellationToken,Task>? delay = null)
    {
        this.state = state; this.save = save; this.now = now ?? (() => DateTimeOffset.UtcNow); this.delay = delay ?? Task.Delay;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
        oauth = new GoogleOAuth(client);
    }
    public bool IsConnected(GmailConnection connection) => state.Gmail.Connections.Contains(connection);
    public object[] List() => state.Gmail.Connections.Select(c => (object)new { id = c.Id.ToString("D"), address = c.Address, automaticLoginCodes = c.AutomaticLoginCodes }).ToArray();
    public GmailConnection Resolve(string mailbox)
    {
        var result = state.Gmail.Connections.SingleOrDefault(c => c.Id.ToString("D").Equals(mailbox, StringComparison.OrdinalIgnoreCase) || c.Address.Equals(mailbox, StringComparison.OrdinalIgnoreCase));
        return result ?? throw new UserError("Connect this Gmail mailbox in Settings → Gmail first.");
    }
    public string Recipient(GmailConnection connection, string address) => address.Length == 0 ? connection.Address :
        GmailAddress.BelongsTo(address, connection.Address) ? GmailAddress.Normalize(address) : throw new UserError("Use the connected mailbox's address or one of its plus aliases.");
    public IReadOnlyList<GmailConnection> ForLogin(Account account) => account.GmailConnectionId is Guid id
        ? state.Gmail.Connections.Where(c => c.Id == id && account.AutoEmailCodes && c.AutomaticLoginCodes).ToList()
        : state.Gmail.Connections.Where(c => c.AutomaticLoginCodes).ToList();
    public bool KnownLogin(GmailConnection connection, string username, string address) => state.Accounts.Any(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && a.GmailConnectionId == connection.Id && a.EmailAddress.Equals(address, StringComparison.OrdinalIgnoreCase));
    public Account SavedAccount(RobloxIdentity identity) => state.Accounts.SingleOrDefault(a => a.UserId == identity.Id) ?? throw new UserError("Save this browser account with ram.addAccount() before linking its email.");
    public void Bind(Account account, GmailConnection connection, string recipient, bool automatic, bool verified = false)
    {
        recipient = Recipient(connection, recipient);
        var previous = (account.GmailConnectionId, account.EmailAddress, account.AutoEmailCodes, account.EmailVerifiedAt);
        account.GmailConnectionId = connection.Id; account.EmailAddress = recipient; account.AutoEmailCodes = automatic;
        account.EmailVerifiedAt = verified ? now() : previous.GmailConnectionId == connection.Id && previous.EmailAddress == recipient ? previous.EmailVerifiedAt : null;
        try { save(); } catch { (account.GmailConnectionId, account.EmailAddress, account.AutoEmailCodes, account.EmailVerifiedAt) = previous; throw; }
    }
    public void Associate(IReadOnlyList<Account> accounts, GmailConnection? connection, string recipient, bool automatic)
    {
        string address = connection is null ? "" : Recipient(connection, recipient);
        var previous = accounts.Select(a => (Account:a, a.GmailConnectionId,a.EmailAddress,a.AutoEmailCodes,a.EmailVerifiedAt)).ToList();
        foreach (var a in accounts) { a.GmailConnectionId = connection?.Id; a.EmailAddress = address; a.AutoEmailCodes = connection is not null && automatic; a.EmailVerifiedAt = null; }
        try { save(); } catch { foreach (var item in previous) { item.Account.GmailConnectionId = item.GmailConnectionId; item.Account.EmailAddress = item.EmailAddress; item.Account.AutoEmailCodes = item.AutoEmailCodes; item.Account.EmailVerifiedAt = item.EmailVerifiedAt; } throw; }
    }
    public async Task<GmailConnection> Connect(Action<string> openBrowser, CancellationToken cancel)
    {
        if (state.Gmail.Connections.Count >= 20) throw new UserError("Connect at most 20 Gmail mailboxes.");
        var obtained = await oauth.Connect(state.Gmail.Client, openBrowser, cancel);
        string address = await Profile(obtained.AccessToken, cancel);
        cancel.ThrowIfCancellationRequested();
        var connection = state.Gmail.Connections.SingleOrDefault(c => c.Address == address);
        bool added = connection is null; connection ??= new() { Address = address };
        string previous = connection.RefreshToken; connection.RefreshToken = obtained.RefreshToken;
        if (added) state.Gmail.Connections.Add(connection);
        try { save(); tokens[connection.Id] = obtained; }
        catch { connection.RefreshToken = previous; if (added) state.Gmail.Connections.Remove(connection); throw; }
        return connection;
    }
    public void Remove(GmailConnection connection)
    {
        // Local removal is immediate. The setup window also links to Google's grant-revocation page.
        var bound = state.Accounts.Where(a => a.GmailConnectionId == connection.Id).Select(a => (Account:a, a.GmailConnectionId,a.EmailAddress,a.AutoEmailCodes,a.EmailVerifiedAt)).ToList();
        state.Gmail.Connections.Remove(connection);
        foreach (var item in bound) { item.Account.GmailConnectionId = null; item.Account.EmailAddress = ""; item.Account.AutoEmailCodes = false; item.Account.EmailVerifiedAt = null; }
        try { save(); tokens.Remove(connection.Id); connection.RefreshToken = ""; }
        catch { state.Gmail.Connections.Add(connection); foreach (var item in bound) { item.Account.GmailConnectionId = item.GmailConnectionId; item.Account.EmailAddress = item.EmailAddress; item.Account.AutoEmailCodes = item.AutoEmailCodes; item.Account.EmailVerifiedAt = item.EmailVerifiedAt; } throw; }
    }
    public async Task Test(GmailConnection connection, CancellationToken cancel) => _ = await Access(connection, cancel, force: true);
    private async Task<string> Access(GmailConnection connection, CancellationToken cancel, bool force = false)
    {
        await tokenLock.WaitAsync(cancel);
        try
        {
            if (!state.Gmail.Connections.Contains(connection)) throw new UserError("This Gmail mailbox was disconnected.");
            if (!force && tokens.TryGetValue(connection.Id, out var cached) && cached.Expires > now().AddMinutes(1)) return cached.AccessToken;
            tokens.Remove(connection.Id);
            var refreshed = await oauth.Refresh(state.Gmail.Client, connection.RefreshToken, cancel);
            if (!(await Profile(refreshed.AccessToken, cancel)).Equals(connection.Address, StringComparison.OrdinalIgnoreCase)) throw new UserError("The Google connection no longer matches this mailbox. Reconnect Gmail.");
            cancel.ThrowIfCancellationRequested();
            if (!IsConnected(connection)) throw new UserError("This Gmail mailbox was disconnected.");
            if (refreshed.RefreshToken != connection.RefreshToken)
            {
                string old = connection.RefreshToken; connection.RefreshToken = refreshed.RefreshToken;
                try { save(); } catch { connection.RefreshToken = old; throw; }
            }
            tokens[connection.Id] = refreshed; return refreshed.AccessToken;
        }
        finally { tokenLock.Release(); }
    }
    private async Task<string> Profile(string access, CancellationToken cancel)
    { using var document = await Get("profile", access, cancel); return GmailAddress.Normalize(document.RootElement.GetProperty("emailAddress").GetString()!); }
    private async Task<JsonDocument> Get(string path, string access, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UserError("Gmail access was denied. Reconnect Gmail and check Gmail API/OAuth permissions.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new UserError("Google is rate limiting mailbox access. Wait before trying again.");
        if (!response.IsSuccessStatusCode) throw new UserError("Google could not read the verification email. Try again later.");
        return await GoogleOAuth.ReadJson(response, 256000, cancel);
    }
    private string Digest(GmailConnection connection, RobloxMail mail) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(connection.Id + ":" + mail.MessageId)));
    public void Consume(GmailConnection connection, RobloxMail mail)
    {
        if (!IsConnected(connection)) throw new UserError("This Gmail mailbox was disconnected.");
        string digest = Digest(connection, mail);
        if (state.Gmail.UsedMessages.Contains(digest)) throw new UserError("This verification email has already been used by the manager.");
        var previous = state.Gmail.UsedMessages;
        state.Gmail.UsedMessages = previous.Append(digest).TakeLast(256).ToList();
        try { save(); } catch { state.Gmail.UsedMessages = previous; throw; }
    }
    public async Task<RobloxMail?> Find(GmailConnection connection, string username, string recipient, RobloxMailPurpose purpose, DateTimeOffset since, CancellationToken cancel)
    {
        recipient = Recipient(connection, recipient);
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[A-Za-z0-9_]{3,20}$")) throw new UserError("A Roblox username is required to match verification mail.");
        string access = await Access(connection, cancel);
        string sender = purpose == RobloxMailPurpose.LoginCode ? "from:no-reply@roblox.com" : "{from:email_validate@roblox.com from:no-reply@roblox.com}";
        string query = sender + " after:" + since.ToUnixTimeSeconds() + " to:" + recipient + " \"" + username + "\"";
        using var list = await Get("messages?maxResults=10&q=" + Uri.EscapeDataString(query), access, cancel);
        if (!list.RootElement.TryGetProperty("messages", out var messages)) return null;
        foreach (var item in messages.EnumerateArray().Take(10))
        {
            string? id = item.GetProperty("id").GetString();
            if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,128}$")) continue;
            using var message = await Get("messages/" + id + "?format=full", access, cancel);
            var found = RobloxMailParser.Parse(message.RootElement, username, recipient, purpose, since, now());
            if (!IsConnected(connection)) throw new UserError("This Gmail mailbox was disconnected.");
            if (found is not null && !state.Gmail.UsedMessages.Contains(Digest(connection, found))) return found;
        }
        return null;
    }
    public async Task<RobloxMail> Wait(GmailConnection connection, string username, string recipient, RobloxMailPurpose purpose, DateTimeOffset since, TimeSpan timeout, CancellationToken cancel)
    {
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(5)) throw new UserError("Choose an email wait of 1–300 seconds.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var found = await Find(connection, username, recipient, purpose, since, deadline.Token);
                if (found is not null) return found;
                await delay(TimeSpan.FromSeconds(5), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new UserError("No matching fresh Roblox verification email arrived. Complete verification manually or request a new email in Roblox."); }
    }
    public void Dispose() { client.Dispose(); tokens.Clear(); }
}
