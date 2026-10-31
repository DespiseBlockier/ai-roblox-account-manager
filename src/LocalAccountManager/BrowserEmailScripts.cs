using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public static class BrowserEmailScripts
{
    public static string RpcSource
    {
        get { using var stream = typeof(BrowserEmailScripts).Assembly.GetManifestResourceStream("LocalAccountManager.BrowserEmailRpc.js") ?? throw new UserError("Email helpers are missing."); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    }
    public static string NewKey() => "__ram_email_" + Guid.NewGuid().ToString("N");
    private static string Key(string value) => Regex.IsMatch(value, "^__ram_email_[a-f0-9]{32}$") ? JsonSerializer.Serialize(value) : throw new UserError("Invalid email browser identifier.");
    public static string Install(string key, string username)
    {
        using var resource = typeof(BrowserEmailScripts).Assembly.GetManifestResourceStream("LocalAccountManager.BrowserEmail.js") ?? throw new UserError("Browser email helpers are missing.");
        using var reader = new StreamReader(resource); return reader.ReadToEnd().Replace("EMAIL_KEY", Key(key)).Replace("EXPECTED_USERNAME", JsonSerializer.Serialize(username));
    }
    public const string ConfirmVerification = """
        (()=>{
          if (location.protocol !== 'https:' || location.hostname !== 'www.roblox.com' || (location.port && location.port !== '443') ||
              location.pathname.replace(/\/$/,'') !== '/account/settings/verify-email' || !new URLSearchParams(location.search).has('ticket')) return false;
          const buttons = [...document.querySelectorAll('button')].filter(e => e.getClientRects().length && !e.disabled &&
            e.getAttribute('aria-disabled') !== 'true' && /^verify (?:my |your )?email(?: address)?$/i.test(e.textContent.trim()));
          if (buttons.length !== 1) return false;
          buttons[0].click(); return true;
        })();
        """;
    public static string Snapshot(string key) => "window[" + Key(key) + "]?.snapshot() ?? null;";
    public static string Fill(string key, int id, string code, string context) => "window[" + Key(key) + "]?.fill(" + id + "," + JsonSerializer.Serialize(code) + "," + JsonSerializer.Serialize(context) + ") ?? false;";
    public static string Submit(string key, int id, string code, string context) => "window[" + Key(key) + "]?.submit(" + id + "," + JsonSerializer.Serialize(code) + "," + JsonSerializer.Serialize(context) + ") ?? false;";
}
