using System.Collections.ObjectModel;
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
    private readonly TextBox code = new()
    {
        Name = "ScriptEditor", Text = BrowserCodeRuntime.Starter, AcceptsReturn = true, AcceptsTab = true, MaxLength = 128000,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"), FontSize = 12, IsUndoEnabled = false
    };
    private readonly CheckBox confirmGroup = new() { Content = "Confirm actual membership in this group before advancing" };
    private readonly Button generate = new() { Name = "GenerateExample", Content = "Generate join-group example" };
    private readonly Button start = new() { Name = "StartCodeRun", Content = "Run on selected accounts" };
    private readonly Button stop = new() { Content = "Stop queue", IsEnabled = false };
    private readonly DataGrid results = new() { RowHeight = 42, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock feedback = new() { Name = "RunFeedback", Text = "Ready. Code runs with the selected Roblox session's permissions. Run code you trust.", TextWrapping = TextWrapping.Wrap };
    private readonly ObservableCollection<BrowserCodeRow> rows;
    private readonly BrowserCodeQueue queue = new();
    private CancellationTokenSource? cancellation;
    private readonly BrowserHub browsers;
    private readonly Func<string?> canStart;
    private long? generatedGroupId;
    private BrowserCodeHelpWindow? docs;
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
            start.Content = "Run in guest browser"; generate.IsEnabled = confirmGroup.IsEnabled = group.IsEnabled = false;
            feedback.Text = "Fresh private guest session. ram.account is null. No saved account will be opened or added.";
        }
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = new GridLength(150) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        var titleLine = new DockPanel();
        var help = new Button { Name = "JavaScriptDocsButton", Content = "JS docs · ram helpers", Margin = new Thickness(12, 0, 0, 0) };
        help.Click += (_, _) =>
        {
            if (docs is not null) { docs.Activate(); return; }
            docs = new BrowserCodeHelpWindow(this); docs.Closed += (_, _) => docs = null; docs.Show();
        };
        DockPanel.SetDock(help, Dock.Right); titleLine.Children.Add(help);
        titleLine.Children.Add(new TextBlock { Text = accounts.Count == 0 ? "JavaScript · Guest browser" : "JavaScript · " + accounts.Count + " selected account(s)", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(titleLine);
        header.Children.Add(new TextBlock { Text = "ram.done() closes the current browser and advances. ram.stop() closes it and stops the queue. Closing a browser also stops the queue.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        header.Children.Add(new TextBlock { Text = "Starting Roblox URL", Margin = new Thickness(0, 0, 0, 5) }); header.Children.Add(url);
        header.Children.Add(new TextBlock { Text = "Group/community ID or link · example generator", Margin = new Thickness(0, 12, 0, 0) });
        var groupLine = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(generate, Dock.Right); groupLine.Children.Add(generate); groupLine.Children.Add(group); header.Children.Add(groupLine);
        group.ToolTip = "Group/community ID or an HTTPS Roblox group link";
        header.Children.Add(confirmGroup);
        var time = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        time.Children.Add(new TextBlock { Text = "Timeout per account (minutes)  ", VerticalAlignment = VerticalAlignment.Center }); time.Children.Add(minutes);
        time.Children.Add(new TextBlock { Text = "  Verification and approval remain on Roblox. This example does not make payouts.", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }); header.Children.Add(time);
        root.Children.Add(header);
        Grid.SetRow(code, 1); code.Margin = new Thickness(0, 4, 0, 12); root.Children.Add(code);
        results.Columns.Add(new DataGridTextColumn { Header = "ACCOUNT", Binding = new Binding("Username"), Width = 160 });
        results.Columns.Add(new DataGridTextColumn { Header = "RESULT", Binding = new Binding("Result"), Width = 100 });
        results.Columns.Add(new DataGridTextColumn { Header = "STATUS", Binding = new Binding("Message"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        results.ItemsSource = rows; Grid.SetRow(results, 2); root.Children.Add(results);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        start.Style = (Style)FindResource("Primary"); buttons.Children.Add(start); buttons.Children.Add(stop);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons); footer.Children.Add(feedback);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        generate.Click += (_, _) =>
        {
            try
            {
                long id = GroupTarget.Parse(group.Text); url.Text = GroupTarget.Url(id);
                generatedGroupId = id;
                code.Text = BrowserCodeRuntime.JoinGroupExample(id); confirmGroup.IsChecked = true;
                feedback.Text = "Example generated. It advances only after membership is confirmed; pending approval keeps this account open.";
            }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); }
        };
        start.Click += async (_, _) => await Run();
        stop.Click += (_, _) => { cancellation?.Cancel(); feedback.Text = "Stopping the current browser and queue…"; };
        Closing += (_, _) => cancellation?.Cancel();
        Closed += (_, _) => { WasClosed = true; docs?.Close(); code.Clear(); };
    }

    private async Task Run()
    {
        if (IsRunning) return;
        try
        {
            if (canStart() is string error) throw new UserError(error);
            if (!int.TryParse(minutes.Text, out int timeout) || timeout is < 1 or > 120) throw new UserError("Enter a timeout from 1 to 120 minutes.");
            var request = new BrowserCodeRequest(url.Text.Trim(), code.Text, TimeSpan.FromMinutes(timeout), confirmGroup.IsChecked == true ? GroupTarget.Parse(group.Text) : null, Guest: rows[0].IsGuest).Validate();
            if (generatedGroupId is long generated && request.ConfirmGroupId is long selected && generated != selected)
                throw new UserError("The group ID changed. Generate the group example again before running it.");
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
        generate.IsEnabled = group.IsEnabled = confirmGroup.IsEnabled = enabled && !rows[0].IsGuest;
        code.IsReadOnly = !enabled; stop.IsEnabled = !enabled;
    }
}
