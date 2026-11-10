using System.Globalization;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public static class GroupTarget
{
    public const long MaxScriptId = 9_007_199_254_740_991;
    public static long Parse(string value)
    {
        value = value.Trim();
        if (value.Any(char.IsControl) || value.Length > 4096) throw new UserError("Enter a group/community ID or a Roblox community link.");
        if (GameTarget.TryId(value, out long id)) return Validate(id);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !Security.IsRobloxWebsite(uri))
            throw new UserError("Enter a group/community ID or an HTTPS Roblox community link.");
        var match = Regex.Match(uri.AbsolutePath, @"^/(?:groups|communities)/([0-9]+)(?:/[^/]*)?/?$", RegexOptions.CultureInvariant);
        if (!match.Success || !GameTarget.TryId(match.Groups[1].Value, out id)) throw new UserError("That link does not identify a Roblox group/community.");
        return Validate(id);
    }
    public static long Validate(long id) => id > 0 && id <= MaxScriptId ? id : throw new UserError("Invalid group/community ID.");
    public static string Url(long id) => "https://www.roblox.com/communities/" + Validate(id).ToString(CultureInfo.InvariantCulture);
}
