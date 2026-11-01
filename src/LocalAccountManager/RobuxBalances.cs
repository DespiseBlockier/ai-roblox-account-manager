using System.Text.Json;

namespace LocalAccountManager;

public static class RobuxBalances
{
    public static long Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("errors", out _) ||
            !root.TryGetProperty("robux", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long balance) || balance < 0)
            throw new UserError("Roblox returned an unexpected Robux balance. Try Check status again later.");
        return balance;
    }
    public static void Apply(Account account, long balance)
    {
        if (balance < 0) throw new UserError("Invalid Robux balance.");
        account.RobuxBalance = balance; account.RobuxCheckedAt = DateTimeOffset.UtcNow; account.RobuxCheckStatus = "Status refreshed";
    }
    public static void Failure(Account account, Exception error) => account.RobuxCheckStatus = error switch
    {
        RobloxApiError { StatusCode: 401 or 403 } or MissingSessionError => "Robux check needs sign-in",
        RobloxApiError { StatusCode: 429 } => "Robux check rate limited · retry later",
        _ => "Robux check failed · retry"
    };
}
