using System.Globalization;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LocalAccountManager;

public sealed partial class AccountBrowser
{
    private static string DisplayBrowserAddress(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && Security.IsRobloxWebsite(uri) &&
        uri.AbsolutePath.TrimEnd('/') == "/account/settings/verify-email" ? uri.GetLeftPart(UriPartial.Path) + " · verification link" : source;
    private string emailBrowserKey = "";
    private string? emailObserverId;
    private DateTimeOffset emailLoginStarted;
    private CancellationTokenSource? emailLoginCancel;
    private Task? emailLoginTask;
    private bool emailLoginTried;
    private (GmailConnection Connection, string Address)? pendingEmailBinding;
    private Action<CoreWebView2>? emailReady = null;

    private async Task InstallEmailObserver(string username, CancellationToken cancel)
    {
        if (gmail is null) return;
        var core = web.CoreWebView2;
        if (emailObserverId is not null) core.RemoveScriptToExecuteOnDocumentCreated(emailObserverId);
        emailBrowserKey = BrowserEmailScripts.NewKey();
        emailObserverId = await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserEmailScripts.Install(emailBrowserKey, username)).WaitAsync(TimeSpan.FromSeconds(5), cancel);
    }
    private void CancelEmailLogin()
    {
        emailLoginCancel?.Cancel(); emailLoginCancel?.Dispose(); emailLoginCancel = null;
    }
    private async Task PrepareEmailLogin()
    {
        CancelEmailLogin(); emailLoginCancel = CancellationTokenSource.CreateLinkedTokenSource(closedCancellation.Token);
        emailLoginStarted = DateTimeOffset.UtcNow; emailLoginTried = false; pendingEmailBinding = null;
        await InstallEmailObserver(login?.Username ?? (account.UserId > 0 ? account.Username : ""), emailLoginCancel.Token);
    }
    private async Task<(string Username, int Id, string Document)> EmailPrompt(CancellationToken cancel)
    {
        if (emailBrowserKey.Length == 0 || !Security.IsBrowserPage(web.CoreWebView2.Source)) return ("", 0, "");
        using var document = JsonDocument.Parse(await web.CoreWebView2.ExecuteScriptAsync(BrowserEmailScripts.Snapshot(emailBrowserKey)).WaitAsync(TimeSpan.FromSeconds(5), cancel));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.GetString() != "email2fa") return ("", 0, "");
        string username = root.TryGetProperty("username", out var user) && user.ValueKind == JsonValueKind.String ? user.GetString()! : "";
        return (username, root.GetProperty("id").GetInt32(), root.GetProperty("documentId").GetString() ?? "");
    }
    private async Task MaybeFillEmailLogin()
    {
        if (gmail is null || !gmail.HasAutomaticMailboxes || emailLoginTried || disposed || !loginMode || emailLoginCancel is null) return;
        var attempt = emailLoginCancel; var cancel = attempt.Token;
        var prompt = await EmailPrompt(cancel);
        string expected = login?.Username ?? (account.UserId > 0 ? account.Username : prompt.Username);
        if (prompt.Id <= 0 || expected.Length == 0 || !expected.Equals(prompt.Username, StringComparison.OrdinalIgnoreCase)) return;
        emailLoginTried = true;
        emailLoginTask = Fill();
        async Task Fill()
        {
            bool Current() => !disposed && loginMode && ReferenceEquals(emailLoginCancel, attempt) && !cancel.IsCancellationRequested;
            try
            {
                var connections = gmail.ForLogin(account); if (connections.Count == 0) return;
                status.Text = "Waiting for a fresh Roblox email code for @" + expected + "…";
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(2);
                while (Current() && DateTimeOffset.UtcNow < deadline)
                {
                    foreach (var connection in connections)
                    {
                        if (!connection.AutomaticLoginCodes || !gmail.IsConnected(connection)) continue;
                        string address = account.GmailConnectionId == connection.Id && account.EmailAddress.Length > 0 ? account.EmailAddress : connection.Address;
                        var found = await gmail.Find(connection, expected, address, RobloxMailPurpose.LoginCode, emailLoginStarted, cancel);
                        if (!Current() || !connection.AutomaticLoginCodes || !gmail.IsConnected(connection)) return;
                        var current = await EmailPrompt(cancel);
                        if (current.Document != prompt.Document || current.Id != prompt.Id || !current.Username.Equals(expected, StringComparison.OrdinalIgnoreCase)) return;
                        if (found is null) continue;
                        if (await web.CoreWebView2.ExecuteScriptAsync(BrowserEmailScripts.Fill(emailBrowserKey, prompt.Id, found.Value, prompt.Document)) != "true") return;
                        if (!Current()) return;
                        gmail.Consume(connection, found); pendingEmailBinding = (connection, address);
                        await Task.Delay(200, cancel); if (!Current()) return;
                        string submitted = await web.CoreWebView2.ExecuteScriptAsync(BrowserEmailScripts.Submit(emailBrowserKey, prompt.Id, found.Value, prompt.Document));
                        if (Current()) status.Text = submitted == "true" ? "Email 2FA code submitted once. Waiting for verified sign-in…" : "Email 2FA code filled. Click Verify in Roblox; no code retry will be made.";
                        return;
                    }
                    await Task.Delay(5000, cancel);
                }
                if (Current()) status.Text = "No matching fresh Roblox email code arrived. Complete email verification manually.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (Current()) status.Text = GmailWindow.Friendly(ex) + " Complete email 2FA manually."; }
        }
    }

    private async Task<CoreWebView2> OpenEmailView(string cookie, CancellationToken cancel)
    {
        signupControlsBrowser = true; signupInitializing = true;
        signupWeb?.Dispose(); if (signupWeb is not null) browserSurface.Children.Remove(signupWeb);
        var view = new WebView2 { Name = "EmailBrowser" }; signupWeb = view; web.Visibility = Visibility.Collapsed; browserSurface.Children.Add(view); ShowForUser();
        try
        {
            var options = environment.CreateCoreWebView2ControllerOptions(); options.ProfileName = "Email" + Guid.NewGuid().ToString("N"); options.IsInPrivateModeEnabled = true;
            await view.EnsureCoreWebView2Async(environment, options).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false; core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Profile.IsPasswordAutosaveEnabled = false; core.Profile.IsGeneralAutofillEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => { if (!Security.IsBrowserPage(e.Uri) && e.Uri != "about:blank") e.Cancel = true; };
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (Security.IsBrowserPage(e.Uri)) core.Navigate(e.Uri); };
            core.SourceChanged += (_, _) => { if (!disposed) address.Text = DisplayBrowserAddress(core.Source); };
            core.WindowCloseRequested += (_, _) => Close();
            var session = core.CookieManager.CreateCookie(".ROBLOSECURITY", cookie, ".roblox.com", "/"); session.IsSecure = session.IsHttpOnly = true; core.CookieManager.AddOrUpdateCookie(session);
            emailReady?.Invoke(core); view.Focus(); return core;
        }
        finally { signupInitializing = false; }
    }

    private async Task<bool> VerifyEmailInBrowser(CoreWebView2 core, RobloxApi connectionApi, RobloxIdentity identity, string cookie, SignupEmail request,
        string password, bool replaceExisting, Action<string> progress, CancellationToken cancel)
    {
        if (gmail is null) throw new UserError("Connect Gmail in Settings before linking email.");
        request.Validate(); var connection = gmail.Resolve(request.Mailbox); string recipient = gmail.Recipient(connection, request.Address);
        var saved = gmail.SavedAccount(identity);
        void Report(string value) { if (!disposed && !cancel.IsCancellationRequested) { status.Text = value; progress(value); } }
        if (await SignupCookie(core, cancel) != cookie) throw new UserError("The browser session changed before email linking.");
        var current = await connectionApi.EmailInfo(cookie).WaitAsync(cancel);
        if (current.Address.Equals(recipient, StringComparison.OrdinalIgnoreCase) && current.Verified) { gmail.Bind(saved, connection, recipient, true, true); return true; }
        if (current.Address.Length > 0 && !current.Address.Equals(recipient, StringComparison.OrdinalIgnoreCase) && !replaceExisting)
            throw new UserError("This Roblox account already has a different email. Use replaceExisting:true only if you intend to replace it.");
        core.Navigate("https://www.roblox.com/my/account#!/info");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Report("Linking the connected Gmail address to @" + identity.Name + "…");
        if (current.Address.Equals(recipient, StringComparison.OrdinalIgnoreCase)) await connectionApi.ResendEmailVerification(cookie, cancel);
        else await connectionApi.SetEmail(cookie, recipient, password, cancel);
        gmail.Bind(saved, connection, recipient, true);
        if (!request.Verify) return false;
        Report("Waiting for a fresh Roblox verification email for @" + identity.Name + "…");
        bool navigated = false, confirmationAttempted = false;
        while (DateTimeOffset.UtcNow - started < TimeSpan.FromMinutes(2))
        {
            cancel.ThrowIfCancellationRequested();
            if (disposed || await SignupCookie(core, cancel) != cookie) throw new UserError("The verification browser session changed. The account is saved; check email verification in Roblox.");
            current = await connectionApi.EmailInfo(cookie).WaitAsync(cancel);
            if (current.Verified && current.Address.Equals(recipient, StringComparison.OrdinalIgnoreCase))
            { gmail.Bind(saved, connection, recipient, true, true); Report("Roblox confirmed the email is verified for @" + identity.Name + "."); return true; }
            if (!navigated)
            {
                var message = await gmail.Find(connection, identity.Name, recipient, RobloxMailPurpose.VerifyEmail, started, cancel);
                if (message is not null)
                {
                    gmail.Consume(connection, message);
                    if (!RobloxMailParser.IsVerificationUrl(message.Value)) throw new UserError("The verification link is unsupported. Complete verification manually.");
                    core.Navigate(message.Value); navigated = true;
                    Report("Opened Roblox's email verification page. Waiting for Roblox to confirm verification…");
                }
            }
            if (navigated && !confirmationAttempted)
            {
                confirmationAttempted = true;
                string result = await core.ExecuteScriptAsync(BrowserEmailScripts.ConfirmVerification).WaitAsync(TimeSpan.FromSeconds(5), cancel);
                if (result == "false") confirmationAttempted = false; // Proven no click; an uncertain result is never replayed.
            }
            await Task.Delay(5000, cancel);
        }
        throw new UserError("The account was saved, but Roblox has not confirmed its email. Finish any steps in the verification browser.");
    }
}
