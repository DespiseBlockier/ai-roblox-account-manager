using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public sealed record GameTarget(long? PlaceId, string? PrivateUrl)
{
    public static GameTarget Parse(string value)
    {
        value = value.Trim();
        if (value.Length > 4096) throw new UserError("The game link is too long.");
        if (value.Any(char.IsControl)) throw new UserError("The game link contains invalid characters.");
        if (TryId(value, out long id)) return new(id, null);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !Security.IsRobloxWebsite(uri))
            throw new UserError("Enter a positive place ID or an HTTPS link from www.roblox.com.");
        if (uri.AbsolutePath.Equals("/share", StringComparison.OrdinalIgnoreCase))
        {
            if (Query(uri, "type") != "Server" || !ValidCode(Query(uri, "code")))
                throw new UserError("Use a Roblox server share link with type=Server and a valid code.");
            return new(null, "https://www.roblox.com/share?code=" + Uri.EscapeDataString(Query(uri, "code")) + "&type=Server");
        }
        var match = Regex.Match(uri.AbsolutePath, @"^/games/([0-9]+)(?:/[^/]*)?/?$", RegexOptions.CultureInvariant);
        if (!match.Success || !TryId(match.Groups[1].Value, out id))
            throw new UserError("That link does not identify a Roblox game.");
        string code = Query(uri, "privateServerLinkCode");
        if (code.Length == 0) return new(id, null);
        if (!ValidCode(code)) throw new UserError("The private server code is invalid.");
        return new(id, $"https://www.roblox.com/games/{id}?privateServerLinkCode={Uri.EscapeDataString(code)}");
    }

    public static bool TryId(string value, out long id) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    public static string ValidateJob(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return "";
        if (!Guid.TryParseExact(value, "D", out var guid)) throw new UserError("The server Job ID must be a GUID, or leave it empty.");
        return guid.ToString("D");
    }

    private static bool ValidCode(string value) => Regex.IsMatch(value, @"^[a-zA-Z0-9_-]{1,256}$", RegexOptions.CultureInvariant);

    private static string Query(Uri uri, string name)
    {
        var values = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => Uri.UnescapeDataString(p[0]).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (values.Count > 1) throw new UserError("The link has duplicate parameters.");
        return values.Count == 1 && values[0].Length == 2 ? Uri.UnescapeDataString(values[0][1]) : "";
    }
}

public static class Security
{
    private static readonly HashSet<string> ApiHosts = new(StringComparer.OrdinalIgnoreCase)
        { "users.roblox.com", "auth.roblox.com", "games.roblox.com", "groups.roblox.com", "usermoderation.roblox.com" };

    public static bool IsRobloxWebsite(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.IdnHost.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.Equals("roblox.com", StringComparison.OrdinalIgnoreCase));

    public static bool IsBrowserPage(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && IsRobloxWebsite(uri);

    public static bool IsApiUrl(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (ApiHosts.Contains(uri.IdnHost) || uri.IdnHost.Equals("accountsettings.roblox.com", StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0 && (uri.AbsolutePath is "/v1/email" or "/v1/email/verify") || uri.IdnHost.Equals("apis.roblox.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath == "/user-settings-api/v1/account-insights/age-group" && uri.Query.Length == 0);

    public static bool CanLaunchFromBrowser(string uri, string origin, string topLevel) =>
        IsBrowserPage(origin) && IsBrowserPage(topLevel) && uri.Length <= 20000 && !uri.Any(char.IsControl) &&
        uri.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase);

    public static string BuildLaunchUri(long placeId, string job, string ticket)
    {
        if (placeId <= 0) throw new UserError("Invalid place ID.");
        job = GameTarget.ValidateJob(job);
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 8192 || ticket.Any(char.IsControl)) throw new UserError("Roblox returned an invalid launch ticket.");
        string tracker = System.Security.Cryptography.RandomNumberGenerator.GetInt32(10000000, int.MaxValue).ToString(CultureInfo.InvariantCulture);
        string launcher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=" + (job.Length == 0 ? "RequestGame" : "RequestGameJob") +
            $"&browserTrackerId={tracker}&placeId={placeId}" + (job.Length == 0 ? "" : "&gameId=" + job) + "&isPlayTogetherGame=false";
        return "roblox-player:1+launchmode:play+gameinfo:" + Uri.EscapeDataString(ticket) +
            "+launchtime:" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
            "+placelauncherurl:" + Uri.EscapeDataString(launcher) + "+browsertrackerid:" + tracker + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
    }

    public static void LaunchRoblox(string uri)
    {
        if (uri.Length > 20000 || uri.Any(char.IsControl) || !uri.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase))
            throw new UserError("Blocked an invalid Roblox launch request.");
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { throw new UserError("Windows could not open Roblox. Install Roblox from roblox.com, then try again."); }
    }

    public static string Friendly(Exception ex) => ex switch
    {
        UserError => ex.Message,
        TaskCanceledException => "Roblox took too long to respond. Try again.",
        TimeoutException => "The browser took too long to initialize. Close this app and try again.",
        System.Net.Http.HttpRequestException => "Could not reach Roblox. Check your connection and try again.",
        System.Security.Cryptography.CryptographicException => "The password is incorrect or the encrypted file is damaged. The original file has been preserved.",
        System.IO.IOException => "Could not read or save the local data. Check folder permissions and available disk space.",
        UnauthorizedAccessException => "Windows denied access to the local data folder.",
        _ => "The operation could not be completed. Your saved data has not been reset."
    };
}
