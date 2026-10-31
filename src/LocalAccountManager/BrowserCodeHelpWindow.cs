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

        const solver = ram.twocaptcha.register('YOUR_2CAPTCHA_KEY', {userAgent:navigator.userAgent});
        // Or use Anti-Captcha without proxy settings:
        const anti = ram.anticaptcha.register('YOUR_ANTICAPTCHA_KEY');
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
          Resolves to {success, token, solution, errorCode, message}. A token is a provider result, not confirmation that Roblox accepted it. Tokens are not automatically inserted into the page. Failure includes a short code; there are no automatic paid retries. Stop/close cancels pending requests, but cannot undo a task already submitted to a provider. At most four requests may be pending; Solvex solves time out after two minutes; 2Captcha/Anti-Captcha solves after five minutes. Those providers are polled every five seconds.

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
        • 2Captcha and Anti-Captcha support FunCaptchaTaskProxyless without your own proxy. Supplying proxyType/proxyAddress/proxyPort switches to FunCaptchaTask. Anti-Captcha proxy mode requires a public IP address and a userAgent option.
        • Solver helpers currently accept FunCaptcha tasks only. Other detected CAPTCHA types return UNSUPPORTED_CAPTCHA_TYPE before a paid request. A returned token may still be rejected by Roblox due to session/IP restrictions.
        • Open Examples for separate 2Captcha, Anti-Captcha, and Solvex token examples and create + save examples.
        • Providers may charge for submitted tasks. Registration is local; status calls the provider balance endpoint. Provider billing is outside the manager's control.
        • API keys and proxy credentials exist in your running script. Only use scripts/pages you trust. The editor clears on close. The app does not save solver keys, send saved Roblox passwords/cookies, follow solver redirects, or accept custom API hosts/callback URLs.

        CREATE ACCOUNTS IN THE VISIBLE BROWSER

        const saved = await ram.createAccount({
          provider: '2captcha', // '2captcha', 'anticaptcha', or 'solvex'
          username: 'YOUR_USERNAME', password: 'YOUR_PASSWORD',
          birthday: 'YYYY-MM-DD', acceptTerms: false,
          apiKey: 'YOUR_PROVIDER_API_KEY',
          proxy: null // Required proxy object for Solvex; optional for the other two.
        });

        Guest mode only (no account selected). Await each signup before starting another. Fill in your own details and real birthday. Read Roblox's terms/privacy policy before setting acceptTerms to true. The Examples button includes this script. It does not run until you choose Run.

        Choose the provider explicitly; an omitted provider preserves the old Solvex behavior. No provider fallback is made. With proxy:null, 2Captcha and Anti-Captcha use their own solver connections while Roblox signup uses your normal connection. With a proxy object, Roblox and the solver use that same proxy. Solvex requires your proxy. Proxyless results may still be rejected by Roblox.

        The helper opens a fresh private browser session for each account, checks the username with Roblox, then fills its visible signup form and clicks Sign Up once. Taken, inappropriate, private-information, and unavailable usernames reject the promise before signup or a solver task. This read-only username request includes your chosen username/birthday, without passwords, session cookies, or solver keys. Later username errors in the form are also detected. Roblox's own page handles its verification protocol, including captchav2. Complete any verification shown in that window. Only after Arkose reports onShown, with a readable callback and blob, the app requests at most one provider solution and passes it to that same page callback. The current configured site key is used, including a page override. ERROR_CAPTCHA_UNSOLVABLE is a provider failure, not proof that the local Start puzzle button needed clicking. The provider loads its own challenge. Click Start puzzle to finish manually if needed. A rejected/unsupported result stays open for manual completion; it never starts another paid task automatically.

        Only an authenticated cookie from that visible browser, independently verified as your expected username, can save the password/cookie using your active vault protection. The promise resolves after saving, with success, saved, userId, and username only. The cookie is never returned to JavaScript. No extra ram.addAccount call is needed. The app never sends a separate native registration request.

        Multiple accounts are supported sequentially. Open Examples → Create 3 accounts, or put await ram.createAccount(options) inside your existing loop. Each success is saved before the next begins. Concurrent requests are rejected. A failed signup, Stop, close, or timeout stops the run; do not catch an error and blindly retry it. Choose enough editor timeout for all signups and any manual verification.

        Your script waits in its original document while the visible signup view navigates. After creation, use the returned identity for status/loops; DOM operations still refer to the original document. To automate the new account's page, finish this run and select the saved account for a new browser-code run.

        A network error or Stop cannot undo a signup already received by Roblox. Check the username before retrying an uncertain result. If creation succeeds but vault saving fails, sign in through Add account to save it. The browser flow is tested with synthetic accounts and provider responses. Live Roblox/provider acceptance is not guaranteed.

        Official provider documentation:
        https://solvex.run/docs
        https://2captcha.com/api-docs/arkoselabs-funcaptcha
        https://anti-captcha.com/apidoc/task-types/FunCaptchaTaskProxyless

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
        • Ordinary script navigation to a new document stops the run. The createAccount helper manages its own separate visible view. The app does not automatically replay your script after navigation.
        • Set the timeout per account in the editor (1–120 minutes).
        • The bulk sign-in Skip account button belongs to login queues, not JavaScript queues.

        GROUP EXAMPLE

        Open Examples, select Join a group, and choose Use example. Set groupId in the copied code and enter the matching group/community ID in native membership confirmation. Review the script before running. Pending approval is not a successful join; verification and approval may need your action in Roblox. This example does not make payouts.

        PERMISSIONS

        Your code runs with the selected Roblox website session's permissions. It can perform account actions and browser requests. Only run code you trust. Already-sent requests cannot be undone by Stop.

        ram contains identity and queue helpers, not your saved password or session cookie. Native host objects and general web-message bridges remain disabled. Explicit solver, signup, and account-save requests use a bounded polling channel. Solver calls use fixed provider hosts; signup is submitted by the visible Roblox page. Only supply the new signup password or addAccount password when explicitly needed; scripts and the page can see supplied secrets. Never include existing session cookies or unrelated private data.
        """;
}
