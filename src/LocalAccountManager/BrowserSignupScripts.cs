using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public static class BrowserSignupScripts
{
    public static string NewKey() => "__ram_signup_" + Guid.NewGuid().ToString("N");
    private static string Key(string key) => Regex.IsMatch(key, "^__ram_signup_[a-f0-9]{32}$") ? JsonSerializer.Serialize(key) : throw new UserError("Invalid signup session identifier.");
    public static string Install(string key)
    {
        using var stream = typeof(BrowserSignupScripts).Assembly.GetManifestResourceStream("LocalAccountManager.BrowserSignup.js") ?? throw new UserError("The browser signup helpers are missing.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd().Replace("SIGNUP_KEY", Key(key), StringComparison.Ordinal);
    }
    public static string Fill(string key, SignupRequest call)
    {
        var birthday = DateOnly.ParseExact(call.Birthday, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return "window[" + Key(key) + "]?.fill(" + JsonSerializer.Serialize(new { username = call.Username, password = call.Password, year = birthday.Year, month = birthday.Month, day = birthday.Day }) + ") ?? null;";
    }
    public static string Challenge(string key) => "window[" + Key(key) + "]?.challenge() ?? null;";
    public static string Apply(string key, int index, string token) => "window[" + Key(key) + "]?.apply(" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + JsonSerializer.Serialize(token) + ") ?? false;";
}
