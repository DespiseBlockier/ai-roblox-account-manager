using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalAccountManager;

public sealed class BrowserCodeHelpWindow : Window
{
    public BrowserCodeHelpWindow(Window owner)
    {
        Owner = owner; Style = (Style)FindResource(typeof(Window));
        Title = "JavaScript docs · ram helpers"; Width = 820; Height = 720; MinWidth = 620; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        var heading = new TextBlock { Text = "JavaScript + ram quick reference", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        root.Children.Add(new TextBox
        {
            Name = "JavaScriptDocs", Text = Reference, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(14)
        });
        Content = root;
    }

    public const string Reference = """
        HOW YOUR SCRIPT RUNS

        The manager runs your JavaScript once for each selected account after verifying its signed-in identity. With no selection it runs once in a fresh private guest browser, without opening or saving a managed account. ram.account is null in guest mode. You can use await directly.

        ram is a helper object supplied to your script by this app. You do not install or import it. It is not a Roblox API and is not a global object in an ordinary browser console.

        QUICK START

        ram.status('Opened @' + ram.account.username);
        await ram.sleep(1000);
        ram.done();
        return;

        COMMANDS

        ram.done()
          Requests completion: close this account's browser and advance to the next account. If group confirmation is enabled, the manager independently confirms membership before advancing.

          This is a synchronous function; it is not necessary to await it. Use return afterward if you have more code below it.

        ram.stop(reason)
          Close the current browser and stop the entire queue. Remaining accounts do not run. The reason is optional.

          Example: ram.stop('This account needs manual verification');

        ram.status(message)
          Show a short progress message in the manager. It does not save a note or mark the account complete.

        await ram.sleep(milliseconds)
          Pause without blocking the page. Each pause is limited to five minutes (300000 ms). Stop or completion cancels a pending sleep.

        ram.account.username
          The selected Roblox username (a string). Check ram.account first in guest mode.

        ram.account.userId
          The selected Roblox user ID (a string, so large IDs do not lose precision).

        ram.signal
          An AbortSignal for cancellation-aware browser calls.

          Example:
          const response = await fetch('/some-page', {
            credentials: 'include', signal: ram.signal
          });

        await ram.addAccount(optionalPassword)
          Save the browser's current authenticated Roblox account to the manager. The native app reads its session cookie and verifies the identity with Roblox. Only {success, userId, username} is returned to JavaScript; the cookie is never returned.

          const saved = await ram.addAccount('password-you-want-to-save');
          ram.status('Saved @' + saved.username);
          ram.done();

          Omit the argument to preserve an existing saved password, or leave a new account without a saved password. A supplied password is stored locally, not validated or changed on Roblox. Existing accounts are matched by verified user ID, preserving their group, notes, and other settings. Storage follows Vault security. Saving happens before the promise resolves; Stop does not undo a completed save.

          This requires an already authenticated session in the current document. A signed-out/expired session rejects without adding a placeholder. A selected-account browser cannot save a different user's session; use guest mode for a different account. Navigation stops the script, so this function alone is not a complete automatic signup workflow.

        SOLVER HELPERS

        const solver = ram.capsolver.register('YOUR_API_KEY');
        // Or register Solvex with your own proxy:
        const solver2 = ram.solvex.register('YOUR_API_KEY', {
          proxyType: 'http', proxyAddress: 'YOUR_PROXY_HOST', proxyPort: 8080,
          proxyLogin: 'YOUR_PROXY_USER', proxyPassword: 'YOUR_PROXY_PASSWORD'
        });

        const valid = await solver.status();
          Resolves to true when the provider recognizes the key, including zero-balance accounts. False means it rejected the key or the check failed (for example, no network).

        const captcha = ram.fetchcaptcha();
          Reads supported visible widget/iframe configuration (reCAPTCHA v2, Turnstile, or FunCaptcha). Returns a task descriptor or null when configuration is not readable. It does not read saved credentials or export the browser cookie jar. ram.featchcaptcha() is an alias for the requested spelling. A provider-specific task object can also be passed explicitly; websiteURL must be a Roblox HTTPS page.

        const result = await solver.solvecaptcha(captcha);
          Resolves to {success, token, solution, errorCode, message}. A token is a provider result, not confirmation that Roblox accepted it. Tokens are not automatically inserted into the page. Failure includes a short code; there are no automatic paid retries. Stop/close cancels pending requests, but cannot undo a task already submitted to a provider. At most four requests may be pending; solves time out after two minutes.

        if (!await solver2.status()) {
          ram.stop('Key check failed or provider unavailable');
          return;
        }
        const result = await solver2.solvecaptcha(ram.fetchcaptcha());
        if (!result.success) {
          ram.stop(result.errorCode);
          return;
        }
        ram.status('Provider returned a solution; website acceptance is still required');
        // Handle result.token using the website's normal flow. Do not log it.

        Provider limitations:
        • Solvex requires a proxy and the current challenge blob (data). Missing options fail before a paid request. Unknown site keys or session-bound challenges may require manual verification.
        • CapSolver's current task catalogue does not list FunCaptcha. Those tasks return CAPSOLVER_FUNCAPTCHA_UNSUPPORTED. Use its documented supported tasks instead.
        • Providers may charge for submitted tasks. Registration is local; status calls the provider balance endpoint. Provider billing is outside the manager's control.
        • API keys and proxy credentials exist in your running script. Only use scripts/pages you trust. The editor clears on close. The app does not save solver keys, send saved Roblox passwords/cookies, follow solver redirects, or accept custom API hosts/callback URLs.

        Official provider documentation:
        https://solvex.run/docs
        https://docs.capsolver.com/en/guide/api-tasktype/

        WAIT FOR SOMETHING ON THE CURRENT PAGE

        for (let attempt = 0; attempt < 20; attempt++) {
          if (document.querySelector('main')) {
            ram.status('The page is ready');
            ram.done();
            return;
          }
          await ram.sleep(500);
        }
        ram.stop('The page did not become ready');

        NORMAL JAVASCRIPT

        const declares a value, if tests a condition, and for repeats work. document.querySelector(...) finds a page element. fetch(...) makes a normal browser request; check response.ok before treating it as successful. These are standard browser features, not ram commands.

        QUEUE BEHAVIOR

        • Returning from your script alone does not advance. Call ram.done() explicitly.
        • ram.stop(), the Stop queue button, a script error, timeout, or closing the browser stops the queue.
        • Navigating to a new document stops the run. The app does not automatically replay your script after navigation.
        • Set the timeout per account in the editor (1–120 minutes).
        • The bulk sign-in Skip account button belongs to login queues, not JavaScript queues.

        GROUP EXAMPLE

        Open Examples, select Join a group, and choose Use example. Set groupId in the copied code and enter the matching group/community ID in native membership confirmation. Review the script before running. Pending approval is not a successful join; verification and approval may need your action in Roblox. This example does not make payouts.

        PERMISSIONS

        Your code runs with the selected Roblox website session's permissions. It can perform account actions and browser requests. Only run code you trust. Already-sent requests cannot be undone by Stop.

        ram contains identity and queue helpers, not your saved password or session cookie. Native host objects and general web-message bridges remain disabled. Explicit solver and account-save requests use a bounded polling channel to the fixed official API hosts. Never paste Roblox passwords/cookies or unrelated private data into scripts.
        """;
}
