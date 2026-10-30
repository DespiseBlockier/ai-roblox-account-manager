using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Windows.Threading;

namespace LocalAccountManager;

public sealed class BrowserHub(Vault vault, RobloxApi api, Func<Account, RobloxIdentity, string, bool, Task> onIdentity)
{
    private readonly Dictionary<Guid, AccountBrowser> browsers = [];
    private Task<CoreWebView2Environment>? environment;
    public int Count => browsers.Count;

    public async Task<AccountBrowser> Open(Account account, string url = "https://www.roblox.com/home", bool visible = true, bool navigate = true)
    {
        if (!Security.IsBrowserPage(url)) throw new UserError("Only the Roblox website can be opened in an account browser.");
        if (browsers.TryGetValue(account.Id, out var current))
        {
            await current.Ready;
            if (visible) { current.ShowForUser(); if (navigate) current.Navigate(url); }
            return current;
        }
        environment ??= CoreWebView2Environment.CreateAsync(null, vault.BrowserRoot, new CoreWebView2EnvironmentOptions
        { AreBrowserExtensionsEnabled = false, AllowSingleSignOnUsingOSPrimaryAccount = false });
        CoreWebView2Environment env;
        try { env = await environment.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { environment = null; throw new UserError("Microsoft Edge WebView2 Runtime is required. Install it from Microsoft's WebView2 download page, then reopen this app."); }
        // A second UI action can have created this account's browser while the environment loaded.
        if (browsers.TryGetValue(account.Id, out current))
        {
            await current.Ready;
            if (visible) { current.ShowForUser(); if (navigate) current.Navigate(url); }
            return current;
        }
        var browser = new AccountBrowser(account, env, api, onIdentity);
        browsers.Add(account.Id, browser);
        browser.Closed += (_, _) => browsers.Remove(account.Id);
        // WPF initializes WebView2 after Loaded. A helper starts minimized without taking focus,
        // then hides after initialization; it never navigates to a web page.
        if (visible) browser.ShowForUser();
        else { browser.ShowActivated = false; browser.WindowState = WindowState.Minimized; browser.Show(); }
        try
        {
            await browser.Initialize(); await browser.RestoreSession(account.SessionCookie);
            if (visible) { browser.ShowForUser(); if (navigate) browser.Navigate(url); }
            else if (!browser.ShownForUser) browser.Hide();
        }
        catch { browser.Close(); throw; }
        return browser;
    }

    private async Task<string> ReadCookie(Account account)
    {
        if (browsers.TryGetValue(account.Id, out var open)) return await open.ReadSession();
        if (account.SessionCookie.Length > 0) return account.SessionCookie;
        var helper = await Open(account, visible: false);
        try { return await helper.ReadSession(); } finally { helper.Close(); }
    }

    public async Task<string> VerifiedCookie(Account account) => await VerifyCookie(account, await ReadCookieForCheck(account));

    private async Task<string> ReadCookieForCheck(Account account)
    {
        try { return await ReadCookie(account); }
        catch (Exception ex) { SessionHealth.Failure(account, ex); throw; }
    }

    private async Task<string> VerifyCookie(Account account, string cookie)
    {
        try
        {
            var identity = await api.Identity(cookie);
            if (account.UserId == 0 || identity.Id != account.UserId)
            { account.SessionStatus = "Wrong account · sign in"; throw new UserError("This session belongs to a different account. Click Sign in to reconnect the selected account."); }
            await onIdentity(account, identity, cookie, false);
            account.SessionStatus = "Valid session"; account.SessionCheckedAt = DateTimeOffset.UtcNow;
            return cookie;
        }
        catch (Exception ex) { if (!account.SessionStatus.StartsWith("Wrong account")) SessionHealth.Failure(account, ex); throw; }
    }

    public async Task CheckHealth(Account account)
    {
        string cookie;
        try { cookie = await ReadCookieForCheck(account); }
        catch (Exception ex) { Moderation.Failure(account, ex); throw; }
        RobloxApiError? restrictedIdentity = null;
        try { await VerifyCookie(account, cookie); }
        // Moderation can remain readable when a restriction blocks the normal identity endpoint.
        catch (RobloxApiError ex) when (ex.StatusCode is 401 or 403) { restrictedIdentity = ex; }
        catch (Exception ex) { Moderation.Failure(account, ex); throw; }
        try
        {
            Moderation.Apply(account, await api.Restriction(cookie));
            if (account.Restriction is not null && restrictedIdentity is not null) account.SessionStatus = "Restricted · open browser";
        }
        catch (Exception ex) { Moderation.Failure(account, ex); throw; }
    }

    public async Task Clear(Account account)
    {
        var browser = await Open(account, visible: false);
        await browser.ClearSession();
        browser.Close();
        account.SessionCookie = "";
        account.SessionStatus = "Signed out locally";
    }
    public void CloseAll() { foreach (var browser in browsers.Values.ToList()) browser.Close(); }
}

public sealed partial class AccountBrowser : Window
{
    private readonly Account account;
    private readonly CoreWebView2Environment environment;
    private readonly RobloxApi api;
    private readonly Func<Account, RobloxIdentity, string, bool, Task> onIdentity;
    private readonly WebView2 web = new();
    private readonly TextBlock address = new() { Text = "Loading browser…", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock status = new() { Text = "Sign in on Roblox, then click Connect account. Complete 2-step verification on Roblox if requested.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 10, 14, 10) };
    private readonly TaskCompletionSource ready = new();
    public Task Ready => ready.Task;
    private bool disposed;
    private bool checking;
    private readonly DispatcherTimer loginTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LoginCredential? login;
    private bool loginMode;
    private bool filled;
    private bool autofilling;
    private bool loginSubmitted;
    private string loginAttemptKey = "";
    private bool ticking;
    private string? lastAttemptedCookie;
    private TaskCompletionSource<LoginOutcome> loginComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<LoginOutcome> LoginCompleted => loginComplete.Task;
    private readonly Button skipLogin = new() { Name = "SkipLogin", Content = "Skip account →", Visibility = Visibility.Collapsed, ToolTip = "Discard this pending login and continue with the next account. Closing the browser stops the entire queue." };
    private bool loginAbandoned;
    private readonly CancellationTokenSource closedCancellation = new();
    public bool IsClosed => disposed;
    public bool ShownForUser { get; private set; }

    public AccountBrowser(Account account, CoreWebView2Environment environment, RobloxApi api, Func<Account, RobloxIdentity, string, bool, Task> onIdentity)
    {
        Style = (Style)FindResource(typeof(Window));
        this.account = account; this.environment = environment; this.api = api; this.onIdentity = onIdentity;
        Title = "Roblox · " + account.Username + " · Account browser";
        Width = 1160; Height = 820; MinWidth = 800; MinHeight = 600;
        var root = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(12) };
        var connect = new Button { Content = "Connect account", Style = (Style)FindResource("Primary") };
        connect.Click += async (_, _) => await Connect();
        DockPanel.SetDock(connect, Dock.Right); top.Children.Add(connect);
        skipLogin.Click += (_, _) => SkipLogin();
        DockPanel.SetDock(skipLogin, Dock.Right); top.Children.Add(skipLogin);
        var home = new Button { Content = "Home" }; home.Click += (_, _) => Navigate("https://www.roblox.com/home");
        DockPanel.SetDock(home, Dock.Left); top.Children.Add(home);
        top.Children.Add(address);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        root.Children.Add(web); Content = root;
        loginTimer.Tick += async (_, _) =>
        {
            if (ticking || disposed || !loginMode) return;
            ticking = true;
            try { await Autofill(); await Connect(automatic: true); }
            catch (Exception ex) { if (!disposed) status.Text = Security.Friendly(ex); }
            finally { ticking = false; }
        };
        Closed += (_, _) => { disposed = true; closedCancellation.Cancel(); loginMode = false; login = null; lastAttemptedCookie = null; loginTimer.Stop(); loginComplete.TrySetResult(LoginOutcome.Stopped); web.Dispose(); };
    }

    public async Task Initialize()
    {
        try
        {
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = account.Id.ToString("N");
            options.IsInPrivateModeEnabled = false;
            await web.EnsureCoreWebView2Async(environment, options).WaitAsync(TimeSpan.FromSeconds(30));
            var core = web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = true;
            core.Profile.IsPasswordAutosaveEnabled = false;
            core.Profile.IsGeneralAutofillEnabled = false;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            core.NavigationStarting += (_, args) =>
            {
                // Let the external-scheme event validate origin and perform the only allowed handoff.
                if (args.Uri.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase) && Security.IsBrowserPage(core.Source)) return;
                if (!Security.IsBrowserPage(args.Uri) && args.Uri != "about:blank")
                { args.Cancel = true; status.Text = "This browser only opens Roblox pages. External navigation was blocked."; }
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Security.IsBrowserPage(args.Uri)) core.Navigate(args.Uri);
                else status.Text = "An external popup was blocked.";
            };
            core.LaunchingExternalUriScheme += (_, args) =>
            {
                args.Cancel = true;
                if (!Security.CanLaunchFromBrowser(args.Uri, args.InitiatingOrigin, core.Source)) return;
                try { Security.LaunchRoblox(args.Uri); status.Text = "Launch request sent to the installed Roblox player."; }
                catch (Exception ex) { status.Text = Security.Friendly(ex); }
            };
            core.SourceChanged += (_, _) => address.Text = core.Source;
            core.WindowCloseRequested += (_, _) => Close();
            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess || !loginMode || disposed) return;
                try { await Autofill(); } catch (Exception ex) { if (!disposed) status.Text = Security.Friendly(ex); }
            };
            ready.TrySetResult();
        }
        catch (Exception ex) { ready.TrySetException(ex); throw; }
    }

    public void Navigate(string url)
    {
        if (!disposed && web.CoreWebView2 is not null && Security.IsBrowserPage(url)) web.CoreWebView2.Navigate(url);
    }

    public void ShowForUser()
    {
        if (disposed) return;
        ShownForUser = true;
        ShowActivated = true;
        Show();
        // Restore after Show: a hidden helper may retain its minimized native-window state.
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (ready.Task.IsCompletedSuccessfully) web.Focus();
    }

    public async Task<string> ReadSession()
    {
        if (disposed) throw new UserError("The account browser was closed. Open it again.");
        var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com/");
        var cookie = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY" && c.IsSecure &&
            (c.Domain == ".roblox.com" || c.Domain == "roblox.com" || c.Domain == "www.roblox.com"));
        if (cookie is null || string.IsNullOrEmpty(cookie.Value))
            throw new MissingSessionError();
        return cookie.Value;
    }

    public async Task<bool> Connect(bool automatic = false)
    {
        if (checking || disposed || loginAbandoned || !ready.Task.IsCompletedSuccessfully) return false;
        checking = true;
        try
        {
            string cookie = await ReadSession();
            if (disposed || loginAbandoned) return false;
            if (automatic && cookie == lastAttemptedCookie) return false;
            lastAttemptedCookie = cookie;
            if (!automatic) status.Text = "Checking this browser's Roblox identity…";
            var identity = await api.Identity(cookie);
            if (disposed || loginAbandoned) return false;
            if (account.UserId > 0 && account.UserId != identity.Id)
                throw new UserError("This entry belongs to @" + account.Username + ". Sign in to that account, or add a new entry for this account.");
            if (login is not null && !login.Username.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
                throw new UserError("Sign in to the queued account @" + login.Username + " before continuing.");
            await onIdentity(account, identity, cookie, true);
            if (disposed) return false;
            Title = "Roblox · @" + identity.Name + " · Account browser";
            status.Text = "Connected @" + identity.Name + ". You can close this browser and launch games from the manager.";
            loginMode = false; loginTimer.Stop(); login = null; lastAttemptedCookie = null; skipLogin.Visibility = Visibility.Collapsed;
            loginComplete.TrySetResult(LoginOutcome.Connected);
            return true;
        }
        catch (Exception ex) { if (!disposed && (!automatic || ex is not MissingSessionError)) status.Text = Security.Friendly(ex); return false; }
        finally { checking = false; }
    }

    public async Task BeginLogin(LoginCredential? credential, string progress, bool allowSkip = false)
    {
        await ClearSession();
        if (disposed) { loginComplete.TrySetResult(LoginOutcome.Stopped); return; }
        loginComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        login = credential; filled = false; loginSubmitted = false; loginAttemptKey = LoginAutofill.NewAttemptKey();
        lastAttemptedCookie = null; loginMode = true; loginAbandoned = false;
        skipLogin.Visibility = allowSkip ? Visibility.Visible : Visibility.Collapsed;
        Title = "Roblox sign in · " + progress;
        status.Text = progress + " · Complete the Roblox login." + (allowSkip ? " Skip account moves to the next login without saving this one." : "") + " Close this browser to stop the queue.";
        ShowForUser();
        Navigate("https://www.roblox.com/login"); loginTimer.Start();
    }

    private void SkipLogin()
    {
        if (disposed || !loginMode || skipLogin.Visibility != Visibility.Visible || loginComplete.Task.IsCompleted) return;
        loginAbandoned = true; loginMode = false; login = null; lastAttemptedCookie = null; loginTimer.Stop();
        skipLogin.IsEnabled = false;
        loginComplete.TrySetResult(LoginOutcome.Skipped);
        Close();
    }

    private async Task Autofill()
    {
        var credential = login;
        string attemptKey = loginAttemptKey;
        if (disposed || !loginMode || autofilling || credential is null || !LoginAutofill.IsLoginPage(web.CoreWebView2.Source)) return;
        bool Current() => !disposed && loginMode && !loginAbandoned && ReferenceEquals(login, credential) && loginAttemptKey == attemptKey;
        autofilling = true;
        try
        {
            if (!filled)
            {
                string result = await web.CoreWebView2.ExecuteScriptAsync(LoginAutofill.Script(credential.Username, credential.Password));
                if (!Current() || result != "true") return;
                filled = true;
                status.Text = "Filled @" + credential.Username + ".";
            }
            if (!Current() || loginSubmitted || !LoginAutofill.CanSubmit(credential)) return;
            if (!LoginAutofill.IsLoginPage(web.CoreWebView2.Source)) return;
            // Reserve the attempt before awaiting JS. An uncertain response must never cause another click.
            loginSubmitted = true;
            try
            {
                string result = await web.CoreWebView2.ExecuteScriptAsync(LoginAutofill.SubmitScript(attemptKey, credential.Username, credential.Password));
                if (!Current()) return;
                if (result == "false")
                {
                    loginSubmitted = false;
                    status.Text = "Details filled. Waiting for Log In to become ready; you can also click it manually.";
                }
                else if (result == "true") status.Text = "Log In pressed. Complete any Roblox verification. The next account opens after a verified sign-in.";
                else status.Text = "Automatic login could not be confirmed. Complete the login manually.";
            }
            catch { if (Current()) status.Text = "Automatic login could not be confirmed. Complete the login manually."; }
        }
        finally { autofilling = false; }
    }

    public async Task RestoreSession(string cookie)
    {
        if (cookie.Length == 0) return;
        var manager = web.CoreWebView2.CookieManager;
        if ((await manager.GetCookiesAsync("https://www.roblox.com/")).Any(c => c.Name == ".ROBLOSECURITY" && !string.IsNullOrEmpty(c.Value))) return;
        var restored = manager.CreateCookie(".ROBLOSECURITY", cookie, ".roblox.com", "/");
        restored.IsSecure = true; restored.IsHttpOnly = true;
        manager.AddOrUpdateCookie(restored);
    }

    public async Task ClearSession()
    {
        await web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        web.CoreWebView2.CookieManager.DeleteAllCookies();
    }
}
