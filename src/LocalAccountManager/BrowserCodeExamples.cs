namespace LocalAccountManager;

public sealed record BrowserCodeExample(string Name, string Description, string Code, bool RequiresAccount = false, bool Group = false, bool GuestOnly = false);

public static class BrowserCodeExamples
{
    private const string SignupDescription = "Creates one account, solves a signup FunCaptcha if requested, verifies its session, and saves the password/cookie to your vault. Fill in your own details, real birthday, and provider key; accept Roblox's terms before running. 2Captcha and Anti-Captcha default to proxyless mode; Solvex needs your proxy. Provider charges may apply. Tested with simulated responses; live signup has not been verified.";
    public static IReadOnlyList<BrowserCodeExample> All =>
    [
        new("Account starter", "Runs once for each selected saved account, then closes and advances.", BrowserCodeRuntime.Starter, RequiresAccount: true),
        new("Guest starter", "Runs once in a fresh private session. No saved account is restored.", BrowserCodeRuntime.GuestStarter, GuestOnly: true),
        new("Save current account", "Requires an already authenticated browser session. Omit the password to preserve an existing saved password. This does not create a Roblox account.", """
            // Call after the browser is signed in. This saves immediately.
            const password = undefined; // Set a string to save a password locally.
            const saved = await ram.addAccount(password);
            ram.status('Saved @' + saved.username);
            ram.done();
            """),
        new("Create account and save", SignupDescription, SignupExample("2captcha"), GuestOnly: true),
        new("2Captcha: create + save", SignupDescription, SignupExample("2captcha"), GuestOnly: true),
        new("Anti-Captcha: create + save", SignupDescription, SignupExample("anticaptcha"), GuestOnly: true),
        new("Solvex: create + save", SignupDescription, SignupExample("solvex"), GuestOnly: true),
        new("2Captcha CAPTCHA", "Uses 2Captcha's proxyless FunCaptcha task. Set your API key. Requests one solution and returns its token to your script. Does not submit it, create an account, or save to the vault.", SolverExample("twocaptcha")),
        new("Anti-Captcha CAPTCHA", "Uses Anti-Captcha's proxyless FunCaptcha task. Set your API key. Requests one solution and returns its token to your script. Does not submit it, create an account, or save to the vault.", SolverExample("anticaptcha")),
        new("Solvex CAPTCHA", "Set your Solvex key and proxy. Requests one FunCaptcha solution. Does not submit it, create an account, or save to the vault.", SolverExample("solvex")),
        new("Join a group", "Set groupId in the code and enter the same ID in membership confirmation. Pending approval is not membership. Requires a selected account.",
            BrowserCodeRuntime.JoinGroupExample(1).Replace("const groupId = 1;", "const groupId = 0; // Replace 0 with your community ID."), RequiresAccount: true, Group: true)
    ];

    private static string SignupExample(string provider) => """
        // Open this editor with NO account selected.
        // Read Roblox's terms and privacy policy before setting acceptTerms to true:
        // https://en.help.roblox.com/hc/en-us/articles/115004647846
        // https://en.help.roblox.com/hc/en-us/articles/115004630823
        const options = {
          provider: 'PROVIDER', // '2captcha', 'anticaptcha', or 'solvex'.
          apiKey: 'YOUR_PROVIDER_API_KEY',
          username: 'YOUR_USERNAME',
          password: 'YOUR_PASSWORD',
          birthday: 'YYYY-MM-DD', // Your real birthday.
          acceptTerms: false,
          proxy: /* PROXY_VALUE */
        };
        // 2Captcha/Anti-Captcha: null uses their proxyless solver and your normal connection.
        // Solvex: replace null with your proxy object (also optional for the other providers):
        // {type:'http', address:'YOUR_PROXY_HOST', port:8080, login:'', password:''}
        // Anti-Captcha proxy mode requires a public proxy IP, not a hostname/local IP.
        ram.status('Creating one account…');
        // Detects CAPTCHA, submits one solution, verifies identity, then saves to the vault.
        // No automatic retries/fallback providers. Check an uncertain result before retrying.
        const saved = await ram.createAccount(options);
        ram.status('Created and saved @' + saved.username);
        ram.done();
        """.Replace("'PROVIDER'", "'" + provider + "'").Replace("/* PROXY_VALUE */", provider == "solvex" ? "{type:'http', address:'YOUR_PROXY_HOST', port:8080, login:'', password:''}" : "null");

    private static string SolverExample(string helper) => """
        // FunCaptcha token example; use a create + save example for complete signup.
        const apiKey = 'YOUR_PROVIDER_API_KEY';
        if (!apiKey || apiKey.startsWith('YOUR_')) throw new Error('Fill in your provider API key.');
        const solver = ram.HELPER.register(apiKey, OPTIONS);
        if (!await solver.status()) throw new Error('Key check failed or provider unavailable.');
        let captcha = null;
        for (let i = 0; i < 120 && !captcha; i++) {
          captcha = ram.fetchcaptcha();
          if (!captcha) await ram.sleep(1000);
        }
        if (!captcha) { ram.stop('No readable CAPTCHA appeared.'); return; }
        const result = await solver.solvecaptcha(captcha);
        if (!result.success) { ram.stop(result.errorCode); return; }
        ram.status('Provider returned a token. Website submission and acceptance are still required.');
        // result.token is available here for page-specific submission. Do not log it.
        // Returning keeps this document open; Stop closes the browser.
        """.Replace("HELPER", helper).Replace("OPTIONS", helper switch
        {
            "solvex" => "{proxyType:'http', proxyAddress:'YOUR_PROXY_HOST', proxyPort:8080}",
            "twocaptcha" => "{userAgent:navigator.userAgent}", _ => "{}"
        });
}
