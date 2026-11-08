using System.Text.Json;

namespace LocalAccountManager;

internal sealed record GameAccessDetails(string Name, long UniverseId, long? RootPlaceId, string? Price, bool Paid);
internal sealed record GameAccessStatus(string Result, string? Reason, bool PurchaseRequired, bool DemoAvailable);

internal static class GameAccess
{
    // Games API serializes these values as names or integers, depending on the endpoint deployment.
    private enum Playability
    {
        GuestProhibited = 2, GameUnapproved = 3, IncorrectConfiguration = 4, UniverseRootPlaceIsPrivate = 5,
        InsufficientPermissionFriendsOnly = 6, InsufficientPermissionGroupOnly = 7, DeviceRestricted = 8,
        UnderReview = 9, PurchaseRequired = 10, AccountRestricted = 11, TemporarilyUnavailable = 12,
        PlaceHasNoPublishedVersion = 13, ComplianceBlocked = 14, ContextualPlayabilityRegionalAvailability = 15,
        ContextualPlayabilityRegionalCompliance = 16, ContextualPlayabilityAgeRecommendationParentalControls = 17,
        ContextualPlayabilityExperienceBlockedParentalControls = 18, ContextualPlayabilityAgeGated = 19,
        ContextualPlayabilityUnverifiedSeventeenPlusUser = 20, FiatPurchaseRequired = 21, FiatPurchaseDeviceRestricted = 22,
        ContextualPlayabilityUnrated = 23, ContextualPlayabilityAgeGatedByDescriptor = 24,
        ContextualPlayabilityAgeCheckRequired = 26, ContextualPlayabilityRequireParentApproval = 27,
        ContextualPlayabilityCoreGated = 28, ContextualPlayabilityTrustedFriendRequired = 29, PlusSubscriptionRequired = 30,
        ContextualPlayabilityPlaytestDisabled = 31, InsufficientPermissionEditorsOnly = 32, InsufficientPermissionPlayTestersOnly = 33
    }

    public static GameAccessDetails ReadPlace(JsonElement root, long placeId)
    {
        var place = Find(root, "placeId", placeId, "Roblox returned no details for this place. Check the place ID or game link.");
        long universeId = PositiveId(place, "universeId") ?? throw InvalidResponse();
        long? price = PositiveId(place, "price");
        bool hasFiatData = place.TryGetProperty("fiatPurchaseData", out var purchase) && purchase.ValueKind == JsonValueKind.Object;
        string? priceLabel = hasFiatData ? Text(purchase, "localizedFiatPrice", 80) : null;
        bool fiat = priceLabel is not null || hasFiatData && Text(purchase, "basePriceId", 80) is not null;
        if (priceLabel is null && price is long robux) priceLabel = robux.ToString("N0") + " Robux";
        return new(Text(place, "name", 160) ?? "Place " + placeId, universeId, PositiveId(place, "universeRootPlaceId"), priceLabel, fiat || price.HasValue);
    }

    public static GameAccessStatus ReadStatus(JsonElement root, long universeId)
    {
        var entry = Find(root, "universeId", universeId, "Roblox returned no access status for this experience.");
        if (!entry.TryGetProperty("isPlayable", out var playable) || playable.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidResponse();
        Playability code = default;
        if (entry.TryGetProperty("playabilityStatus", out var status))
        {
            if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int number)) code = (Playability)number;
            else if (status.ValueKind == JsonValueKind.String) Enum.TryParse(status.GetString(), out code);
        }

        bool purchaseRequired = code is Playability.PurchaseRequired or Playability.FiatPurchaseRequired or Playability.FiatPurchaseDeviceRestricted;
        string result = playable.GetBoolean() && !purchaseRequired ? "Can play" : code switch
        {
            Playability.UniverseRootPlaceIsPrivate => "Game is private",
            Playability.PurchaseRequired or Playability.FiatPurchaseRequired => "Purchase required",
            Playability.FiatPurchaseDeviceRestricted => "Purchase requires a supported device",
            Playability.GuestProhibited => "Sign-in required",
            Playability.GameUnapproved => "Game blocked by Roblox moderation",
            Playability.IncorrectConfiguration => "Game configuration prevents access",
            Playability.AccountRestricted => "Account restricted",
            Playability.InsufficientPermissionFriendsOnly => "Friends only",
            Playability.InsufficientPermissionGroupOnly => "Group members only",
            Playability.InsufficientPermissionEditorsOnly => "Editors only",
            Playability.InsufficientPermissionPlayTestersOnly => "Playtesters only",
            Playability.UnderReview => "Game under review",
            Playability.TemporarilyUnavailable => "Game temporarily unavailable",
            Playability.PlaceHasNoPublishedVersion => "Game has no published version",
            Playability.DeviceRestricted => "Device restricted",
            Playability.ComplianceBlocked or Playability.ContextualPlayabilityRegionalAvailability or Playability.ContextualPlayabilityRegionalCompliance => "Unavailable in this region",
            Playability.ContextualPlayabilityAgeRecommendationParentalControls or Playability.ContextualPlayabilityExperienceBlockedParentalControls => "Blocked by parental controls",
            Playability.ContextualPlayabilityAgeGated or Playability.ContextualPlayabilityAgeGatedByDescriptor or Playability.ContextualPlayabilityCoreGated => "Age restricted",
            Playability.ContextualPlayabilityAgeCheckRequired or Playability.ContextualPlayabilityUnverifiedSeventeenPlusUser => "Age verification required",
            Playability.ContextualPlayabilityRequireParentApproval => "Parent approval required",
            Playability.ContextualPlayabilityUnrated => "Game has no maturity rating",
            Playability.ContextualPlayabilityTrustedFriendRequired => "Trusted friend required",
            Playability.PlusSubscriptionRequired => "Roblox Plus required",
            Playability.ContextualPlayabilityPlaytestDisabled => "Playtest unavailable",
            _ => "Access blocked"
        };
        bool demo = purchaseRequired && entry.TryGetProperty("demoModeAvailable", out var demoValue) && demoValue.ValueKind == JsonValueKind.True;
        return new(result, Text(entry, "unplayableDisplayText", 240), purchaseRequired, demo);
    }

    public static string Describe(GameAccessDetails game, GameAccessStatus access, bool? owns)
    {
        var parts = new List<string> { game.Name, access.Result };
        if (game.Paid || access.PurchaseRequired)
        {
            parts.Add(game.Price is { } price ? "Paid game (" + price + ")" : "Paid game");
            parts.Add(owns switch { true => "Owns access", false => "No purchase found", _ => "Ownership unavailable" });
        }
        if (access.DemoAvailable) parts.Add("Demo available");
        if (access.Reason is { } reason && !string.Equals(reason, access.Result, StringComparison.OrdinalIgnoreCase)) parts.Add(reason);
        return string.Join(" · ", parts);
    }

    private static JsonElement Find(JsonElement root, string key, long id, string missing)
    {
        if (root.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        var matches = root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && PositiveId(item, key) == id).Take(2).ToList();
        return matches.Count switch { 1 => matches[0], 0 => throw new UserError(missing), _ => throw InvalidResponse() };
    }

    private static long? PositiveId(JsonElement item, string key) => item.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long id) && id > 0 ? id : null;

    private static string? Text(JsonElement item, string key, int maximum)
    {
        if (!item.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return null;
        string text = new string((value.GetString() ?? "").Where(c => !char.IsControl(c)).Take(maximum).ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    private static UserError InvalidResponse() => new("Roblox returned an unexpected game access response. Try again later.");
}
