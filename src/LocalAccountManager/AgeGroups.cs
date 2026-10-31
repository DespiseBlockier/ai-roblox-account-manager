using System.Text.Json;

namespace LocalAccountManager;

public sealed record AgeGroupResult(string Group, bool? IsChecked);

public static class AgeGroups
{
    // Only known Roblox translation keys become labels. Never infer a bracket from a birthday.
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Under9"] = "Under 9", ["9To12"] = "9–12", ["Under13"] = "Under 13",
        ["13To15"] = "13–15", ["16To17"] = "16–17", ["18To20"] = "18–20",
        ["Over21"] = "21+", ["Over13"] = "13+", ["Under18"] = "Under 18", ["Over18"] = "18+"
    };

    public static bool IsValid(string? value) => value is "Unknown" or "Unchecked" || value is not null && Labels.ContainsValue(value);

    public static AgeGroupResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("errors", out _)) throw InvalidResponse();
        if (!root.TryGetProperty("isChecked", out var check) || check.ValueKind == JsonValueKind.Null) return new("Unknown", null);
        if (check.ValueKind == JsonValueKind.False) return new("Unchecked", false);
        if (check.ValueKind != JsonValueKind.True) throw InvalidResponse();
        if (!root.TryGetProperty("ageGroupTranslationKey", out var field) || field.ValueKind == JsonValueKind.Null) return new("Unknown", true);
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: <= 150 } key) throw InvalidResponse();
        foreach (string prefix in new[] { "Label.AgeGroup", "Label.AgeBand" })
            if (key.StartsWith(prefix, StringComparison.Ordinal) && Labels.TryGetValue(key[prefix.Length..], out string? label)) return new(label, true);
        return new("Unknown", true);
    }

    public static void Apply(Account account, AgeGroupResult result)
    {
        if (!IsValid(result.Group) || result.IsChecked == true && result.Group == "Unchecked") throw InvalidResponse();
        account.IsAgeChecked = result.IsChecked;
        account.AgeGroup = result.IsChecked switch { false => "Unchecked", true => result.Group, null => "Unknown" };
        account.AgeGroupCheckedAt = DateTimeOffset.UtcNow;
        account.AgeGroupCheckStatus = result.IsChecked switch
        {
            false => "Roblox reports that this account has not completed an age check.",
            null => "Roblox did not provide an age-check status.",
            true => result.Group == "Unknown" ? "Roblox did not provide a recognized age group." : "Status refreshed"
        };
    }

    public static void Failure(Account account, Exception error) => account.AgeGroupCheckStatus = error switch
    {
        RobloxApiError { StatusCode: 401 or 403 } or MissingSessionError => "Age-group check needs sign-in",
        RobloxApiError { StatusCode: 429 } => "Age-group check rate limited · retry later",
        _ => "Age-group check failed · retry"
    };

    private static UserError InvalidResponse() => new("Roblox returned an unexpected age-group response. Try Check status again later.");
}
