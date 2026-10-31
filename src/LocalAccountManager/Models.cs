using System.Text.Json.Serialization;

namespace LocalAccountManager;

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserId { get; set; }
    public string Username { get; set; } = "New account";
    public string DisplayName { get; set; } = "Sign in to connect";
    public string Alias { get; set; } = "";
    public string Group { get; set; } = "Default";
    public string Notes { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    public string SavedTarget { get; set; } = "";
    public string SavedJob { get; set; } = "";
    public string SavedPassword { get; set; } = "";
    public string SessionCookie { get; set; } = "";
    public bool SessionNeedsRestore { get; set; }
    public string AgeGroup { get; set; } = "Unknown";
    public bool? IsAgeChecked { get; set; }
    public DateTimeOffset? AgeGroupCheckedAt { get; set; }
    [JsonIgnore] public string AgeGroupCheckStatus { get; set; } = "Status not refreshed this session";
    [JsonIgnore] public string AgeGroupLabel => "Age group: " + (IsAgeChecked == false || AgeGroup == "Unchecked" ? "Unchecked" : IsAgeChecked == true ? AgeGroup : "Unknown");
    [JsonIgnore] public string AgeGroupDetails => AgeGroupLabel + "\nAge check: " + (IsAgeChecked switch { true => "Completed", false => "Unchecked", null => "Unknown" }) + "\n" + AgeGroupCheckStatus +
        (AgeGroupCheckedAt is { } time ? "\nLast successful check: " + time.ToLocalTime().ToString("f") : "") +
        "\nReported by Roblox. Check status refreshes it. Failed checks keep the last confirmed status.";
    public ModerationRestriction? Restriction { get; set; }
    public DateTimeOffset? ModerationCheckedAt { get; set; }
    [JsonIgnore] public string ModerationCheckStatus { get; set; } = "Not checked this session";
    [JsonIgnore] public bool HasRestriction => Restriction is not null && !Restriction.IsWarning;
    [JsonIgnore] public string ModerationLabel => Restriction?.Label(DateTimeOffset.UtcNow) ?? (ModerationCheckedAt is null ? "Ban not checked" : "No active ban");
    [JsonIgnore] public string ModerationTiming => Restriction?.Timing ?? "";
    [JsonIgnore] public string ModerationColor => Restriction is null ? "#A9B3C5" : Restriction.IsWarning || Restriction.HasExpired(DateTimeOffset.UtcNow) ? "#F6C977" : "#FF927D";
    [JsonIgnore] public string ModerationDetails => ModerationLabel + "\n" +
        (Restriction is { } restriction ? (restriction.Began is { } start ? "\nStarted: " + start.ToLocalTime().ToString("f") : "\nStart date unavailable") +
            (restriction.Ends is { } end ? "\nEnds: " + end.ToLocalTime().ToString("f") : "\nRoblox did not provide an end date. Duration is unknown.") +
            (restriction.Began is { } began && restriction.Ends is { } ends && ends > began ? "\nDuration: " + ModerationRestriction.Duration(ends - began) : "") +
            (restriction.HasExpired(DateTimeOffset.UtcNow) ? "\nThe end time has passed. Open the browser to review/reactivate; this does not confirm the account is unbanned." : "") +
            (restriction.Reason.Length > 0 ? "\n\nRoblox message:\n" + restriction.Reason : "") + "\n" : "") +
        "\n" + ModerationCheckStatus + (ModerationCheckedAt is { } time ? "\nLast successful check: " + time.ToLocalTime().ToString("f") : "") +
        "\n\nChecks run at startup and every 5 minutes while the manager is open. Check status refreshes selected accounts. A failed check keeps the last known result.";
    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias;
    [JsonIgnore] public string Initial => Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";
    [JsonIgnore] public string Handle => UserId > 0 ? "@" + Username : "Login required";
    [JsonIgnore] public string LastUsedLabel => LastUsed?.ToLocalTime().ToString("MMM d, HH:mm") ?? "Never launched";
    [JsonIgnore] public string Star => Favorite ? "★" : "";
    [JsonIgnore] public string SessionStatus { get; set; } = "Not checked";
    [JsonIgnore] public DateTimeOffset? SessionCheckedAt { get; set; }
    [JsonIgnore] public string SessionCheckLabel => SessionCheckedAt is { } time ? "Last checked " + time.ToLocalTime().ToString("MMM d, HH:mm:ss") : "This session has not been checked yet.";
    [JsonIgnore] public string SessionColor => SessionStatus.StartsWith("Valid") ? "#C3F27D" :
        SessionStatus.Contains("Expired") || SessionStatus.Contains("Wrong account") ? "#FF927D" : "#A9B3C5";
    [JsonIgnore] public string CredentialLabel => SavedPassword.Length > 0 ? "Password saved" : "No saved password";
    [JsonIgnore] public string NotePreview => Notes.Replace('\r', ' ').Replace('\n', ' ');
}

public sealed class GameBookmark
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Job { get; set; } = "";
}

public sealed class AppState
{
    public int Version { get; set; } = 2;
    public VaultAuthentication Authentication { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public List<Account> Accounts { get; set; } = [];
    public List<GameBookmark> Favorites { get; set; } = [];
    public List<GameBookmark> Recent { get; set; } = [];
}

public sealed class AppSettings
{
    public bool AutomaticUpdates { get; set; }
}

public sealed class VaultAuthentication
{
    public bool Enabled { get; set; }
    public Guid Id { get; set; }
    public string Secret { get; set; } = "";
    public List<string> RecoveryHashes { get; set; } = [];
    public long LastAcceptedStep { get; set; } = -1;
    public override string ToString() => "VaultAuthentication [redacted]";
}

public sealed record RobloxIdentity(long Id, string Name, string DisplayName);
public sealed record GameServer(string Id, int Playing, int MaxPlayers, int? Ping, double? Fps);
public sealed record ServerPage(List<GameServer> Servers, string? NextCursor);

public class UserError(string message) : Exception(message);
public sealed class SecondFactorRequiredError() : UserError("Enter your authenticator code or an unused recovery code.");
public sealed class RobloxApiError(int statusCode, string message) : UserError(message)
{
    public int StatusCode { get; } = statusCode;
}
public sealed class MissingSessionError() : UserError("No saved session was found. Click Sign in to reconnect this account.");
