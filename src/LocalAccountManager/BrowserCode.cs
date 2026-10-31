using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public static class GroupTarget
{
    public const long MaxScriptId = 9_007_199_254_740_991;
    public static long Parse(string value)
    {
        value = value.Trim();
        if (value.Any(char.IsControl) || value.Length > 4096) throw new UserError("Enter a group/community ID or a Roblox community link.");
        if (GameTarget.TryId(value, out long id)) return Validate(id);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !Security.IsRobloxWebsite(uri))
            throw new UserError("Enter a group/community ID or an HTTPS Roblox community link.");
        var match = Regex.Match(uri.AbsolutePath, @"^/(?:groups|communities)/([0-9]+)(?:/[^/]*)?/?$", RegexOptions.CultureInvariant);
        if (!match.Success || !GameTarget.TryId(match.Groups[1].Value, out id)) throw new UserError("That link does not identify a Roblox group/community.");
        return Validate(id);
    }
    public static long Validate(long id) => id > 0 && id <= MaxScriptId ? id : throw new UserError("Invalid group/community ID.");
    public static string Url(long id) => "https://www.roblox.com/communities/" + Validate(id).ToString(CultureInfo.InvariantCulture);
}

public sealed record BrowserCodeRequest(string Url, string Code, TimeSpan Timeout, long? ConfirmGroupId = null, bool Guest = false)
{
    public BrowserCodeRequest Validate()
    {
        if (!Security.IsBrowserPage(Url) || Url.Length > 4096 || Url.Any(char.IsControl) || !Guest && LoginAutofill.IsLoginPage(Url))
            throw new UserError("Choose a Roblox HTTPS starting page. Login-page scripts require a guest browser.");
        if (string.IsNullOrWhiteSpace(Code) || Code.Length > 128_000 || Code.Contains('\0')) throw new UserError("Enter JavaScript code (up to 128,000 characters).");
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromHours(2)) throw new UserError("Choose a timeout of up to 120 minutes per account.");
        if (ConfirmGroupId is long group) GroupTarget.Validate(group);
        if (Guest && ConfirmGroupId is not null) throw new UserError("Select a saved account to confirm group membership.");
        return this with { Url = new Uri(Url).AbsoluteUri };
    }
}

public enum BrowserCodeOutcome { Completed, Stopped, Failed }
public sealed record BrowserCodeResult(BrowserCodeOutcome Outcome, string Message);
public sealed record BrowserCodeState(string Phase, string Message, List<SolverRequest>? Requests = null, List<AddAccountRequest>? Accounts = null, List<SignupRequest>? Signups = null);

public static class BrowserCodeRuntime
{
    public static string NewKey() => "__ram_run_" + Guid.NewGuid().ToString("N");
    private static string Key(string key)
    {
        if (!Regex.IsMatch(key, @"^__ram_run_[a-f0-9]{32}$")) throw new UserError("Invalid browser-code run identifier.");
        return JsonSerializer.Serialize(key);
    }

    public static string Install(string key, Account account, string code, bool guest = false)
    {
        // Only account identity is passed to JavaScript. Never include passwords, cookies, or native objects.
        string identity = guest ? "null" : JsonSerializer.Serialize(new { userId = account.UserId.ToString(CultureInfo.InvariantCulture), username = account.Username });
        return """
            (() => {
              if (location.protocol !== 'https:' || !['www.roblox.com','roblox.com'].includes(location.hostname) ||
                  (location.port && location.port !== '443')) return null;
            """ + "\nif (" + (guest ? "false" : "true") + " && location.pathname.replace(/\\/$/, '').toLowerCase() === '/login') return null;\nconst key = " + Key(key) + ";\nconst identity = " + identity + ";\n" + """
              const abort = new AbortController();
              let phase = 'running', message = 'Code is running.';
              const text = value => String(value ?? '').slice(0, 2000);
              const finish = (next, value) => {
                if (phase === 'done' || phase === 'stopped' || phase === 'failed') return;
                phase = next; message = text(value);
              };
            """ + "\n" + BrowserSolverRuntime.Source + "\n" + """
              const ram = Object.freeze({
                addAccount: requestAddAccount, createAccount: requestCreateAccount,
                account: identity === null ? null : Object.freeze(identity), signal: abort.signal,
                twocaptcha: Object.freeze({register: (key, options) => registerSolver('2captcha', key, options)}),
                anticaptcha: Object.freeze({register: (key, options) => registerSolver('anticaptcha', key, options)}),
                solvex: Object.freeze({register: (key, options) => registerSolver('solvex', key, options)}),
                fetchcaptcha: fetchCaptcha, featchcaptcha: fetchCaptcha,
                status: value => { if (phase === 'running' || phase === 'waiting') message = text(value); },
                done: () => { finish('done', 'Code requested completion.'); abort.abort(); },
                stop: value => { finish('stopped', value || 'Code stopped the queue.'); abort.abort(); },
                sleep: ms => new Promise((resolve, reject) => {
                  if (abort.signal.aborted) { reject(new Error('Stopped')); return; }
                  const stop = () => { clearTimeout(timer); reject(new Error('Stopped')); };
                  const timer = setTimeout(() => { abort.signal.removeEventListener('abort', stop); resolve(); }, Math.max(0, Math.min(Number(ms) || 0, 300000)));
                  abort.signal.addEventListener('abort', stop, {once:true});
                })
              });
              Object.defineProperty(window, key, {configurable:true, enumerable:false, value:Object.freeze({
                snapshot: () => ({phase, message, requests:solverOutbox.splice(0,4), accounts:accountOutbox.splice(0,1), signups:signupOutbox.splice(0,1)}),
                reply: (id, result) => { const call = solverPending.get(id); if (call) { solverPending.delete(id); call.resolve(result); } },
                replySignup: (id, result) => { const call = signupPending.get(id); if (call) { signupPending.delete(id); result.success ? call.resolve(result) : call.reject(new Error(result.error || 'Could not create the account.')); } },
                replyAccount: (id, result) => { const call = accountPending.get(id); if (call) { accountPending.delete(id); result.success ? call.resolve(result) : call.reject(new Error(result.error || 'Could not save the current account.')); } },
                stop: () => { abort.abort(); finish('stopped', 'Stopped'); }
              })});
              setTimeout(async () => {
                try {
                  await (async function(ram) {
            """ + "\n" + code + "\n" + """
                  })(ram);
                  if (phase === 'running') { phase = 'waiting'; message = 'Code returned. Call ram.done() to close this account and advance.'; }
                } catch (error) {
                  if (!abort.signal.aborted) finish('failed', error && error.message ? error.message : 'JavaScript failed.');
                }
              }, 0);
              return {installed:true};
            })();
            """;
    }

    public static string Read(string key) => "(() => { const run = window[" + Key(key) + "]; return run ? run.snapshot() : null; })();";
    public static string Stop(string key) => "(() => { const run = window[" + Key(key) + "]; if (run) run.stop(); })();";
    public static string Reply(string key, int id, object response)
    {
        if (id < 1) throw new UserError("Invalid solver request identifier.");
        return "window[" + Key(key) + "]?.reply(" + id.ToString(CultureInfo.InvariantCulture) + "," + JsonSerializer.Serialize(response) + ");";
    }
    public static string ReplyAccount(string key, int id, object response)
    {
        if (id < 1) throw new UserError("Invalid account request identifier.");
        return "window[" + Key(key) + "]?.replyAccount(" + id.ToString(CultureInfo.InvariantCulture) + "," + JsonSerializer.Serialize(response) + ");";
    }
    public static string ReplySignup(string key, int id, object response)
    {
        if (id != 1) throw new UserError("Invalid signup identifier.");
        return "window[" + Key(key) + "]?.replySignup(1," + JsonSerializer.Serialize(response) + ");";
    }
    public static BrowserCodeState Parse(string json)
    {
        if (json.Length > 280_000) throw new UserError("Browser code returned too much status data.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new UserError("The page changed while code was running. The queue stopped without replaying code.");
            string phase = doc.RootElement.GetProperty("phase").GetString() ?? "";
            if (phase is not ("running" or "waiting" or "done" or "stopped" or "failed")) throw new UserError("Invalid browser-code status.");
            string message = doc.RootElement.GetProperty("message").GetString() ?? "";
            if (message.Length > 2000) message = message[..2000];
            List<SolverRequest>? requests = null;
            if (doc.RootElement.TryGetProperty("requests", out var pending))
            {
                if (pending.ValueKind != JsonValueKind.Array || pending.GetArrayLength() > 4) throw new UserError("Too many solver requests.");
                requests = JsonSerializer.Deserialize<List<SolverRequest>>(pending.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (requests?.Any(r => r is null || r.Id < 1) == true) throw new UserError("Invalid solver request.");
            }
            List<AddAccountRequest>? accounts = null;
            if (doc.RootElement.TryGetProperty("accounts", out var additions))
            {
                if (additions.ValueKind != JsonValueKind.Array || additions.GetArrayLength() > 1) throw new UserError("Only one account save can run at a time.");
                accounts = JsonSerializer.Deserialize<List<AddAccountRequest>>(additions.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                foreach (var addition in accounts ?? []) { if (addition is null || addition.Id < 1) throw new UserError("Invalid account request."); BrowserAccountStore.ValidatePassword(addition.Password); }
            }
            List<SignupRequest>? signups = null;
            if (doc.RootElement.TryGetProperty("signups", out var registrations))
            {
                if (registrations.ValueKind != JsonValueKind.Array || registrations.GetArrayLength() > 1 || registrations.GetRawText().Length > 12000) throw new UserError("Only one signup can run at a time.");
                signups = JsonSerializer.Deserialize<List<SignupRequest>>(registrations.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                foreach (var signup in signups ?? []) { if (signup is null || signup.Id != 1) throw new UserError("Invalid signup request."); }
            }
            return new(phase, message, requests, accounts, signups);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new UserError("Could not read browser-code status. The queue stopped."); }
    }

    public static string JoinGroupExample(long groupId)
    {
        GroupTarget.Validate(groupId);
        using var stream = typeof(BrowserCodeRuntime).Assembly.GetManifestResourceStream("LocalAccountManager.JoinGroup.js") ?? throw new UserError("The built-in group example is missing.");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd().Replace("/* GROUP_ID */ 12345678", groupId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    public const string Starter = "// Runs once for each selected account.\nram.status('Opened @' + ram.account.username);\nawait ram.sleep(1000);\nram.done(); // Close this browser and move to the next account.\n";
    public const string GuestStarter = "// Runs once in a fresh private browser. ram.account is null.\nram.status('Guest browser ready');\nawait ram.sleep(1000);\nram.done(); // Close the guest browser.\n";
}

public sealed class BrowserCodeQueue
{
    public bool IsRunning { get; private set; }
    public async Task<List<BrowserCodeResult>> Run(IReadOnlyList<Account> accounts,
        Func<Account, CancellationToken, Task<BrowserCodeResult>> runOne, CancellationToken cancellationToken)
    {
        if (IsRunning) throw new UserError("A browser-code queue is already running.");
        IsRunning = true;
        var results = new List<BrowserCodeResult>();
        try
        {
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await runOne(account, cancellationToken);
                results.Add(result);
                if (result.Outcome != BrowserCodeOutcome.Completed) break;
                if (results.Count < accounts.Count) await Task.Delay(1500, cancellationToken);
            }
            return results;
        }
        finally { IsRunning = false; }
    }
}
