using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAccountManager;

public enum ModerationKind { Legacy, Warning, AccountVerification, PermanentBan }
internal enum RestrictionSource { Moderation = 1, AccountLock = 5 }

// Cached evidence from Roblox, not a prediction based on failed authentication.
public sealed record ModerationRestriction(DateTimeOffset? Began, DateTimeOffset? Ends, string Reason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public ModerationKind Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool IsBrowserWarning { get; init; }
    [JsonIgnore] public bool RequiresVerification => Kind == ModerationKind.AccountVerification;
    [JsonIgnore] public bool IsPermanentBan => Kind == ModerationKind.PermanentBan;
    [JsonIgnore] public bool IsWarning => Kind == ModerationKind.Warning || Kind == ModerationKind.Legacy &&
        (IsBrowserWarning || Began is { } start && Ends is { } end && end == start);
    public bool HasExpired(DateTimeOffset now) => !IsWarning && !RequiresVerification && !IsPermanentBan && Ends is { } end && end <= now;
    public string Label(DateTimeOffset now) => RequiresVerification ? "Verify account" : IsWarning ? "Warning" : IsPermanentBan ? "Banned · permanent" : HasExpired(now) ? "Expired · reactivate" :
        Began is { } start && Ends is { } end ? "Banned · " + Duration(end - start) : "Restricted · duration unknown";
    [JsonIgnore] public string Timing => Ends is { } end && !IsWarning && !RequiresVerification && !IsPermanentBan ? (HasExpired(DateTimeOffset.UtcNow) ? "Ended " : "Ends ") + end.ToLocalTime().ToString("MMM d, HH:mm") : "Open browser for details";

    public static string Duration(TimeSpan value)
    {
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays} day{((int)value.TotalDays == 1 ? "" : "s")}" + (value.Hours > 0 ? $" {value.Hours}h" : "");
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours} hour{((int)value.TotalHours == 1 ? "" : "s")}" + (value.Minutes > 0 ? $" {value.Minutes}m" : "");
        return $"{Math.Max(1, (int)Math.Ceiling(value.TotalMinutes))} min";
    }
}

public static class Moderation
{
    public static ModerationRestriction? Parse(JsonElement root, long? expectedUserId = null)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("errors", out _)) throw InvalidResponse();
        long punishedUserId = 0;
        if (root.TryGetProperty("punishedUserId", out var user) && user.ValueKind != JsonValueKind.Null)
        {
            bool valid = user.ValueKind switch
            {
                JsonValueKind.Number => user.TryGetInt64(out punishedUserId),
                JsonValueKind.String => long.TryParse(user.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out punishedUserId),
                _ => false
            };
            if (!valid || punishedUserId < 0) throw InvalidResponse();
            if (punishedUserId > 0 && expectedUserId is long expected && punishedUserId != expected)
                throw new UserError("Roblox returned moderation for a different account.");
        }
        string? punishment = root.TryGetProperty("punishmentTypeDescription", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        bool warning = punishment == "Warn";
        bool permanent = punishment == "Delete";
        bool typedRestriction = warning || permanent || punishment?.StartsWith("Ban ", StringComparison.Ordinal) == true;
        // Roblox's current moderation page uses the punishment type and affected user, even without a legacy intervention ID.
        if (!typedRestriction || punishedUserId == 0)
        {
            if (!root.TryGetProperty("interventionId", out var intervention) || intervention.ValueKind == JsonValueKind.Null)
            {
                if (typedRestriction) throw InvalidResponse();
                return null;
            }
            if (!((intervention.ValueKind == JsonValueKind.Number && intervention.TryGetInt64(out long id) && id > 0) ||
                (intervention.ValueKind == JsonValueKind.String && intervention.GetString() is { Length: > 0 and <= 200 }))) throw InvalidResponse();
        }
        var began = Date(root, "beginDate");
        // Roblox's Delete punishment has no scheduled end, even if a legacy date is present.
        var ends = permanent ? null : Date(root, "endDate");
        if (began is { } start && ends is { } end && end < start) throw InvalidResponse();
        string reason = "";
        if (root.TryGetProperty("messageToUser", out var message) && message.ValueKind != JsonValueKind.Null)
        {
            if (message.ValueKind != JsonValueKind.String || message.GetString() is not { Length: <= 4000 } text || text.Contains('\0')) throw InvalidResponse();
            reason = text;
        }
        return new(began, ends, reason) { Kind = permanent ? ModerationKind.PermanentBan : warning ? ModerationKind.Warning : ModerationKind.Legacy };
    }

    internal static RestrictionSource? ReadRestrictionSource(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("errors", out _) ||
            !root.TryGetProperty("restriction", out var restriction)) throw InvalidResponse();
        if (restriction.ValueKind == JsonValueKind.Null) return null;
        if (restriction.ValueKind != JsonValueKind.Object || !restriction.TryGetProperty("source", out var source) ||
            source.ValueKind != JsonValueKind.Number || !source.TryGetInt32(out int kind) || kind <= 0) throw InvalidResponse();
        return (RestrictionSource)kind;
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
        account.ModerationCheckStatus = restriction is { IsBrowserWarning: true } ? "Warning seen in the account browser" : "Checked";
    }
    public static void Failure(Account account, Exception error) => account.ModerationCheckStatus = error switch
    {
        RobloxApiError { StatusCode: 401 } or MissingSessionError => "Ban check needs sign-in",
        RobloxApiError { StatusCode: 429 } => "Ban check rate limited · retry later",
        _ => "Ban check failed · retry"
    };
}
