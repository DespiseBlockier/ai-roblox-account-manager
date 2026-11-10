using Microsoft.Playwright;

namespace LocalAccountManager.Linux;

internal sealed class LinuxBrowser(RobloxApi api)
{
    private const string Roblox = "https://www.roblox.com/";
    internal const string Home = Roblox + "home";
    private const string Login = Roblox + "login";
    private const string SessionName = ".ROBLOSECURITY";

    internal Task Browse(string url, string cookie, CancellationToken stop) =>
        WithPage(url, cookie, async (page, _) =>
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Close += (_, _) => closed.TrySetResult();
            if (!page.IsClosed) await closed.Task.WaitAsync(stop);
            return true;
        }, stop);

    internal Task<bool> SignIn(Account? expected, LoginCredential? credential,
        Action<RobloxIdentity, string> save, Action<string> status, CancellationToken stop) =>
        WithPage(Login, "", async (page, context) =>
        {
            string attempt = LoginAutofill.NewAttemptKey();
            string attemptedCookie = "";
            bool filled = false;
            bool submitted = false;
            while (!page.IsClosed)
            {
                stop.ThrowIfCancellationRequested();
                if (!submitted && credential is { Password.Length: > 0 } && LoginAutofill.IsLoginPage(page.Url))
                {
                    try
                    {
                        if (!filled)
                            filled = await page.EvaluateAsync<bool>(
                                LoginAutofill.Script(credential.Username, credential.Password)).WaitAsync(stop);
                        if (filled)
                            submitted = await page.EvaluateAsync<bool>(
                                LoginAutofill.SubmitScript(attempt, credential.Username, credential.Password)).WaitAsync(stop);
                    }
                    catch (PlaywrightException) when (!page.IsClosed)
                    {
                        // Login can navigate while the form is being filled.
                    }
                }
                var cookies = await context.CookiesAsync([Roblox]).WaitAsync(stop);
                var cookie = cookies.FirstOrDefault(c => c.Name == SessionName && c.Secure && c.HttpOnly &&
                    c.Domain is ".roblox.com" or "www.roblox.com" or "roblox.com" && c.Path == "/");
                if (cookie is not null && cookie.Value != attemptedCookie)
                {
                    attemptedCookie = cookie.Value;
                    try
                    {
                        var identity = await api.Identity(cookie.Value).WaitAsync(stop);
                        if (expected is { UserId: > 0 } && expected.UserId != identity.Id ||
                            credential is not null && !credential.Username.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            status("Wrong account. Sign out in Chromium, then sign in with the queued account.");
                        }
                        else
                        {
                            stop.ThrowIfCancellationRequested();
                            save(identity, cookie.Value);
                            return true;
                        }
                    }
                    catch (RobloxApiError ex) when (ex.StatusCode is 401 or 403)
                    {
                        status("Complete Roblox verification in Chromium to save this account.");
                        await Task.Delay(TimeSpan.FromSeconds(5), stop);
                        attemptedCookie = "";
                    }
                }
                await Task.Delay(800, stop);
            }
            return false;
        }, stop);

    private static async Task<T> WithPage<T>(string url, string cookie,
        Func<IPage, IBrowserContext, Task<T>> action, CancellationToken stop)
    {
        if (!Security.IsBrowserPage(url)) throw new UserError("Open a secure Roblox URL.");
        var executable = FindChromium();
        using var driver = await Playwright.CreateAsync();
        stop.ThrowIfCancellationRequested();
        try
        {
            await using var browser = await driver.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                ExecutablePath = executable, Headless = false, ChromiumSandbox = true, Timeout = 20000
            });
            stop.ThrowIfCancellationRequested();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                AcceptDownloads = false, ServiceWorkers = ServiceWorkerPolicy.Block
            });
            await context.RouteAsync("**/*", route =>
            {
                var request = route.Request;
                return request.IsNavigationRequest && !Security.IsBrowserPage(request.Url) && IsMainFrame(request)
                    ? route.AbortAsync() : route.ContinueAsync();
            });
            if (cookie.Length > 0)
            {
                await context.AddCookiesAsync([new Cookie
                {
                    Name = SessionName, Value = cookie, Domain = ".roblox.com", Path = "/", Secure = true, HttpOnly = true
                }]);
            }
            var page = await context.NewPageAsync();
            try
            {
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25000
                }).WaitAsync(stop);
                return await action(page, context);
            }
            catch (PlaywrightException) when (page.IsClosed || !browser.IsConnected) { return default!; }
        }
        catch (PlaywrightException)
        {
            throw new UserError("Chromium could not open the isolated Roblox session. Check that Chromium runs with its sandbox on this desktop.");
        }
    }

    private static bool IsMainFrame(IRequest request)
    {
        try { return request.Frame.ParentFrame is null; }
        catch (PlaywrightException) { return true; }
    }

    private static string FindChromium()
    {
        string? configured = Environment.GetEnvironmentVariable("RAM_CHROMIUM_PATH");
        if (configured is not null)
        {
            if (Path.IsPathFullyQualified(configured) && File.Exists(configured)) return configured;
            throw new UserError("RAM_CHROMIUM_PATH must point to an installed Chromium executable.");
        }
        string[] paths = ["/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome-stable", "/usr/bin/google-chrome"];
        return paths.FirstOrDefault(File.Exists) ?? throw new UserError("Install Chromium to open browser sessions.");
    }
}
