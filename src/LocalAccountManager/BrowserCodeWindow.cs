using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace LocalAccountManager;

public sealed class BrowserCodeRow(Account account, bool guest = false)
{
    public Account Account { get; } = account;
    public bool IsGuest => guest;
    public string Username => IsGuest ? "Guest browser" : "@" + Account.Username;
    public string Result { get; set; } = "Queued";
    public string Message { get; set; } = "";
}

public sealed class BrowserCodeWindow : Window
{
    private readonly TextBox url = new() { Text = "https://www.roblox.com/home", MaxLength = 4096 };
    private readonly TextBox group = new() { Name = "GroupInput", MaxLength = 4096, MinWidth = 200 };
    private readonly TextBox minutes = new() { Text = "10", Width = 65, MaxLength = 3 };
    private readonly JavaScriptEditor code = new() { Name = "ScriptEditor", Text = BrowserCodeRuntime.Starter };
    private readonly CheckBox confirmGroup = new() { Content = "Confirm actual membership in this group before advancing" };
    private BrowserCodeHelpWindow? docs;
    private BrowserCodeExamplesWindow? examplesWindow;
    private readonly Button start = new() { Name = "StartCodeRun", Content = "Run on selected accounts" };
    private readonly Button stop = new() { Content = "Stop queue", IsEnabled = false };
    private readonly DataGrid results = new() { RowHeight = double.NaN, MinRowHeight = 42, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock feedback = new() { Name = "RunFeedback", Text = "Ready. Code runs with the selected Roblox session's permissions. Run code you trust.", TextWrapping = TextWrapping.Wrap };
    private readonly ObservableCollection<BrowserCodeRow> rows;
    private readonly BrowserCodeQueue queue = new();
    private CancellationTokenSource? cancellation;
    private readonly BrowserHub browsers;
    private readonly Func<string?> canStart;
    private bool groupExampleLoaded;
    public bool IsRunning { get; private set; }
    public bool WasClosed { get; private set; }
    public event EventHandler? QueueEnded;

    public BrowserCodeWindow(Window owner, IReadOnlyList<Account> accounts, BrowserHub browsers, Func<string?>? canStart = null)
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner; this.browsers = browsers; this.canStart = canStart ?? (() => null);
        Title = "Open browser with code · " + accounts.Count + " selected accounts";
        Width = 1000; Height = 900; MinWidth = 800; MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        rows = new(accounts.Select(a => new BrowserCodeRow(a)));
        if (accounts.Count == 0)
        {
            rows.Add(new BrowserCodeRow(new Account { Username = "", DisplayName = "Guest" }, guest: true));
            Title = "Open browser with code · Guest browser"; code.Text = BrowserCodeRuntime.GuestStarter;
            start.Content = "Run in guest browser"; confirmGroup.IsEnabled = group.IsEnabled = false;
            feedback.Text = "Fresh private guest session. ram.account is null. Saved accounts are not restored. ram.addAccount() saves only when your code explicitly calls it.";
        }
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = new GridLength(150) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        var titleLine = new DockPanel();
        var help = new Button { Name = "JavaScriptDocsButton", Content = "JS docs", Margin = new Thickness(6, 0, 0, 0) };
        var showExamples = new Button { Name = "JavaScriptExamplesButton", Content = "Examples", Margin = new Thickness(6, 0, 0, 0) };
        help.Click += (_, _) =>
        {
            if (docs is not null) { docs.Activate(); return; }
            docs = new BrowserCodeHelpWindow(this); docs.Closed += (_, _) => docs = null; docs.Show();
        };
        showExamples.Click += (_, _) =>
        {
            if (examplesWindow is not null) { examplesWindow.Activate(); return; }
            examplesWindow = new BrowserCodeExamplesWindow(this, rows[0].IsGuest, () => IsRunning, example =>
            {
                if (IsRunning) return;
                code.Text = example.Code; groupExampleLoaded = example.Group; confirmGroup.IsChecked = example.Group;
                if (example.Group) group.Clear();
                feedback.Text = "Example copied into the editor. Review its settings before running; nothing has run yet.";
                Activate(); code.Focus();
            });
            examplesWindow.Closed += (_, _) => examplesWindow = null; examplesWindow.Show();
        };
        var helpButtons = new StackPanel { Orientation = Orientation.Horizontal }; helpButtons.Children.Add(help); helpButtons.Children.Add(showExamples);
        DockPanel.SetDock(helpButtons, Dock.Right); titleLine.Children.Add(helpButtons);
        titleLine.Children.Add(new TextBlock { Text = accounts.Count == 0 ? "JavaScript · Guest browser" : "JavaScript · " + accounts.Count + " selected account(s)", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(titleLine);
        header.Children.Add(new TextBlock { Text = "ram.done() closes the current browser and advances. ram.stop() closes it and stops the queue. Closing a browser also stops the queue.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        header.Children.Add(new TextBlock { Text = "Starting Roblox URL", Margin = new Thickness(0, 0, 0, 5) }); header.Children.Add(url);
        var time = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        time.Children.Add(new TextBlock { Text = "Timeout per account (minutes)  ", VerticalAlignment = VerticalAlignment.Center }); time.Children.Add(minutes);
        time.Children.Add(new TextBlock { Text = "  Run only code you trust.", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }); header.Children.Add(time);
        root.Children.Add(header);
        var editor = new DockPanel { Margin = new Thickness(0, 8, 0, 12) };
        var verification = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        verification.Children.Add(confirmGroup); verification.Children.Add(new TextBlock { Text = "Group/community ID or link · optional membership confirmation", Margin = new Thickness(0, 0, 0, 5) });
        verification.Children.Add(group); DockPanel.SetDock(verification, Dock.Bottom); editor.Children.Add(verification); editor.Children.Add(code);
        Grid.SetRow(editor, 1); root.Children.Add(editor);
        results.Columns.Add(new DataGridTextColumn { Header = "ACCOUNT", Binding = new Binding("Username"), Width = 160 });
        results.Columns.Add(new DataGridTextColumn { Header = "RESULT", Binding = new Binding("Result"), Width = 100 });
        var messageStyle = new Style(typeof(TextBlock));
        messageStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        messageStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Message")));
        results.Columns.Add(new DataGridTextColumn { ElementStyle = messageStyle, Header = "STATUS", Binding = new Binding("Message"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        results.ItemsSource = rows; Grid.SetRow(results, 2); root.Children.Add(results);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        start.Style = (Style)FindResource("Primary"); buttons.Children.Add(start); buttons.Children.Add(stop);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons); footer.Children.Add(feedback);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        start.Click += async (_, _) => await Run();
        code.TextChanged += (_, _) =>
        {
            if (groupExampleLoaded && !Regex.IsMatch(code.Text, @"const\s+groupId\s*=\s*[0-9]+\s*;")) groupExampleLoaded = false;
        };
        stop.Click += (_, _) => { cancellation?.Cancel(); feedback.Text = "Stopping the current browser and queue…"; };
        Closing += (_, _) => cancellation?.Cancel();
        Closed += (_, _) => { WasClosed = true; docs?.Close(); examplesWindow?.Close(); code.Clear(); };
    }

    private async Task Run()
    {
        if (IsRunning) return;
        try
        {
            if (canStart() is string error) throw new UserError(error);
            if (!int.TryParse(minutes.Text, out int timeout) || timeout is < 1 or > 120) throw new UserError("Enter a timeout from 1 to 120 minutes.");
            var request = new BrowserCodeRequest(url.Text.Trim(), code.Text, TimeSpan.FromMinutes(timeout), confirmGroup.IsChecked == true ? GroupTarget.Parse(group.Text) : null, Guest: rows[0].IsGuest).Validate();
            if (groupExampleLoaded)
            {
                var match = Regex.Match(code.Text, @"const\s+groupId\s*=\s*([0-9]+)\s*;");
                if (!match.Success || !GameTarget.TryId(match.Groups[1].Value, out long exampleGroup)) throw new UserError("Set a valid groupId in the example code before running.");
                if (request.ConfirmGroupId != exampleGroup) throw new UserError("The code groupId and membership confirmation must match.");
            }
            cancellation = new CancellationTokenSource();
            IsRunning = true; SetControls(false);
            foreach (var row in rows) { row.Result = "Queued"; row.Message = ""; } results.Items.Refresh();
            var outcomes = await queue.Run(rows.Select(row => row.Account).ToList(), async (account, token) =>
            {
                var row = rows.First(r => r.Account.Id == account.Id);
                row.Result = "Running"; row.Message = "Opening account browser…"; results.Items.Refresh();
                AccountBrowser? browser = null;
                try
                {
                    browser = row.IsGuest ? await browsers.OpenGuest(visible: false) : await browsers.Open(account, visible: false);
                    token.ThrowIfCancellationRequested();
                    var result = await browser.RunCode(request, message => { row.Message = message; feedback.Text = row.Username + ": " + message; results.Items.Refresh(); }, token);
                    row.Result = result.Outcome.ToString(); row.Message = result.Message; results.Items.Refresh();
                    return result;
                }
                catch (OperationCanceledException) { row.Result = "Stopped"; row.Message = "Stop requested."; return new(BrowserCodeOutcome.Stopped, row.Message); }
                catch (Exception ex) { row.Result = "Failed"; row.Message = Security.Friendly(ex); return new(BrowserCodeOutcome.Failed, row.Message); }
                finally { if (browser is not null && !browser.IsClosed) browser.Close(); results.Items.Refresh(); }
            }, cancellation.Token);
            feedback.Text = $"{outcomes.Count(r => r.Outcome == BrowserCodeOutcome.Completed)}/{rows.Count} completed. " +
                (outcomes.Count == rows.Count && outcomes.All(r => r.Outcome == BrowserCodeOutcome.Completed) ? "Queue finished." : "Queue stopped; remaining accounts were not run.");
        }
        catch (OperationCanceledException) { feedback.Text = "Queue stopped. Remaining accounts were not run."; }
        catch (Exception ex) { feedback.Text = Security.Friendly(ex); }
        finally
        {
            foreach (var row in rows.Where(r => r.Result == "Queued")) row.Result = "Not run";
            results.Items.Refresh(); IsRunning = false; SetControls(true); cancellation?.Dispose(); cancellation = null;
            QueueEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetControls(bool enabled)
    {
        start.IsEnabled = url.IsEnabled = minutes.IsEnabled = enabled;
        group.IsEnabled = confirmGroup.IsEnabled = enabled && !rows[0].IsGuest;
        examplesWindow?.RefreshAvailability();
        code.IsReadOnly = !enabled; stop.IsEnabled = !enabled;
    }
}
