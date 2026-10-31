using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LocalAccountManager;

public sealed partial class AccountBrowser
{
    // The caller document keeps the script's pending promise alive while this visible
    // signup view navigates. Every registration gets a fresh private browser profile.
    private async Task<object> CreateAccountInBrowser(SignupRequest call, BrowserSolverClient solvers, Action<string> progress, CancellationToken cancel)
    {
        call.Validate();
        if (call.Email is { } email) { if (gmail is null) throw new UserError("Connect Gmail in Settings before creating an account with email verification."); var connected = gmail.Resolve(email.Mailbox); _ = gmail.Recipient(connected, email.Address); }
        cancel.ThrowIfCancellationRequested(); signupActive = true; signupInitializing = true; signupControlsBrowser = true;
        signupWeb?.Dispose();
        if (signupWeb is not null) browserSurface.Children.Remove(signupWeb);
        var view = new WebView2 { Name = "SignupBrowser" }; signupWeb = view;
        web.Visibility = Visibility.Collapsed; browserSurface.Children.Add(view); ShowForUser();
        var frames = new List<CoreWebView2Frame>();
        string key = BrowserSignupScripts.NewKey();
        bool submitted = false, solverAttempted = false;
        string lastMessage = ""; string? attemptedCookie = null;
        void Report(string message) { if (message != lastMessage && !disposed) { lastMessage = message; status.Text = message; progress(message); } }
        try
        {
            Report("Opening the visible Roblox signup page…");
            var signupEnvironment = environment;
            if (call.Proxy is SignupProxy proxy)
            {
                // Browser proxy settings are process-wide, so use an isolated environment.
                string folder = Path.Combine(environment.UserDataFolder, "SignupProxy", Guid.NewGuid().ToString("N"));
                signupEnvironment = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions
                {
                    AreBrowserExtensionsEnabled = false, AllowSingleSignOnUsingOSPrimaryAccount = false,
                    AdditionalBrowserArguments = "--proxy-server=" + proxy.Type + "://" + proxy.Address + ":" + proxy.Port.ToString(CultureInfo.InvariantCulture)
                }).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            }
            var controller = signupEnvironment.CreateCoreWebView2ControllerOptions();
            controller.ProfileName = "Signup" + Guid.NewGuid().ToString("N"); controller.IsInPrivateModeEnabled = true;
            await view.EnsureCoreWebView2Async(signupEnvironment, controller).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            signupInitializing = false;
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Profile.IsPasswordAutosaveEnabled = false; core.Profile.IsGeneralAutofillEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => { if (!Security.IsBrowserPage(e.Uri) && e.Uri != "about:blank") e.Cancel = true; };
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (Security.IsBrowserPage(e.Uri)) core.Navigate(e.Uri); };
            core.SourceChanged += (_, _) => { if (!disposed) address.Text = DisplayBrowserAddress(core.Source); };
            core.WindowCloseRequested += (_, _) => Close();
            core.BasicAuthenticationRequested += (_, e) =>
            {
                e.Cancel = true;
                if (call.Proxy is not SignupProxy configured || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
                    !uri.Host.Equals(configured.Address, StringComparison.OrdinalIgnoreCase) || uri.Port != configured.Port) return;
                e.Response.UserName = configured.Login ?? ""; e.Response.Password = configured.Password ?? ""; e.Cancel = false;
            };
            void Track(CoreWebView2Frame frame)
            {
                frames.Add(frame); frame.FrameCreated += (_, e) => Track(e.Frame);
                frame.Destroyed += (_, _) => frames.Remove(frame);
            }
            core.FrameCreated += (_, e) => Track(e.Frame);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserSignupScripts.Install(key));
            signupReady?.Invoke(core);

            using var proxiedIdentityApi = call.Proxy is null ? null : new RobloxApi(new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = true,
                Proxy = new WebProxy(new Uri(call.Proxy.Type + "://" + call.Proxy.Address + ":" + call.Proxy.Port.ToString(CultureInfo.InvariantCulture)))
                { Credentials = string.IsNullOrEmpty(call.Proxy.Login) ? null : new NetworkCredential(call.Proxy.Login, call.Proxy.Password) }
            });
            Report("Checking whether Roblox allows this username…");
            await (proxiedIdentityApi ?? api).CheckSignupUsername(call.Username, call.Birthday, cancel);
            cancel.ThrowIfCancellationRequested();
            core.Navigate("https://www.roblox.com/signup"); view.Focus();
            DateTimeOffset opened = DateTimeOffset.UtcNow;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                if (disposed || view != signupWeb) throw new OperationCanceledException(cancel);
                string? cookie = await SignupCookie(core, cancel);
                if (cookie is not null && cookie != attemptedCookie)
                {
                    attemptedCookie = cookie;
                    Report("Checking the account signed in inside this browser…");
                    var identity = await (proxiedIdentityApi ?? api).Identity(cookie).WaitAsync(cancel);
                    cancel.ThrowIfCancellationRequested();
                    if (!string.Equals(identity.Name, call.Username, StringComparison.OrdinalIgnoreCase)) throw new UserError("The browser signed in to a different username. Nothing was saved for this signup.");
                    if (await SignupCookie(core, cancel) != cookie) throw new UserError("The signup browser session changed before saving. Nothing was saved.");
                    Account saved;
                    try { saved = await onAddAccount!(identity, cookie, call.Password, cancel); }
                    catch (OperationCanceledException) { throw; }
                    catch { throw new UserError("The browser signed in successfully, but saving to the vault failed. Add this account through browser login before trying to create it again."); }
                    AccountAdded?.Invoke(saved);
                    // Preserve the explicit addAccount/fetch session if the caller continues.
                    if (!disposed && !cancel.IsCancellationRequested)
                        try { await RestoreSession(cookie); } catch { /* The verified account is already committed to the vault. */ }
                    Report("Created and saved @" + saved.Username + " from the visible browser.");
                    bool? emailVerified = null; string? emailError = null;
                    if (call.Email is { } requestedEmail)
                    {
                        try { emailVerified = await VerifyEmailInBrowser(core, proxiedIdentityApi ?? api, identity, cookie, requestedEmail, call.Password, false, Report, cancel); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { emailVerified = false; emailError = GmailWindow.Friendly(ex); Report("Account created and saved. " + emailError); }
                    }
                    return new { success = true, saved = true, userId = saved.UserId.ToString(CultureInfo.InvariantCulture), username = saved.Username, emailVerified, emailError };
                }
                if (Security.IsBrowserPage(core.Source))
                {
                    using var filled = JsonDocument.Parse(await core.ExecuteScriptAsync(BrowserSignupScripts.Fill(key, call)).WaitAsync(TimeSpan.FromSeconds(5), cancel));
                    string phase = filled.RootElement.ValueKind == JsonValueKind.Object && filled.RootElement.TryGetProperty("phase", out var state) ? state.GetString() ?? "" : "";
                    if (phase == "username-taken") throw new UserError("This username is already taken. Choose a different username.");
                    if (phase == "username-inappropriate") throw new UserError("Roblox says this username is not appropriate. Choose a different username.");
                    if (phase == "username-rejected") throw new UserError("Roblox rejected this username in the signup form. Choose a different username.");
                    bool justSubmitted = !submitted && phase == "submitted";
                    submitted |= phase == "submitted";
                    if (phase == "validating") Report("Waiting for Roblox to validate the signup fields…");
                    if (justSubmitted) Report("Signup submitted by the visible browser. Waiting for Roblox verification…");
                    else if (!submitted && (phase == "review" || DateTimeOffset.UtcNow - opened > TimeSpan.FromSeconds(20))) Report("Review the signup fields in the visible browser. The app submits its filled form once; complete any remaining steps there.");
                }
                if (!solverAttempted && submitted)
                {
                    var targets = new List<Func<string, Task<string>>> { core.ExecuteScriptAsync };
                    foreach (var frame in frames.ToArray()) targets.Add(frame.ExecuteScriptAsync);
                    foreach (var execute in targets)
                    {
                        JsonElement challenge;
                        try
                        {
                            using var snapshot = JsonDocument.Parse(await execute(BrowserSignupScripts.Challenge(key)).WaitAsync(TimeSpan.FromSeconds(3), cancel));
                            challenge = snapshot.RootElement.Clone();
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { continue; } // A frame can be replaced by Roblox during verification.
                        if (challenge.ValueKind != JsonValueKind.Object) continue;
                        if (!challenge.TryGetProperty("index", out var index) || !index.TryGetInt32(out int callback) || callback < 0 || callback >= 8 ||
                            !challenge.TryGetProperty("websitePublicKey", out var pk) || pk.ValueKind != JsonValueKind.String ||
                            !challenge.TryGetProperty("blob", out var blob) || blob.ValueKind != JsonValueKind.String || blob.GetString()?.Length is not (> 0 and <= 50000)) continue;
                        solverAttempted = true;
                        var task = new Dictionary<string, object?>
                        {
                            ["type"] = call.Proxy is null ? "FunCaptchaTaskProxyless" : "FunCaptchaTask", ["websiteURL"] = core.Source,
                            ["websitePublicKey"] = pk.GetString(), ["data"] = call.Provider == "solvex" ? blob.GetString() : JsonSerializer.Serialize(new { blob = blob.GetString() })
                        };
                        if (challenge.TryGetProperty("funcaptchaApiJSSubdomain", out var domain) && domain.ValueKind == JsonValueKind.String) task["funcaptchaApiJSSubdomain"] = domain.GetString();
                        if (call.Provider == "2captcha" || call.Provider == "anticaptcha" && call.Proxy is not null) task["userAgent"] = core.Settings.UserAgent;
                        if (call.Proxy is SignupProxy chosen)
                        {
                            task["proxyType"] = chosen.Type; task["proxyAddress"] = chosen.Address; task["proxyPort"] = chosen.Port;
                            if (!string.IsNullOrEmpty(chosen.Login)) task["proxyLogin"] = chosen.Login;
                            if (!string.IsNullOrEmpty(chosen.Password)) task["proxyPassword"] = chosen.Password;
                        }
                        Report("CAPTCHA is open in the browser. Requesting one " + BrowserSolverClient.ProviderName(call.Provider) + " solution…");
                        var result = JsonSerializer.SerializeToElement(await solvers.Execute(new SolverRequest { Id = 1, Provider = call.Provider, Action = "solve", ApiKey = call.ApiKey, Task = JsonSerializer.SerializeToElement(task) }, cancel));
                        cancel.ThrowIfCancellationRequested();
                        if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
                            !result.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String || token.GetString()?.Length is not (> 0 and <= 20000))
                        {
                            string code = result.TryGetProperty("errorCode", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() is string name && System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z0-9_]{1,80}$") && !name.Contains(call.ApiKey, StringComparison.OrdinalIgnoreCase) ? name : "PROVIDER_ERROR";
                            Report(code == "ERROR_CAPTCHA_UNSOLVABLE" || code == "CAPTCHA_UNSOLVABLE"
                                ? BrowserSolverClient.ProviderName(call.Provider) + ": " + code + ". Provider could not solve it. Click Start puzzle in the browser and finish manually, or stop. No paid retry."
                                : BrowserSolverClient.ProviderName(call.Provider) + ": " + code + ". Finish verification in the browser or stop. No paid retry.");
                        }
                        else
                        {
                            bool applied = false;
                            try { applied = await execute(BrowserSignupScripts.Apply(key, callback, token.GetString()!)).WaitAsync(TimeSpan.FromSeconds(5), cancel) == "true"; }
                            catch (OperationCanceledException) { throw; }
                            catch { }
                            Report(applied ? "Solution passed to Roblox's browser callback. Waiting for the browser to sign in…" : "The verification frame changed. Complete the current verification in the browser; no extra solver task will be created.");
                        }
                        break;
                    }
                }
                if (submitted && !solverAttempted) Report("Complete any verification shown in the browser. A solver is used only after Arkose reports the challenge is shown, with readable configuration.");
                await Task.Delay(500, cancel);
            }
        }
        finally { signupActive = signupInitializing = false; }
    }

    private static async Task<string?> SignupCookie(CoreWebView2 core, CancellationToken cancel)
    {
        var cookies = await core.CookieManager.GetCookiesAsync("https://www.roblox.com/").WaitAsync(cancel);
        return cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY" && c.IsSecure && c.IsHttpOnly &&
            (c.Domain == ".roblox.com" || c.Domain == "roblox.com" || c.Domain == "www.roblox.com"))?.Value;
    }
}
