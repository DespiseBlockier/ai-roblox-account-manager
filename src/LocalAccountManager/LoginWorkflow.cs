using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

// Deliberately redacted ToString: these objects must never leak into diagnostic output.
public sealed class LoginCredential(string username, string password, string? group = null, string? notes = null) : IDisposable
{
    public string Username { get; } = username;
    public string Password { get; private set; } = password;
    public string? Group { get; } = group;
    public string? Notes { get; private set; } = notes;
    public override string ToString() => "LoginCredential [redacted]";
    public void Dispose() { Password = ""; Notes = null; }
}

public static class LoginInput
{
    public static LoginCredential Create(string username, string password, string? group = null, string? notes = null)
    {
        username = username.Trim(); group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        if (!Regex.IsMatch(username, @"^[a-zA-Z0-9_]{1,20}$") || password.Length is < 1 or > 1024 || password.Any(char.IsControl))
            throw new UserError("Enter a Roblox username and a password of 1–1024 characters.");
        if (group is not null && (group.Length > 80 || group.Any(char.IsControl))) throw new UserError("Local group names must be at most 80 characters without control characters.");
        if (notes is not null && (notes.Length > 4000 || notes.Contains('\0'))) throw new UserError("Notes must be at most 4,000 characters without null characters.");
        return new(username, password, group, notes);
    }

    public static List<LoginCredential> Parse(string text, bool groupColumn = true)
    {
        if (text.Length > 512_000) throw new UserError("The login list is too large. Add at most 500 accounts at a time.");
        var result = new List<LoginCredential>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                int split = line.IndexOf(';');
                if (split < 0) throw new UserError($"Line {i + 1}: use username;password;group;\"Note\" (group and note optional).");
                LoginCredential credential;
                try
                {
                    var fields = groupColumn ? Fields(line) : [line[..split], line[(split + 1)..]];
                    if (fields.Count is < 2 or > 4) throw new UserError("Use two to four fields; quote values containing semicolons, or select the legacy password format.");
                    credential = Create(fields[0], fields[1], fields.Count >= 3 ? fields[2] : null, fields.Count == 4 ? fields[3] : null);
                }
                catch (UserError ex) { throw new UserError($"Line {i + 1}: {ex.Message}"); }
                if (!names.Add(credential.Username)) { credential.Dispose(); throw new UserError($"Line {i + 1}: this username is listed more than once."); }
                result.Add(credential);
                if (result.Count > 500) throw new UserError("Add at most 500 accounts at a time.");
            }
            if (result.Count == 0) throw new UserError("Enter at least one username;password line.");
            return result;
        }
        catch { foreach (var credential in result) credential.Dispose(); throw; }
    }

    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false, closed = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else { quoted = false; closed = true; }
            }
            else if (c == ';') { fields.Add(field.ToString()); field.Clear(); closed = false; }
            else if (closed) throw new UserError("A quoted field must end at the separator or end of line.");
            else if (c == '"' && field.Length == 0) quoted = true;
            else field.Append(c);
        }
        if (quoted) throw new UserError("A quoted field is missing its closing quote.");
        fields.Add(field.ToString());
        return fields;
    }
}

// Only this exact top-level page can receive credentials. Both native code and JS check it.
public static class LoginAutofill
{
    public static bool CanSubmit(LoginCredential? credential) => credential is not null && credential.Password.Length > 0;
    public static string NewAttemptKey() => "__ram_login_" + Guid.NewGuid().ToString("N");
    public static bool IsLoginPage(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        Security.IsRobloxWebsite(uri) && uri.IdnHost.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.TrimEnd('/').Equals("/login", StringComparison.OrdinalIgnoreCase);

    public static string Script(string username, string password) => """
        (() => {
          if (location.protocol !== 'https:' || location.hostname !== 'www.roblox.com' ||
              (location.port && location.port !== '443') || location.pathname.replace(/\/$/,'').toLowerCase() !== '/login') return false;
          const user = document.querySelector('#login-username');
          const pass = document.querySelector('#login-password');
          if (!(user instanceof HTMLInputElement) || !(pass instanceof HTMLInputElement) || pass.type !== 'password') return false;
          const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
        """ + "\nconst values = " + JsonSerializer.Serialize(new[] { username, password }) + ";\n" + """
          [user, pass].forEach((field, i) => {
            setter.call(field, values[i]);
            field.dispatchEvent(new Event('input', { bubbles: true }));
            field.dispatchEvent(new Event('change', { bubbles: true }));
          });
          return true;
        })();
        """;

    public static string SubmitScript(string key, string username, string password)
    {
        if (!Regex.IsMatch(key, @"^__ram_login_[a-f0-9]{32}$")) throw new UserError("Invalid login attempt identifier.");
        return """
            (() => {
              if (location.protocol !== 'https:' || location.hostname !== 'www.roblox.com' ||
                  (location.port && location.port !== '443') || location.pathname.replace(/\/$/,'').toLowerCase() !== '/login') return false;
            """ + "\nconst key = " + JsonSerializer.Serialize(key) + ";\nconst values = " + JsonSerializer.Serialize(new[] { username, password }) + ";\n" + """
              let attempt = window[key];
              if (attempt && attempt.submitted) return true;
              const user = document.querySelector('#login-username');
              const pass = document.querySelector('#login-password');
              const button = document.querySelector('#login-button');
              if (!(user instanceof HTMLInputElement) || !(pass instanceof HTMLInputElement) || pass.type !== 'password' ||
                  !(button instanceof HTMLButtonElement)) return false;
              if (!attempt) {
                attempt = {submitted:false, button:null};
                Object.defineProperty(window, key, {value:attempt, configurable:true, enumerable:false});
              }
              if (attempt.button !== button) {
                attempt.button = button;
                button.addEventListener('click', () => { attempt.submitted = true; }, {capture:true, once:true});
              }
              if (!values[1] || user.value !== values[0] || pass.value !== values[1] || button.disabled ||
                  button.getAttribute('aria-disabled') === 'true' || !button.getClientRects().length ||
                  getComputedStyle(button).visibility === 'hidden') return false;
              attempt.submitted = true;
              button.click();
              return true;
            })();
            """;
    }
}

// Staging is independent of the saved account list. Failed/canceled logins never create rows.
public sealed class AccountEnrollment(AppState state, Action save)
{
    public Account? Current { get; private set; }
    public LoginCredential? Credential { get; private set; }
    public bool Committed { get; private set; }

    public Account Begin(LoginCredential? credential = null)
    {
        if (Current is not null) throw new UserError("Finish or close the current sign-in browser first.");
        Credential = credential; Committed = false;
        Current = credential is null ? new Account() : state.Accounts.FirstOrDefault(a => a.Username.Equals(credential.Username, StringComparison.OrdinalIgnoreCase)) ??
            new Account { Username = credential.Username, DisplayName = credential.Username };
        return Current;
    }

    public void Commit(Account account, RobloxIdentity identity, string cookie, bool saveCredential = true)
    {
        bool staged = ReferenceEquals(Current, account);
        bool saved = state.Accounts.Contains(account);
        if (!staged && !saved) throw new UserError("This account entry is no longer active.");
        if (account.UserId > 0 && account.UserId != identity.Id) throw new UserError("Wrong account. Sign in to @" + account.Username + ".");
        if (staged && Credential is not null && !Credential.Username.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
            throw new UserError("The signed-in username does not match the queued account. Sign in to @" + Credential.Username + ".");
        if (state.Accounts.Any(a => a.Id != account.Id && a.UserId == identity.Id)) throw new UserError("This Roblox account is already in the account list.");
        var old = (account.UserId, account.Username, account.DisplayName, account.SavedPassword, account.SessionCookie, account.Group, account.Notes);
        account.UserId = identity.Id; account.Username = identity.Name; account.DisplayName = identity.DisplayName;
        account.SessionCookie = cookie;
        if (staged && Credential is not null && saveCredential)
        {
            account.SavedPassword = Credential.Password;
            if (Credential.Group is not null) account.Group = Credential.Group;
            if (Credential.Notes is not null) account.Notes = Credential.Notes;
        }
        if (!saved) state.Accounts.Add(account);
        try { save(); }
        catch
        {
            if (!saved) state.Accounts.Remove(account);
            (account.UserId, account.Username, account.DisplayName, account.SavedPassword, account.SessionCookie, account.Group, account.Notes) = old;
            throw;
        }
        account.SessionStatus = "Valid session"; account.SessionCheckedAt = DateTimeOffset.UtcNow;
        if (staged) Committed = true;
    }

    public void Finish() { Current = null; Credential = null; Committed = false; }

    public static int RemoveLegacyPlaceholders(AppState state) => state.Accounts.RemoveAll(a => a.UserId == 0 &&
        a.Username == "New account" && a.DisplayName == "Sign in to connect" && a.Alias.Length == 0 && a.Notes.Length == 0 &&
        a.SavedPassword.Length == 0 && a.SessionCookie.Length == 0);
}

public enum LoginOutcome { Connected, Skipped, Stopped }

public sealed class LoginQueue
{
    public bool IsRunning { get; private set; }
    public int Completed { get; private set; }
    public int Skipped { get; private set; }
    public async Task Run(IReadOnlyList<LoginCredential?> entries, Func<LoginCredential?, int, Task<LoginOutcome>> login)
    {
        if (IsRunning) throw new UserError("A sign-in queue is already running.");
        IsRunning = true; Completed = 0; Skipped = 0;
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var outcome = await login(entries[i], i);
                entries[i]?.Dispose();
                if (outcome == LoginOutcome.Stopped) break;
                if (outcome == LoginOutcome.Connected) Completed++;
                else if (outcome == LoginOutcome.Skipped) Skipped++;
            }
        }
        finally { foreach (var entry in entries) entry?.Dispose(); IsRunning = false; }
    }
}

public static class SessionHealth
{
    public static void Failure(Account account, Exception error)
    {
        account.SessionCheckedAt = DateTimeOffset.UtcNow;
        account.SessionStatus = error switch
        {
            RobloxApiError { StatusCode: 401 } => "Expired · sign in",
            RobloxApiError { StatusCode: 403 } => "Verification needed",
            MissingSessionError => "No session · sign in",
            _ => "Check failed · retry"
        };
    }
}
