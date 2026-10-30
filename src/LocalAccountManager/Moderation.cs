using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAccountManager;

// Cached evidence from Roblox, not a prediction based on failed authentication.
public sealed record ModerationRestriction(DateTimeOffset? Began, DateTimeOffset? Ends, string Reason)
{
    [JsonIgnore] public bool IsWarning => Began is { } start && Ends is { } end && end == start;
    public bool HasExpired(DateTimeOffset now) => !IsWarning && Ends is { } end && end <= now;
    public string Label(DateTimeOffset now) => IsWarning ? "Warning · open browser" : HasExpired(now) ? "Expired · reactivate" :
        Began is { } start && Ends is { } end ? "Banned · " + Duration(end - start) : "Restricted · duration unknown";
    [JsonIgnore] public string Timing => Ends is { } end && !IsWarning ? (HasExpired(DateTimeOffset.UtcNow) ? "Ended " : "Ends ") + end.ToLocalTime().ToString("MMM d, HH:mm") : "Open browser for details";

    public static string Duration(TimeSpan value)
    {
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays} day{((int)value.TotalDays == 1 ? "" : "s")}" + (value.Hours > 0 ? $" {value.Hours}h" : "");
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours} hour{((int)value.TotalHours == 1 ? "" : "s")}" + (value.Minutes > 0 ? $" {value.Minutes}m" : "");
        return $"{Math.Max(1, (int)Math.Ceiling(value.TotalMinutes))} min";
    }
}

public static class Moderation
{
    public static ModerationRestriction? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("errors", out _)) throw InvalidResponse();
        if (!root.TryGetProperty("interventionId", out var intervention) || intervention.ValueKind == JsonValueKind.Null) return null;
        if (!((intervention.ValueKind == JsonValueKind.Number && intervention.TryGetInt64(out long id) && id > 0) ||
            (intervention.ValueKind == JsonValueKind.String && intervention.GetString() is { Length: > 0 and <= 200 }))) throw InvalidResponse();
        var began = Date(root, "beginDate");
        var ends = Date(root, "endDate");
        if (began is { } start && ends is { } end && end < start) throw InvalidResponse();
        string reason = "";
        if (root.TryGetProperty("messageToUser", out var message) && message.ValueKind != JsonValueKind.Null)
        {
            if (message.ValueKind != JsonValueKind.String || message.GetString() is not { Length: <= 4000 } text || text.Contains('\0')) throw InvalidResponse();
            reason = text;
        }
        return new(began, ends, reason);
    }

    private static DateTimeOffset? Date(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 100 } text ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) throw InvalidResponse();
        return date;
    }
    private static UserError InvalidResponse() => new("Roblox returned an unexpected moderation response. Open the account browser for details.");

    public static void Apply(Account account, ModerationRestriction? restriction)
    {
        account.Restriction = restriction;
        account.ModerationCheckedAt = DateTimeOffset.UtcNow;
        account.ModerationCheckStatus = "Checked";
    }
    public static void Failure(Account account, Exception error) => account.ModerationCheckStatus = error switch
    {
        RobloxApiError { StatusCode: 401 } or MissingSessionError => "Ban check needs sign-in",
        RobloxApiError { StatusCode: 429 } => "Ban check rate limited · retry later",
        _ => "Ban check failed · retry"
    };
}
