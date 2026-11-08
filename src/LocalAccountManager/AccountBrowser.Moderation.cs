using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace LocalAccountManager;

public sealed partial class AccountBrowser
{
    private ulong navigationId;
    internal event Action<Account>? WarningDetected;
    internal bool IsOnModerationPage => !disposed && !IsGuest && ShownForUser && !signupControlsBrowser &&
        Uri.TryCreate(web.CoreWebView2?.Source, UriKind.Absolute, out var uri) && Security.IsRobloxWebsite(uri) &&
        uri.AbsolutePath.TrimEnd('/').Equals("/not-approved", StringComparison.OrdinalIgnoreCase);

    private const string WarningScript = """
        (() => {
          const lines = document.body?.innerText.split(/\r?\n/).map(line => line.trim().toLowerCase()) ?? [];
          if (!lines.includes('warning') || !lines.includes('what happened') ||
              !lines.some(line => line.startsWith('moderator note:'))) return false;
          return [...document.querySelectorAll('button, [role="button"]')].some(button =>
            button.innerText.trim().toLowerCase() === 'continue' && button.getClientRects().length > 0 &&
            getComputedStyle(button).visibility === 'visible');
        })()
        """;

    private async Task CheckBrowserWarning(CoreWebView2 core, ulong pageId)
    {
        string trustedCookie = account.SessionCookie;
        bool CurrentPage() => WarningDetected is not null && !loginMode && !IsCodeRunning && pageId == navigationId &&
            IsOnModerationPage && account.UserId > 0 && trustedCookie.Length > 0 && account.SessionCookie == trustedCookie;

        if (!CurrentPage()) return;
        var cancel = closedCancellation.Token;
        var elapsed = Stopwatch.StartNew();
        try
        {
            // Roblox renders the notice after navigation finishes. Observe the page without interacting with it.
            while (elapsed.Elapsed < TimeSpan.FromSeconds(15) && CurrentPage())
            {
                if (await core.ExecuteScriptAsync(WarningScript).WaitAsync(TimeSpan.FromSeconds(3), cancel) == "true")
                {
                    if (CurrentPage() && await ReadSession().WaitAsync(TimeSpan.FromSeconds(3), cancel) == trustedCookie && CurrentPage())
                        WarningDetected?.Invoke(account);
                    return;
                }
                await Task.Delay(350, cancel);
            }
        }
        catch { /* Browser navigation and closing can interrupt this optional observation. */ }
    }
}
