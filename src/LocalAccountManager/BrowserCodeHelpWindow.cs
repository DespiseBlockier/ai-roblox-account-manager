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

        The manager runs your JavaScript once for each selected account, after verifying that browser's signed-in identity. It runs inside an async function, so you can use await directly.

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
          The selected Roblox username (a string).

        ram.account.userId
          The selected Roblox user ID (a string, so large IDs do not lose precision).

        ram.signal
          An AbortSignal for cancellation-aware browser calls.

          Example:
          const response = await fetch('/some-page', {
            credentials: 'include', signal: ram.signal
          });

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

        Enter a community ID or Roblox community link and choose Generate join-group example. Review the script before running. Keep native membership confirmation enabled. Pending approval is not a successful join; verification and approval may need your action in Roblox. This example does not make payouts.

        PERMISSIONS

        Your code runs with the selected Roblox website session's permissions. It can perform account actions and browser requests. Only run code you trust. Already-sent requests cannot be undone by Stop.

        ram contains identity and queue helpers, not your saved password or session cookie. Native host objects and web-message bridges are disabled. Do not paste credentials or private data into scripts.
        """;
}
