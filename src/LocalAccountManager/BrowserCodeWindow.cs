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
    private readonly TextBox code = new()
    {
        Name = "ScriptEditor", Text = BrowserCodeRuntime.Starter, AcceptsReturn = true, AcceptsTab = true, MaxLength = 128000,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"), FontSize = 12, IsUndoEnabled = false
    };
    private readonly CheckBox confirmGroup = new() { Content = "Confirm actual membership in this group before advancing" };
    private readonly TabControl pages = new() { Name = "BrowserCodeTabs" };
    private readonly ListBox examples = new() { Name = "ExampleList", DisplayMemberPath = "Name" };
    private readonly Button useExample = new() { Name = "UseExample", Content = "Use example" };
    private readonly Button start = new() { Name = "StartCodeRun", Content = "Run on selected accounts" };
    private readonly Button stop = new() { Content = "Stop queue", IsEnabled = false };
    private readonly DataGrid results = new() { RowHeight = 42, SelectionMode = DataGridSelectionMode.Single };
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
        titleLine.Children.Add(new TextBlock { Text = accounts.Count == 0 ? "JavaScript · Guest browser" : "JavaScript · " + accounts.Count + " selected account(s)", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(titleLine);
        header.Children.Add(new TextBlock { Text = "ram.done() closes the current browser and advances. ram.stop() closes it and stops the queue. Closing a browser also stops the queue.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        header.Children.Add(new TextBlock { Text = "Starting Roblox URL", Margin = new Thickness(0, 0, 0, 5) }); header.Children.Add(url);
        var time = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        time.Children.Add(new TextBlock { Text = "Timeout per account (minutes)  ", VerticalAlignment = VerticalAlignment.Center }); time.Children.Add(minutes);
        time.Children.Add(new TextBlock { Text = "  Run only code you trust.", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }); header.Children.Add(time);
        root.Children.Add(header);
        var codePage = new TabItem { Name = "CodeTab", Header = "Code" };
        var docsPage = new TabItem { Name = "DocsTab", Header = "Docs" };
        var examplesPage = new TabItem { Name = "ExamplesTab", Header = "Examples" };
        var editor = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var verification = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        verification.Children.Add(confirmGroup);
        verification.Children.Add(new TextBlock { Text = "Group/community ID or link · optional membership confirmation", Margin = new Thickness(0, 0, 0, 5) });
        verification.Children.Add(group); DockPanel.SetDock(verification, Dock.Bottom); editor.Children.Add(verification); editor.Children.Add(code); codePage.Content = editor;
        docsPage.Content = new TextBox { Name = "JavaScriptDocs", Text = BrowserCodeHelpWindow.Reference, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        var library = new Grid { Margin = new Thickness(0, 8, 0, 0) }; library.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) }); library.ColumnDefinitions.Add(new ColumnDefinition());
        examples.ItemsSource = BrowserCodeExamples.All; examples.Margin = new Thickness(0, 0, 12, 0); library.Children.Add(examples);
        var detail = new DockPanel(); Grid.SetColumn(detail, 1); library.Children.Add(detail);
        var description = new TextBlock { Name = "ExampleDescription", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(description, Dock.Top); detail.Children.Add(description);
        useExample.Style = (Style)FindResource("Primary"); useExample.Margin = new Thickness(0, 10, 0, 0); useExample.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(useExample, Dock.Bottom); detail.Children.Add(useExample);
        var preview = new TextBox { Name = "ExamplePreview", IsReadOnly = true, AcceptsReturn = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12 }; detail.Children.Add(preview);
        examples.SelectionChanged += (_, _) =>
        {
            if (examples.SelectedItem is not BrowserCodeExample example) return;
            description.Text = example.Description + (rows[0].IsGuest && example.RequiresAccount ? " Select a saved account to use this example." : example.GuestOnly && !rows[0].IsGuest ? " Open the editor with no account selected to use a guest browser." : "");
            preview.Text = example.Code; useExample.IsEnabled = !IsRunning && (!example.RequiresAccount || !rows[0].IsGuest) && (!example.GuestOnly || rows[0].IsGuest);
        };
        useExample.Click += (_, _) =>
        {
            if (IsRunning || examples.SelectedItem is not BrowserCodeExample example || (example.RequiresAccount && rows[0].IsGuest || example.GuestOnly && !rows[0].IsGuest)) return;
            code.Text = example.Code; groupExampleLoaded = example.Group; confirmGroup.IsChecked = example.Group;
            if (example.Group) group.Clear();
            pages.SelectedItem = codePage; feedback.Text = "Example copied to Code. Review its settings before running; nothing has run yet.";
        };
        examples.SelectedIndex = rows[0].IsGuest ? 1 : 0;
        examplesPage.Content = library; pages.Items.Add(codePage); pages.Items.Add(docsPage); pages.Items.Add(examplesPage); pages.SelectedIndex = 0;
        Grid.SetRow(pages, 1); pages.Margin = new Thickness(0, 4, 0, 12); root.Children.Add(pages);
        results.Columns.Add(new DataGridTextColumn { Header = "ACCOUNT", Binding = new Binding("Username"), Width = 160 });
        results.Columns.Add(new DataGridTextColumn { Header = "RESULT", Binding = new Binding("Result"), Width = 100 });
        results.Columns.Add(new DataGridTextColumn { Header = "STATUS", Binding = new Binding("Message"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
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
        Closed += (_, _) => { WasClosed = true; code.Clear(); };
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
        useExample.IsEnabled = enabled && examples.SelectedItem is BrowserCodeExample example && (!example.RequiresAccount || !rows[0].IsGuest) && (!example.GuestOnly || rows[0].IsGuest);
        code.IsReadOnly = !enabled; stop.IsEnabled = !enabled;
    }
}
