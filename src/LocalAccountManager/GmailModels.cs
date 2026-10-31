using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public sealed class GmailSettings
{
    public GoogleDesktopClient Client { get; set; } = new();
    public List<GmailConnection> Connections { get; set; } = [];
    public List<string> UsedMessages { get; set; } = [];
    public override string ToString() => "GmailSettings [redacted]";
}

public sealed class GoogleDesktopClient
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public override string ToString() => "GoogleDesktopClient [redacted]";
    public void Validate(bool allowEmpty = false)
    {
        if (allowEmpty && ClientId == "" && ClientSecret == "") return;
        if (ClientId is null || !Regex.IsMatch(ClientId, @"^[0-9]+-[a-zA-Z0-9_-]{1,180}\.apps\.googleusercontent\.com$") ||
            ClientSecret is null || ClientSecret.Length is < 1 or > 1024 || ClientSecret.Any(char.IsControl))
            throw new UserError("Import the JSON for a Google OAuth Desktop app client. Web app credentials are not supported.");
    }
    public static GoogleDesktopClient Parse(string json)
    {
        if (json.Length > 16000) throw new UserError("The Google client file is too large.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var installed = document.RootElement.GetProperty("installed");
            var result = new GoogleDesktopClient { ClientId = installed.GetProperty("client_id").GetString()!, ClientSecret = installed.GetProperty("client_secret").GetString()! };
            result.Validate(); return result; // Never trust endpoints or redirect URLs from the file.
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new UserError("Choose a Google OAuth Desktop app client JSON file."); }
    }
}

public sealed class GmailConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Address { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public bool AutomaticLoginCodes { get; set; } = true;
    public override string ToString() => "GmailConnection [redacted]";
}

public sealed class SignupEmail
{
    public string Mailbox { get; set; } = "";
    public string Address { get; set; } = "";
    public bool Verify { get; set; } = true;
    public override string ToString() => "SignupEmail [redacted]";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Mailbox) || Mailbox.Length > 254 || Mailbox.Any(char.IsControl)) throw new UserError("Choose a connected Gmail mailbox.");
        if (Address is null) throw new UserError("Enter an email address or leave it blank for the connected mailbox.");
        if (Address.Length > 0) _ = GmailAddress.Normalize(Address);
    }
}

public static class GmailAddress
{
    public static string Normalize(string value)
    {
        if (value is null || value.Length is < 3 or > 254 || value.Any(c => char.IsControl(c) || c > 127) ||
            !MailAddress.TryCreate(value, out var address) || !address.Address.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(address.User, @"^[a-zA-Z0-9._+%-]+$") || !address.Host.Contains('.'))
            throw new UserError("Enter a valid email address without a display name.");
        return address.Address.ToLowerInvariant();
    }
    public static bool BelongsTo(string recipient, string mailbox)
    {
        string Canonical(string input)
        {
            var address = new MailAddress(Normalize(input)); string user = address.User.Split('+')[0];
            if (address.Host is "gmail.com" or "googlemail.com") return user.Replace(".", "") + "@gmail.com";
            return user + "@" + address.Host;
        }
        return Canonical(recipient) == Canonical(mailbox);
    }
    public static void Validate(GmailSettings settings)
    {
        if (settings is null || settings.Client is null || settings.Connections is null || settings.UsedMessages is null || settings.Connections.Count > 20 || settings.UsedMessages.Count > 256)
            throw new UserError("The saved Gmail settings are invalid.");
        settings.Client.Validate(allowEmpty: settings.Connections.Count == 0);
        var ids = new HashSet<Guid>(); var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in settings.Connections)
        {
            if (connection is null || connection.Id == Guid.Empty || !ids.Add(connection.Id) || !addresses.Add(Normalize(connection.Address)) ||
                connection.RefreshToken is null || connection.RefreshToken.Length is < 1 or > 8192 || connection.RefreshToken.Any(char.IsControl)) throw new UserError("The saved Gmail connection is invalid.");
        }
        if (settings.UsedMessages.Any(s => s is null || !Regex.IsMatch(s, "^[a-f0-9]{64}$"))) throw new UserError("The Gmail replay protection data is invalid.");
    }
}

public sealed record RobloxEmailInfo(string Address, bool Verified);

public sealed class EmailRequest
{
    public int Id { get; set; }
    public string Action { get; set; } = "";
    public string Mailbox { get; set; } = "";
    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public bool ReplaceExisting { get; set; }
    public override string ToString() => "EmailRequest [redacted]";
    public void Validate()
    {
        if (Id < 1 || Action is not ("list" or "waitForCode" or "linkAndVerify") || TimeoutSeconds is < 1 or > 300 ||
            Mailbox is null || Mailbox.Length > 254 || Mailbox.Any(char.IsControl) || Address is null || Address.Length > 254 ||
            Username is null || Username.Length > 20 || Username.Any(char.IsControl)) throw new UserError("Invalid email operation.");
        if (Action != "list" && Mailbox.Length == 0) throw new UserError("Choose a connected Gmail mailbox.");
        if (Address.Length > 0) _ = GmailAddress.Normalize(Address);
        if (Username.Length > 0 && !Regex.IsMatch(Username, "^[A-Za-z0-9_]{3,20}$")) throw new UserError("Enter a Roblox username for the email operation.");
    }
}
