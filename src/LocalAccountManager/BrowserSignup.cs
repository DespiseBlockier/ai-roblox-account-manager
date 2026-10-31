using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public sealed class SignupProxy
{
    public string Type { get; set; } = "http";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string? Login { get; set; }
    public string? Password { get; set; }
    public override string ToString() => "SignupProxy [redacted]";
}

public sealed class SignupRequest
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Birthday { get; set; } = "";
    public bool AcceptTerms { get; set; }
    public string ApiKey { get; set; } = "";
    public string Provider { get; set; } = "solvex"; // Preserve existing explicitly run Solvex scripts.
    public SignupEmail? Email { get; set; }
    public SignupProxy? Proxy { get; set; }
    public override string ToString() => "SignupRequest [redacted]";
    public void Validate()
    {
        if (Username is null || !Regex.IsMatch(Username, "^[A-Za-z0-9_]{3,20}$") || Username.StartsWith("YOUR_", StringComparison.Ordinal)) throw new UserError("Enter a Roblox username of 3–20 letters, numbers, or underscores.");
        BrowserAccountStore.ValidatePassword(Password);
        if (Password is null || Password.Length < 8 || Password.StartsWith("YOUR_", StringComparison.Ordinal)) throw new UserError("Enter a Roblox password of at least eight characters.");
        if (!DateOnly.TryParseExact(Birthday, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birthday) || birthday > DateOnly.FromDateTime(DateTime.UtcNow) || birthday.Year < 1900) throw new UserError("Enter your real birthday as YYYY-MM-DD.");
        if (!AcceptTerms) throw new UserError("Read Roblox's terms and privacy policy, then set acceptTerms to true to sign up.");
        Email?.Validate();
        _ = BrowserSolverClient.ProviderHost(Provider);
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length > 512 || ApiKey.Any(char.IsControl) || ApiKey.StartsWith("YOUR_", StringComparison.Ordinal)) throw new UserError("Enter your own " + BrowserSolverClient.ProviderName(Provider) + " API key.");
        if (Proxy is null)
        {
            if (Provider == "solvex") throw new UserError("Solvex requires your own proxy. Use 2Captcha or Anti-Captcha for proxyless mode.");
            return;
        }
        if (Provider != "solvex" && Proxy.Type == "https") throw new UserError("This provider supports http or socks5 proxies in signup. Use type http for an HTTP CONNECT proxy.");
        if (Proxy.Type is not ("http" or "https" or "socks5") || Proxy.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(Proxy.Address) || Proxy.Address.Length > 253 || Proxy.Address.StartsWith("YOUR_", StringComparison.Ordinal) ||
            Uri.CheckHostName(Proxy.Address) == UriHostNameType.Unknown || Proxy.Address.Any(c => c is '/' or '\\' or '@' or ':' || char.IsWhiteSpace(c) || char.IsControl(c))) throw new UserError("Enter a valid http, https, or socks5 proxy host and port.");
        if (Provider == "anticaptcha")
        {
            var probe = JsonSerializer.SerializeToElement(new { type = "FunCaptchaTask", websiteURL = "https://www.roblox.com/", websitePublicKey = "validation", proxyType = Proxy.Type, proxyAddress = Proxy.Address, proxyPort = Proxy.Port, userAgent = "Validation/1.0" });
            _ = BrowserSolverClient.ValidateTask(probe, Provider);
        }
        foreach (string? value in new[] { Proxy.Login, Proxy.Password })
            if (value is not null && (value.Length > 512 || value.Any(char.IsControl))) throw new UserError("Invalid proxy credentials.");
    }
    public void ClearSecrets() { Password = ""; ApiKey = ""; if (Proxy is not null) Proxy.Password = Proxy.Login = null; }
}
