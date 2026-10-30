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
    public List<Account> Accounts { get; set; } = [];
    public List<GameBookmark> Favorites { get; set; } = [];
    public List<GameBookmark> Recent { get; set; } = [];
}

public sealed record RobloxIdentity(long Id, string Name, string DisplayName);
public sealed record GameServer(string Id, int Playing, int MaxPlayers, int? Ping, double? Fps);
public sealed record ServerPage(List<GameServer> Servers, string? NextCursor);

public class UserError(string message) : Exception(message);
public sealed class RobloxApiError(int statusCode, string message) : UserError(message)
{
    public int StatusCode { get; } = statusCode;
}
public sealed class MissingSessionError() : UserError("No saved session was found. Click Sign in to reconnect this account.");
