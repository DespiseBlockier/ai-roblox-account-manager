using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LocalAccountManager;

internal sealed record GameAccessRow(string Account, string Result);

internal sealed class GameAccessResultsWindow : Window
{
    public string Summary { get; private set; } = "Checking game access…";

    public GameAccessResultsWindow(Window owner, long placeId, IReadOnlyList<Account> accounts, Func<Account, Task<string>> check)
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner;
        Title = "Game access results";
        Width = 1000;
        Height = 650;
        MinWidth = 620;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var rows = new ObservableCollection<GameAccessRow>(accounts.Select(account => new GameAccessRow("@" + account.Username, "Waiting")));
        var list = new DataGrid { Name = "GameAccessResults", ItemsSource = rows, RowHeight = double.NaN, MinRowHeight = 42, SelectionMode = DataGridSelectionMode.Single };
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        list.Columns.Add(new DataGridTextColumn { Header = "ACCOUNT", Binding = new Binding(nameof(GameAccessRow.Account)), Width = 180 });
        var wrap = new Style(typeof(TextBlock));
        wrap.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        list.Columns.Add(new DataGridTextColumn
        {
            Header = "RESULT", Binding = new Binding(nameof(GameAccessRow.Result)), ElementStyle = wrap,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var root = new DockPanel { Margin = new Thickness(22) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "Game access results", FontSize = 23, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = $"Place {placeId} · {accounts.Count} accounts", Margin = new Thickness(0, 5, 0, 0) });
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var action = new Button { Name = "GameAccessAction", Content = "Stop after current", Margin = new Thickness(10, 0, 0, 0) };
        var progress = new TextBlock { Text = Summary, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(action, Dock.Right);
        footer.Children.Add(action);
        footer.Children.Add(progress);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(list);
        Content = root;

        bool running = true;
        bool stopRequested = false;
        void Stop()
        {
            stopRequested = true;
            action.IsEnabled = false;
            progress.Text = "Stopping after the current check…";
        }
        action.Click += (_, _) => { if (running) Stop(); else Close(); };
        Closing += (_, e) => { if (running) { e.Cancel = true; Stop(); } };
        ContentRendered += Start;

        async void Start(object? sender, EventArgs e)
        {
            ContentRendered -= Start;
            int checkedCount = 0;
            bool rateLimited = false;
            while (checkedCount < accounts.Count && !stopRequested)
            {
                int index = checkedCount++;
                progress.Text = $"Checking {checkedCount} of {accounts.Count} accounts…";
                rows[index] = rows[index] with { Result = "Checking…" };
                string result;
                try { result = await check(accounts[index]); }
                catch (RobloxApiError error) when (error.StatusCode == 429)
                {
                    result = Security.Friendly(error);
                    rateLimited = true;
                    stopRequested = true;
                }
                catch (Exception error) { result = Security.Friendly(error); }
                rows[index] = rows[index] with { Result = result };
                if (checkedCount < accounts.Count && !stopRequested) await Task.Delay(650);
            }
            for (int i = checkedCount; i < rows.Count; i++)
                rows[i] = rows[i] with { Result = rateLimited ? "Not checked · Roblox rate limited the batch" : "Not checked · stopped" };
            Summary = $"Checked {checkedCount} of {accounts.Count} accounts.";
            if (rateLimited) Summary += " Roblox rate limited the batch.";
            progress.Text = Summary;
            running = false;
            action.Content = "Close";
            action.IsEnabled = action.IsDefault = action.IsCancel = true;
        }
    }
}
