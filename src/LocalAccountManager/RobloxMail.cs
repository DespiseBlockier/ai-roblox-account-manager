using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public enum RobloxMailPurpose { LoginCode, VerifyEmail }
public sealed record RobloxMail(string MessageId, DateTimeOffset Received, string Value)
{
    public override string ToString() => "RobloxMail [redacted]";
}

public static class RobloxMailParser
{
    private static bool Matches(string value, string pattern, RegexOptions options = RegexOptions.IgnoreCase) => Regex.IsMatch(value, pattern, options | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
    public static RobloxMail? Parse(JsonElement message, string username, string recipient, RobloxMailPurpose purpose, DateTimeOffset since, DateTimeOffset now)
    {
        try
        {
            if (!Matches(username, "^[A-Za-z0-9_]{3,20}$")) return null;
            recipient = GmailAddress.Normalize(recipient);
            string id = message.GetProperty("id").GetString()!;
            if (id is null || !Matches(id, "^[a-zA-Z0-9_-]{1,128}$")) return null;
            if (!long.TryParse(message.GetProperty("internalDate").GetString(), out long milliseconds)) return null;
            var received = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            if (received < since || received < now.AddMinutes(-15) || received > now.AddMinutes(1)) return null;
            var payload = message.GetProperty("payload"); var headers = payload.GetProperty("headers").EnumerateArray().ToList();
            string Header(string name) => headers.FirstOrDefault(h => h.GetProperty("name").GetString()!.Equals(name, StringComparison.OrdinalIgnoreCase)) is var item && item.ValueKind == JsonValueKind.Object ? item.GetProperty("value").GetString() ?? "" : "";
            if (!MailAddress.TryCreate(Header("From"), out var from)) return null;
            string sender = from.Address.ToLowerInvariant();
            if (purpose == RobloxMailPurpose.LoginCode ? sender != "no-reply@roblox.com" : sender is not ("email_validate@roblox.com" or "no-reply@roblox.com")) return null;
            // Use Gmail's first authentication result, never a later sender-supplied lookalike.
            string authentication = Header("Authentication-Results");
            if (!Matches(authentication, @"^mx\.google\.com\s*;") || !Matches(authentication, @"(?:^|;)\s*dmarc=pass\b[^;]*\bheader\.from=roblox\.com(?=\s|;|$)")) return null;
            bool Recipient(string value)
            {
                try { var list = new MailAddressCollection(); list.Add(value); return list.Any(a => a.Address.Equals(recipient, StringComparison.OrdinalIgnoreCase)); } catch (FormatException) { return false; }
            }
            if (!Recipient(Header("To")) && !Recipient(Header("Delivered-To"))) return null;
            string subject = Header("Subject");
            if (purpose == RobloxMailPurpose.LoginCode ? !Matches(subject, @"(?:2|two)[ -]step\s+verification|security\s+code|login\s+code") : !Matches(subject, @"verif(?:y|ication).{0,40}email|email.{0,40}verif(?:y|ication)")) return null;
            var text = new StringBuilder(); var html = new StringBuilder(); int parts = 0;
            void Read(JsonElement part, int depth)
            {
                if (depth > 8 || ++parts > 32) return;
                if (part.TryGetProperty("filename", out var filename) && filename.GetString()?.Length > 0) return;
                string mime = part.TryGetProperty("mimeType", out var type) ? type.GetString() ?? "" : "";
                if (mime is "text/plain" or "text/html" && part.TryGetProperty("body", out var body) && body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                {
                    string encoded = data.GetString()!; if (encoded.Length > 88000 || text.Length + html.Length > 128000) return;
                    string base64 = encoded.Replace('-', '+').Replace('_', '/'); base64 += new string('=', (4 - base64.Length % 4) % 4);
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    (mime == "text/plain" ? text : html).Append(decoded).Append('\n');
                }
                if (part.TryGetProperty("parts", out var children)) foreach (var child in children.EnumerateArray()) Read(child, depth + 1);
            }
            Read(payload, 0);
            string markup = html.ToString();
            string clean = Regex.Replace(markup, @"<(script|style)\b[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
            clean = WebUtility.HtmlDecode(Regex.Replace(clean, "<[^>]{0,2000}>", " ", RegexOptions.None, TimeSpan.FromMilliseconds(150)));
            string content = text + "\n" + clean;
            // Ambiguous messages are left to the user rather than guessing between alt accounts.
            if (!Matches(content, @"(?:\bhi|\bhello|\bhey|\busername\s*:)\s+@?" + Regex.Escape(username) + @"(?![A-Za-z0-9_])")) return null;
            if (purpose == RobloxMailPurpose.LoginCode)
            {
                var codes = Regex.Matches(content, @"(?<!\d)[0-9]{6}(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150)).Select(m => m.Value).Distinct().ToList();
                return codes.Count == 1 ? new(id, received, codes[0]) : null;
            }
            string linksText = text + "\n" + WebUtility.HtmlDecode(markup);
            var links = Regex.Matches(linksText, "https://[^\\s<>\"']{1,6000}", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150))
                .Select(m => m.Value).Where(IsVerificationUrl).Distinct(StringComparer.Ordinal).ToList();
            return links.Count == 1 ? new(id, received, links[0]) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or RegexMatchTimeoutException) { return null; }
    }
    public static bool IsVerificationUrl(string value)
    {
        if (value.Length > 6000 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !Security.IsRobloxWebsite(uri) || uri.IdnHost != "www.roblox.com" || uri.Fragment.Length > 0 ||
            uri.AbsolutePath.TrimEnd('/') != "/account/settings/verify-email") return false;
        var query = uri.Query.TrimStart('?').Split('&');
        return query.Length == 1 && query[0].StartsWith("ticket=", StringComparison.Ordinal) && query[0].Length is > 7 and <= 5000 && !value.Any(char.IsControl);
    }
}
