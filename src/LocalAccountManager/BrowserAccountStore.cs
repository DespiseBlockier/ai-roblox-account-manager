using System.Text.RegularExpressions;

namespace LocalAccountManager;

public sealed class AddAccountRequest
{
    public int Id { get; set; }
    public string? Password { get; set; }
    public override string ToString() => "AddAccountRequest [redacted]";
}

public static class BrowserAccountStore
{
    public static string? NormalizeGroup(string? group)
    {
        if (group is null) return null;
        if (group.Length > 80 || group.Any(char.IsControl)) throw new UserError("The manager group must be at most 80 characters without control characters.");
        return group.Trim().Length == 0 ? "Default" : group.Trim();
    }
    public static void ValidatePassword(string? password)
    {
        if (password is not null && (password.Length is < 1 or > 1024 || password.Any(char.IsControl)))
            throw new UserError("If supplied, the saved password must be 1–1024 characters without control characters.");
    }
    // The caller must obtain this identity from Roblox using the exact session cookie being saved.
    public static Account SaveVerified(AppState state, RobloxIdentity identity, string cookie, string? password, Action save, string? group = null)
    {
        ValidatePassword(password);
        group = NormalizeGroup(group);
        if (identity.Id <= 0 || !Regex.IsMatch(identity.Name, "^[A-Za-z0-9_]{1,20}$") || cookie.Length is < 1 or > 20000 || cookie.Any(c => char.IsControl(c) || c == ';'))
            throw new UserError("A verified Roblox session is required before adding an account.");
        var account = state.Accounts.SingleOrDefault(a => a.UserId == identity.Id);
        bool created = account is null; account ??= new Account();
        var old = (account.UserId, account.Username, account.DisplayName, account.SessionCookie, account.SavedPassword, account.SessionNeedsRestore, account.Group);
        account.UserId = identity.Id; account.Username = identity.Name; account.DisplayName = identity.DisplayName;
        account.SessionCookie = cookie; account.SessionNeedsRestore = true;
        if (password is not null) account.SavedPassword = password;
        if (group is not null) account.Group = group;
        if (created) state.Accounts.Add(account);
        try { save(); }
        catch
        {
            if (created) state.Accounts.Remove(account);
            (account.UserId, account.Username, account.DisplayName, account.SessionCookie, account.SavedPassword, account.SessionNeedsRestore, account.Group) = old;
            throw;
        }
        account.SessionStatus = "Valid session"; account.SessionCheckedAt = DateTimeOffset.UtcNow;
        return account;
    }
}
