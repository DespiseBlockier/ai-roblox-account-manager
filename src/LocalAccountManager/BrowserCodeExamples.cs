namespace LocalAccountManager;

public sealed record BrowserCodeExample(string Name, string Description, string Code, bool RequiresAccount = false, bool Group = false, bool GuestOnly = false);

public static class BrowserCodeExamples
{
    public static IReadOnlyList<BrowserCodeExample> All =>
    [
        new("Account starter", "Runs once for each selected saved account, then closes and advances.", BrowserCodeRuntime.Starter, RequiresAccount: true),
        new("Guest starter", "Runs once in a fresh private session. No saved account is restored.", BrowserCodeRuntime.GuestStarter, GuestOnly: true),
        new("Save current account", "Requires an already authenticated browser session. Omit the password to preserve an existing saved password. This does not create a Roblox account.", """
            // Call after the browser is signed in. This saves immediately.
            // Set password to a string if you want to save it locally.
            const password = undefined;
            const saved = await ram.addAccount(password);
            ram.status('Saved @' + saved.username);
            ram.done();
            """),
        new("Create account and save", "Creates one account in guest mode, solves a signup CAPTCHA with Solvex if requested, verifies the new session, and saves its password and cookie to your vault. Fill in your own details, real birthday, API key, and proxy. Solvex may charge. Unsupported verification stops the run. Tested with simulated responses; live signup has not been verified.", """
            // Open this editor with NO account selected.
            // Read https://en.help.roblox.com/hc/en-us/articles/115004647846
            // and https://en.help.roblox.com/hc/en-us/articles/115004630823 first.
            const options = {
              username: 'YOUR_USERNAME',
              password: 'YOUR_PASSWORD',
              birthday: 'YYYY-MM-DD', // Your real birthday.
              acceptTerms: false, // Set true only after reading and accepting Roblox's terms.
              apiKey: 'YOUR_SOLVEX_KEY',
              proxy: {
                type: 'http', address: 'YOUR_PROXY_HOST', port: 8080,
                login: '', password: '' // Optional proxy authentication.
              }
            };
            ram.status('Creating one account…');
            // Includes CAPTCHA detection, solution submission, session verification,
            // and vault save. Do not loop: check a failed/uncertain signup before retrying.
            const saved = await ram.createAccount(options);
            ram.status('Created and saved @' + saved.username);
            ram.done();
            """, GuestOnly: true),
        new("CAPTCHA solver", "Fill in your own Solvex key and proxy. Requests at most one solution. A returned token is not automatically submitted to Roblox; this is not a complete signup script.", """
            const apiKey = 'YOUR_SOLVEX_KEY';
            const proxy = {proxyType:'http', proxyAddress:'YOUR_PROXY_HOST', proxyPort:8080};
            if (apiKey.startsWith('YOUR_') || proxy.proxyAddress.startsWith('YOUR_')) {
              throw new Error('Fill in your Solvex key and proxy before running.');
            }
            const solver = ram.solvex.register(apiKey, proxy);
            if (!await solver.status()) throw new Error('Key check failed or provider unavailable.');
            let captcha = null;
            for (let i = 0; i < 120 && !captcha; i++) {
              captcha = ram.fetchcaptcha();
              if (!captcha) await ram.sleep(1000);
            }
            if (!captcha) { ram.status('No readable CAPTCHA appeared.'); return; }
            const result = await solver.solvecaptcha(captcha);
            if (!result.success) { ram.status(result.errorCode); return; }
            ram.status('Provider returned a token. Website submission and acceptance are still required.');
            // result.token is available here. Do not log it or assume the site accepted it.
            // Returning keeps this document open; Stop closes the browser.
            """),
        new("Join a group", "Set groupId in the code and enter the same ID in membership confirmation. Pending approval is not membership. Requires a selected account.",
            BrowserCodeRuntime.JoinGroupExample(1).Replace("const groupId = 1;", "const groupId = 0; // Replace 0 with your community ID."), RequiresAccount: true, Group: true)
    ];
}
