using System.Text.Json;

namespace LocalAccountManager;

public static class AgeGroups
{
    // Only known Roblox translation keys become labels. Never infer a bracket from a birthday.
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Under9"] = "Under 9", ["9To12"] = "9–12", ["Under13"] = "Under 13",
        ["13To15"] = "13–15", ["16To17"] = "16–17", ["18To20"] = "18–20",
        ["Over21"] = "21+", ["Over13"] = "13+", ["Under18"] = "Under 18", ["Over18"] = "18+"
    };

    public static bool IsValid(string? value) => value == "Unknown" || value is not null && Labels.ContainsValue(value);

    public static string Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("errors", out _)) throw InvalidResponse();
        if (!root.TryGetProperty("ageGroupTranslationKey", out var field) || field.ValueKind == JsonValueKind.Null) return "Unknown";
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: <= 150 } key) throw InvalidResponse();
        foreach (string prefix in new[] { "Label.AgeGroup", "Label.AgeBand" })
            if (key.StartsWith(prefix, StringComparison.Ordinal) && Labels.TryGetValue(key[prefix.Length..], out string? label)) return label;
        return "Unknown";
    }

    public static void Apply(Account account, string group)
    {
        if (!IsValid(group)) throw InvalidResponse();
        account.AgeGroup = group;
        account.AgeGroupCheckedAt = DateTimeOffset.UtcNow;
        account.AgeGroupCheckStatus = group == "Unknown" ? "Roblox did not provide a recognized age group." : "Checked";
    }

    public static void Failure(Account account, Exception error) => account.AgeGroupCheckStatus = error switch
    {
        RobloxApiError { StatusCode: 401 or 403 } or MissingSessionError => "Age-group check needs sign-in",
        RobloxApiError { StatusCode: 429 } => "Age-group check rate limited · retry later",
        _ => "Age-group check failed · retry"
    };

    private static UserError InvalidResponse() => new("Roblox returned an unexpected age-group response. Try Check status again later.");
}
