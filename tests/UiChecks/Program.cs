using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalAccountManager;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try { return Run(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int Run()
    {
        var app = new HarnessApp();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/RobloxAccountManager;component/Styles.xaml") });
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Check = () => Check(app);
        app.Run();
        return app.Result;
    }

    private static async Task Check(Application app)
    {
        int count = 0;
        void Assert(bool ok, string name) { if (!ok) throw new Exception("FAILED " + name); count++; Console.WriteLine("PASS " + name); }
        string root = Path.Combine(Environment.CurrentDirectory, "test-results", "ui-" + Guid.NewGuid().ToString("N"));
        using var vault = new Vault(root);
        vault.Unlock("ui-test-portable-master-password");
        var a = new Account { UserId = 123, Username = "test_a", DisplayName = "Test A" };
        var b = new Account { UserId = 456, Username = "test_b", DisplayName = "Test B" };
        var state = new AppState { Accounts = [a, b, new Account(), new Account()] };
        var window = new MainWindow(vault, state, monitorSessions: false);
        window.Show(); window.UpdateLayout();
        Assert(window.IsVisible && window.ActualWidth > 1000, "main window opens and lays out");
        Assert(window.Background is SolidColorBrush bg && bg.Color == Color.FromRgb(0x10, 0x13, 0x19), "dark window style applied to derived WPF window");
        Assert(window.Icon is not null, "app window uses the custom embedded icon");
        Directory.CreateDirectory(root);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string preview = Path.Combine(root, "layout-preview.png");
        using (var output = File.Create(preview)) encoder.Save(output);
        Console.WriteLine("Layout preview: " + preview);
        var grid = (DataGrid)window.FindName("AccountsGrid");
        Assert(grid.Items.Count == 2, "account list renders");
        Assert(state.Accounts.Count == 2, "old unfinished login rows removed from saved state");
        ((TextBox)window.FindName("Search")).Text = "test_b";
        Assert(grid.Items.Count == 1, "account search filters");
        ((TextBox)window.FindName("Search")).Text = "";
        grid.SelectedItem = a;
        Assert(((TextBlock)window.FindName("SelectedLabel")).Text == "@test_a", "account selection updates launch panel");
        window.Height = 710; window.UpdateLayout();
        Assert(((TextBox)window.FindName("PlaceInput")).ActualWidth > 150, "launch input remains sized at minimum window height");
        var bulk = new BulkEditWindow(window, [a, b], ["Default", "Farm"], _ => throw new Exception("Preview must not apply edits"));
        bulk.Show(); bulk.UpdateLayout();
        FindNamed<CheckBox>(bulk, "ApplyGroup")!.IsChecked = true;
        FindNamed<ComboBox>(bulk, "BulkGroup")!.Text = "Farm";
        FindNamed<CheckBox>(bulk, "ApplyAlias")!.IsChecked = true;
        FindNamed<TextBox>(bulk, "BulkAlias")!.Text = "{index}-{username}";
        FindNamed<Button>(bulk, "PreviewBulk")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(a.Group == "Default" && FindNamed<TextBlock>(bulk, "BulkFeedback")!.Text.StartsWith("Preview only"), "bulk editor previews without changing saved accounts");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        bulk.UpdateLayout();
        var bulkImage = new RenderTargetBitmap((int)bulk.ActualWidth, (int)bulk.ActualHeight, 96, 96, PixelFormats.Pbgra32); bulkImage.Render(bulk);
        var bulkEncoder = new PngBitmapEncoder(); bulkEncoder.Frames.Add(BitmapFrame.Create(bulkImage));
        string bulkPath = Path.Combine(root, "bulk-edit-preview.png");
        using (var output = File.Create(bulkPath)) bulkEncoder.Save(output);
        Console.WriteLine("Bulk edit preview: " + bulkPath);
        bulk.Close();
        using (var previewApi = new RobloxApi(new FakeHandler(_ => throw new Exception("Editor preview must not make API requests"))))
        {
            var previewHub = new BrowserHub(vault, previewApi, (_, _, _, _) => Task.CompletedTask);
            var editor = new BrowserCodeWindow(window, [a, b], previewHub);
            editor.Show(); editor.UpdateLayout();
            var groupInput = FindNamed<TextBox>(editor, "GroupInput")!;
            groupInput.Text = "42";
            FindNamed<Button>(editor, "GenerateExample")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(FindNamed<TextBox>(editor, "ScriptEditor")!.Text.Contains("const groupId = 42;"), "editor generates group code for the entered ID");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            editor.UpdateLayout();
            var editorBitmap = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            editorBitmap.Render(editor);
            var editorEncoder = new PngBitmapEncoder(); editorEncoder.Frames.Add(BitmapFrame.Create(editorBitmap));
            string editorPath = Path.Combine(root, "browser-code-preview.png");
            using (var output = File.Create(editorPath)) editorEncoder.Save(output);
            Console.WriteLine("Browser code preview: " + editorPath);
            groupInput.Text = "43";
            FindNamed<Button>(editor, "StartCodeRun")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!editor.IsRunning && FindNamed<TextBlock>(editor, "RunFeedback")!.Text.Contains("group ID changed"), "changing generated group ID requires regenerating before any run");
            editor.Close();
        }
        window.Close();
        using (var firstVault = new Vault(Path.Combine(root, "unlock-screen")))
        {
            var unlock = new UnlockVaultWindow(firstVault);
            Exception? unlockFailure = null;
            unlock.Loaded += (_, _) =>
            {
                try
                {
                    FindNamed<PasswordBox>(unlock, "MasterPassword")!.Password = "synthetic-master-password";
                    FindNamed<PasswordBox>(unlock, "ConfirmPassword")!.Password = "does-not-match";
                    FindNamed<Button>(unlock, "UnlockVault")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(!firstVault.Exists && unlock.State is null, "master-password confirmation prevents creating a mismatched vault");
                    FindNamed<PasswordBox>(unlock, "ConfirmPassword")!.Password = "synthetic-master-password";
                    FindNamed<Button>(unlock, "UnlockVault")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { unlockFailure = ex; unlock.Close(); }
            };
            Assert(unlock.ShowDialog() == true && firstVault.IsPortable && unlock.State is not null, "first-run dialog creates the portable encrypted vault");
            if (unlockFailure is not null) throw unlockFailure;
        }

        using var api = new RobloxApi(new FakeHandler(request =>
        {
            string cookie = request.Headers.GetValues("Cookie").Single();
            if (cookie.EndsWith("fake-expired")) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            long id = cookie.EndsWith("fake-a") ? 123 : cookie.EndsWith("fake-b") ? 456 : 789;
            string name = id == 123 ? "test_a" : id == 456 ? "test_b" : "queue_c";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"id\":{id},\"name\":\"{name}\",\"displayName\":\"Test\"}}") };
        }));
        var enrollment = new AccountEnrollment(state, () => vault.Save(state));
        var hub = new BrowserHub(vault, api, (account, identity, cookie, fromLogin) => { enrollment.Commit(account, identity, cookie, fromLogin); return Task.CompletedTask; });
        try
        {
            // No Roblox page is loaded and all API traffic uses a fake HttpMessageHandler.
            var browserA = await hub.Open(a, visible: false);
            var browserB = await hub.Open(b, visible: false);
            var webA = FindWeb(browserA)!;
            var webB = FindWeb(browserB)!;
            Assert(webA is not null && webB is not null, "hidden WebView2 profiles initialize");
            var cookieA = webA!.CoreWebView2.CookieManager.CreateCookie(".ROBLOSECURITY", "fake-a", ".roblox.com", "/");
            cookieA.IsSecure = true; cookieA.IsHttpOnly = true;
            webA.CoreWebView2.CookieManager.AddOrUpdateCookie(cookieA);
            Assert((await webB!.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com/")).Count == 0, "account B cannot see account A's cookies");
            var cookieB = webB.CoreWebView2.CookieManager.CreateCookie(".ROBLOSECURITY", "fake-b", ".roblox.com", "/");
            cookieB.IsSecure = true; cookieB.IsHttpOnly = true;
            webB.CoreWebView2.CookieManager.AddOrUpdateCookie(cookieB);
            Assert(await hub.VerifiedCookie(a) == "fake-a", "selected account A identity verified");
            Assert(await hub.VerifiedCookie(b) == "fake-b", "selected account B identity verified");
            a.UserId = 999;
            bool blocked = false; try { await hub.VerifiedCookie(a); } catch (UserError) { blocked = true; }
            Assert(blocked, "mismatched account identity blocks launch"); a.UserId = 123;
            Assert(!webA.CoreWebView2.Profile.IsPasswordAutosaveEnabled && !webA.CoreWebView2.Settings.AreHostObjectsAllowed && !webA.CoreWebView2.Settings.IsWebMessageEnabled, "browser credential-save and host bridges disabled");
            await hub.Clear(a);
            var reopened = await hub.Open(a, visible: false);
            Assert((await FindWeb(reopened)!.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com/")).Count == 0, "local sign out clears selected profile");
            Assert(await hub.VerifiedCookie(b) == "fake-b", "sign out preserves other account's session");

            using var credential = new LoginCredential("queue_c", "synthetic'\";password\\with spaces ");
            var pending = enrollment.Begin(credential);
            var pendingBrowser = await hub.Open(pending, visible: false);
            var pendingWeb = FindWeb(pendingBrowser)!;
            Assert(state.Accounts.Count == 2, "opening pending browser does not add a row");
            pendingWeb.CoreWebView2.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            pendingWeb.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                const string html = "<!doctype html><html><body><input id='login-username'><input type='password' id='login-password'></body></html>";
                args.Response = pendingWeb.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8");
            };
            await pendingBrowser.BeginLogin(credential, "synthetic fixture");
            string[]? filled = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                string raw = await pendingWeb.CoreWebView2.ExecuteScriptAsync("(() => { const u=document.querySelector('#login-username'),p=document.querySelector('#login-password'); return u&&p?[u.value,p.value]:null; })()");
                filled = JsonSerializer.Deserialize<string[]>(raw);
                if (filled is { Length: 2 } && filled[0] == credential.Username && filled[1] == credential.Password) break;
                await Task.Delay(100);
            }
            Assert(filled is { Length: 2 } && filled[0] == credential.Username && filled[1] == credential.Password, "autofill handles quotes and delimiters in an intercepted login-page fixture");
            var pendingCookie = pendingWeb.CoreWebView2.CookieManager.CreateCookie(".ROBLOSECURITY", "fake-c", ".roblox.com", "/");
            pendingCookie.IsSecure = true; pendingCookie.IsHttpOnly = true;
            pendingWeb.CoreWebView2.CookieManager.AddOrUpdateCookie(pendingCookie);
            Assert(await pendingBrowser.LoginCompleted.WaitAsync(TimeSpan.FromSeconds(10)), "synthetic verified login is detected automatically");
            Assert(state.Accounts.Count == 3 && pending.SavedPassword == credential.Password && pending.SessionCookie == "fake-c", "verified login alone saves row and encrypted credentials");
            pendingBrowser.Close(); enrollment.Finish();
            using var canceledCredential = new LoginCredential("queue_d", "discard-me");
            var canceled = enrollment.Begin(canceledCredential);
            var canceledBrowser = await hub.Open(canceled, visible: false);
            canceledBrowser.Close();
            Assert(!await canceledBrowser.LoginCompleted && state.Accounts.Count == 3, "closing unfinished browser cancels without adding a row");
            enrollment.Finish();
            hub.CloseAll();
            b.SessionCookie = "fake-expired";
            bool expired = false; try { await hub.VerifiedCookie(b); } catch (RobloxApiError ex) { expired = ex.StatusCode == 401; }
            Assert(expired && b.SessionStatus.StartsWith("Expired"), "expired saved cookies displayed distinctly");
        }
        finally { hub.CloseAll(); }
        Assert(hub.Count == 0, "browser windows disposed on close");
        count += await CheckBrowserCode(vault);
        Console.WriteLine($"All {count} UI and WebView2 checks passed. Only synthetic credentials and intercepted test HTML were used; no live Roblox login occurred.");
    }

    private static async Task<int> CheckBrowserCode(Vault vault)
    {
        int passed = 0;
        void Assert(bool ok, string name) { if (!ok) throw new Exception("FAILED " + name); passed++; Console.WriteLine("PASS " + name); }
        var account = new Account { UserId = 901, Username = "script_fixture", DisplayName = "Script Fixture", SessionCookie = "code-cookie", SavedPassword = "not-exposed-to-code" };
        bool nativeMember = false, browserMember = false, nativeWrongIdentity = false, nativeExpired = false;
        int joinPosts = 0, nativeMemberChecks = 0;
        string joinMode = "join";
        using var api = new RobloxApi(new FakeHandler(request =>
        {
            if (request.RequestUri!.Host == "users.roblox.com")
                return new HttpResponseMessage(nativeExpired ? HttpStatusCode.Unauthorized : HttpStatusCode.OK) { Content = new StringContent(nativeWrongIdentity ? "{\"id\":902,\"name\":\"wrong_fixture\",\"displayName\":\"Wrong\"}" : "{\"id\":901,\"name\":\"script_fixture\",\"displayName\":\"Fixture\"}") };
            if (request.RequestUri.Host == "groups.roblox.com")
            {
                if (request.Method != HttpMethod.Get) throw new Exception("Native code must only read membership");
                nativeMemberChecks++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nativeMember ? "{\"data\":[{\"group\":{\"id\":42}}]}" : "{\"data\":[]}") };
            }
            throw new Exception("Unexpected native destination");
        }));
        var hub = new BrowserHub(vault, api, (_, _, _, _) => Task.CompletedTask);
        async Task<AccountBrowser> Fixture()
        {
            var browser = await hub.Open(account, visible: false);
            var core = FindWeb(browser)!.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var uri = new Uri(args.Request.Uri);
                int status = 200; string extra = "";
                string body = "{}";
                if (uri.Host == "www.roblox.com") body = "<!doctype html><html><head><meta name='csrf-token' data-token='initial-fake-token'></head><body>Local browser-code fixture</body></html>";
                else if (uri.Host == "users.roblox.com") body = "{\"id\":901,\"name\":\"script_fixture\"}";
                else if (uri.Host == "groups.roblox.com" && uri.AbsolutePath.Contains("/groups/roles"))
                    body = browserMember ? "{\"data\":[{\"group\":{\"id\":42}}]}" : "{\"data\":[]}";
                else if (uri.Host == "groups.roblox.com" && args.Request.Method == "POST")
                {
                    joinPosts++;
                    if (joinMode == "rate") status = 429;
                    else if (joinMode == "join" && joinPosts == 1) { status = 403; extra = "x-csrf-token: replacement-fake-token\r\n"; }
                    else if (joinMode == "join") { nativeMember = browserMember = true; }
                }
                else if (args.Request.Method != "OPTIONS") status = 403;
                string headers = "Content-Type: " + (uri.Host == "www.roblox.com" ? "text/html" : "application/json") + "; charset=utf-8\r\n" + extra +
                    "Access-Control-Allow-Origin: https://www.roblox.com\r\nAccess-Control-Allow-Credentials: true\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: content-type,x-csrf-token\r\nAccess-Control-Expose-Headers: x-csrf-token,rblx-challenge-id\r\n";
                args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), status, status == 200 ? "OK" : "Test failure", headers);
            };
            return browser;
        }
        BrowserCodeRequest Request(string text, int seconds = 10, long? confirm = null) =>
            new("https://www.roblox.com/communities/42", text, TimeSpan.FromSeconds(seconds), confirm);
        try
        {
            var browser = await Fixture();
            var result = await browser.RunCode(Request("if ('password' in ram.account || 'cookie' in ram.account) throw new Error('unexpected credentials'); ram.done();"), _ => { }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Completed && browser.IsClosed, "ram.done closes the correct account browser and completes");
            browser = await Fixture();
            result = await browser.RunCode(Request("ram.status('No completion command');", 2), _ => { }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Failed && browser.IsClosed, "returning from code without ram.done never advances");
            browser = await Fixture();
            result = await browser.RunCode(Request("ram.stop('Fixture stop');"), _ => { }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Stopped && browser.IsClosed, "ram.stop closes browser and stops the queue");
            browser = await Fixture();
            result = await browser.RunCode(Request("ram.status('close-fixture'); await ram.sleep(300000);"), message => { if (message == "close-fixture") browser.Close(); }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Stopped && browser.IsClosed, "closing current code browser stops without completion");

            nativeMember = false; bool sawPending = false;
            browser = await Fixture();
            var pending = browser.RunCode(Request("ram.done();", 12, 42), message => { if (message.StartsWith("Waiting for Roblox")) sawPending = true; }, CancellationToken.None);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!sawPending && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert(sawPending && !pending.IsCompleted, "completion command cannot bypass native group-membership confirmation");
            nativeMember = true;
            result = await pending;
            Assert(result.Outcome == BrowserCodeOutcome.Completed && nativeMemberChecks >= 2, "confirmed membership releases the account for next alt");

            nativeMember = browserMember = false; joinPosts = 0; joinMode = "join";
            browser = await Fixture();
            result = await browser.RunCode(Request(BrowserCodeRuntime.JoinGroupExample(42), 15, 42), _ => { }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Completed && joinPosts == 2, "generated example joins with exactly one CSRF retry and confirms membership");
            joinPosts = 0;
            browser = await Fixture();
            result = await browser.RunCode(Request(BrowserCodeRuntime.JoinGroupExample(42), 10, 42), _ => { }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Completed && joinPosts == 0, "already-member accounts complete without another join request");
            nativeMember = browserMember = false; joinPosts = 0; joinMode = "pending";
            browser = await Fixture();
            result = await browser.RunCode(Request(BrowserCodeRuntime.JoinGroupExample(42), 2, 42), _ => { }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Failed && joinPosts == 1, "successful pending request is never mislabeled joined or replayed");
            nativeMember = browserMember = false; joinPosts = 0; joinMode = "rate";
            browser = await Fixture();
            result = await browser.RunCode(Request(BrowserCodeRuntime.JoinGroupExample(42), 5, 42), _ => { }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Stopped && joinPosts == 1, "join rate limit stops the example without repeated attempts");
            joinPosts = 0; nativeWrongIdentity = true;
            browser = await Fixture();
            result = await browser.RunCode(Request(BrowserCodeRuntime.JoinGroupExample(42), 5, 42), _ => { }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Failed && joinPosts == 0 && account.SessionStatus.StartsWith("Wrong account"), "wrong browser identity blocks code before any join request");
            nativeWrongIdentity = false; nativeExpired = true;
            browser = await Fixture();
            result = await browser.RunCode(Request(BrowserCodeRuntime.JoinGroupExample(42), 5, 42), _ => { }, CancellationToken.None);
            Assert(result.Outcome == BrowserCodeOutcome.Failed && joinPosts == 0 && account.SessionStatus.StartsWith("Expired"), "expired code session updates account status and performs no join");
            Assert(hub.Count == 0, "code runner cleans up its browser after every result");
        }
        finally { hub.CloseAll(); }
        return passed;
    }

    private static WebView2? FindWeb(DependencyObject node)
    {
        if (node is WebView2 web) return web;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject obj && FindWeb(obj) is WebView2 found) return found;
        return null;
    }
    private static T? FindNamed<T>(DependencyObject node, string name) where T : FrameworkElement
    {
        if (node is T target && target.Name == name) return target;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject obj && FindNamed<T>(obj, name) is T found) return found;
        return null;
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(handle(request)); }
    private sealed class HarnessApp : Application
    {
        public Func<Task> Check { get; set; } = null!;
        public int Result { get; private set; } = 1;
        protected override async void OnStartup(StartupEventArgs e)
        {
            // Skip the production startup and default data directory entirely.
            try { await Check(); Result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { Shutdown(); }
        }
    }
}
