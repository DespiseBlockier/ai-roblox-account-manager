using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LocalAccountManager;

public sealed class FormDialog : Window
{
    private readonly List<TextBox> inputs = [];
    public string[] Values => inputs.Select(i => i.Text).ToArray();
    public FormDialog(Window owner, string title, string description, (string Name, string Value, int Max)[] fields)
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner; Title = title; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Name, Margin = new Thickness(0, 10, 0, 6) });
            var input = new TextBox { Text = field.Value, MaxLength = field.Max };
            if (field.Name == "Notes") { input.Height = 110; input.TextWrapping = TextWrapping.Wrap; input.AcceptsReturn = true; input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            inputs.Add(input); panel.Children.Add(input);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; buttons.Children.Add(cancel);
        var save = new Button { Content = "Save", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) => DialogResult = true; buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => inputs[0].Focus();
    }
}

public sealed class ServerWindow : Window
{
    private readonly DataGrid grid = new() { RowHeight = 40, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock status = new() { Text = "Refresh to fetch public servers from Roblox.", Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button more = new() { Content = "Next page", IsEnabled = false };
    private readonly CheckBox smallest = new() { Content = "Smallest servers first", IsChecked = true };
    private string? cursor;
    private bool busy;
    private bool loadedSmallest;
    public string SelectedJob { get; private set; } = "";
    public ServerWindow(Window owner, RobloxApi api, long place)
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner; Title = "Public servers · Place " + place; Width = 800; Height = 550; MinWidth = 650; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "Refresh" };
        top.Children.Add(refresh); top.Children.Add(more); top.Children.Add(smallest); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var select = new Button { Content = "Use selected server", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 14, 0, 0) };
        select.Click += (_, _) => { if (grid.SelectedItem is GameServer server) { SelectedJob = server.Id; DialogResult = true; } };
        DockPanel.SetDock(select, Dock.Bottom); root.Children.Add(select);
        grid.Columns.Add(new DataGridTextColumn { Header = "SERVER ID", Binding = new System.Windows.Data.Binding("Id"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        foreach (string property in new[] { "Playing", "MaxPlayers", "Ping", "Fps" }) grid.Columns.Add(new DataGridTextColumn { Header = property, Binding = new System.Windows.Data.Binding(property), Width = 85 });
        root.Children.Add(grid); Content = root;
        async Task Load(bool next)
        {
            if (busy) return;
            busy = true; refresh.IsEnabled = more.IsEnabled = smallest.IsEnabled = false;
            status.Text = "Loading public servers…";
            try
            {
                if (!next) loadedSmallest = smallest.IsChecked == true;
                var page = await api.Servers(place, next ? cursor : null, loadedSmallest);
                grid.ItemsSource = page.Servers; cursor = page.NextCursor;
                status.Text = $"{page.Servers.Count} public servers returned. Ping is reported by Roblox.";
            }
            catch (Exception ex) { cursor = null; status.Text = Security.Friendly(ex); }
            finally { busy = false; refresh.IsEnabled = smallest.IsEnabled = true; more.IsEnabled = !string.IsNullOrEmpty(cursor); }
        }
        refresh.Click += async (_, _) => await Load(false); more.Click += async (_, _) => await Load(true);
        smallest.Click += (_, _) => { cursor = null; more.IsEnabled = false; };
        Closing += (_, e) => { if (busy) e.Cancel = true; };
    }
}

public sealed class DataWindow : Window
{
    public DataWindow(Window owner, Vault vault, AppState state, Action<AppState> restore)
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner; Title = "Local data & backups"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Your portable vault", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = vault.Root, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "Encrypted layout backups contain account labels, groups, notes, and game links. Browser sessions and passwords are not included. Restored accounts need to sign in again.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Backup password · at least 12 characters", Margin = new Thickness(0, 18, 0, 6) });
        var password = new PasswordBox { MaxLength = 1024 }; panel.Children.Add(password);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        var export = new Button { Content = "Export layout", Style = (Style)FindResource("Primary") };
        var import = new Button { Content = "Import layout" }; buttons.Children.Add(export); buttons.Children.Add(import);
        export.Click += (_, _) =>
        {
            try
            {
                if (password.Password.Length < 12) throw new UserError("Use at least 12 characters for the backup password.");
                var dialog = new SaveFileDialog { Filter = "Encrypted layout|*.ramlayout", FileName = "roblox-layout-" + DateTime.Now.ToString("yyyy-MM-dd") + ".ramlayout" };
                if (dialog.ShowDialog(this) == true) { Vault.ExportLayout(dialog.FileName, state, password.Password); password.Clear(); feedback.Text = "Encrypted layout saved. Keep its password somewhere safe."; }
            }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); }
        };
        import.Click += (_, _) =>
        {
            try
            {
                var dialog = new OpenFileDialog { Filter = "Encrypted layout|*.ramlayout" };
                if (dialog.ShowDialog(this) == true) { var imported = Vault.ImportLayout(dialog.FileName, password.Password); restore(imported); password.Clear(); feedback.Text = "Layout merged. Existing accounts were kept; imported sessions require a fresh login."; }
            }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); }
        };
        panel.Children.Add(buttons); panel.Children.Add(feedback); Content = panel;
    }
}
