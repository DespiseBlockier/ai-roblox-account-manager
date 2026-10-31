using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace LocalAccountManager;

public sealed class GmailWindow : Window
{
    private readonly CancellationTokenSource canceled = new();
    private bool busy, closed;
    public const string SetupGuide = """
        No hosted server is needed. Everything runs on this PC; Google hosts Gmail and sign-in.

        1. In Google Cloud Console, create/select a project and enable Gmail API.
        2. Configure Google Auth Platform → Branding/Audience. For a personal app, use External/Testing and add your Gmail address as a test user.
        3. In Data Access, add https://www.googleapis.com/auth/gmail.readonly.
        4. Create an OAuth client with application type Desktop app. Download its JSON.
        5. Import that JSON here, then choose Connect Gmail and approve read-only access in your normal browser.

        The temporary callback listens only on 127.0.0.1 and closes after sign-in. There is no public callback server. Google may expire grants for apps in Testing after seven days; reconnect when needed. Public distribution under a shared OAuth client can require Google's verification process. This build lets each owner import their own Desktop client.

        Only fresh, authenticated Roblox verification messages are processed. Unmatched messages and other verification methods stay manual. No sending, deleting, marking mail read, or general inbox access is exposed to JavaScript.
        """;

    public GmailWindow(Window owner, Vault vault, AppState state, GmailService gmail, IReadOnlyList<Account> selected)
    {
        Owner = owner; Style = (Style)FindResource(typeof(Window)); Title = "Gmail connections";
        Width = 850; Height = 790; MinWidth = 740; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(24) };
        var feedback = new TextBlock { Name = "GmailFeedback", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(feedback, Dock.Bottom); root.Children.Add(feedback);
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "Gmail · Google sign-in", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Google grants read-only mailbox access. This app queries Roblox verification mail only. Connections and refresh tokens stay in your portable vault; your Google password is never entered here.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        if (!vault.UsesMasterPassword) panel.Children.Add(new TextBlock { Text = "No master password · Not recommended. Google refresh tokens will be readable in this unprotected Data folder. Enable vault protection before connecting if you want them encrypted.", Foreground = System.Windows.Media.Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBox { Name = "GmailSetupGuide", Text = SetupGuide, IsReadOnly = true, Height = 190, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) });
        var setupButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        var import = new Button { Name = "ImportGoogleClient", Content = "Import Desktop client JSON…" };
        var console = new Button { Content = "Google Cloud setup" }; console.Click += (_, _) => Open("https://console.cloud.google.com/apis/library/gmail.googleapis.com");
        var grants = new Button { Content = "Google app permissions" }; grants.Click += (_, _) => Open("https://myaccount.google.com/connections");
        setupButtons.Children.Add(import); setupButtons.Children.Add(console); setupButtons.Children.Add(grants); panel.Children.Add(setupButtons);
        var clientStatus = new TextBlock { Name = "GoogleClientStatus", Text = state.Gmail.Client.ClientId.Length == 0 ? "No Desktop client imported." : "Desktop client configured.", Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(clientStatus);
        var connections = new DataGrid { Name = "GmailConnections", Height = 105, RowHeight = 36, SelectionMode = DataGridSelectionMode.Single };
        connections.Columns.Add(new DataGridTextColumn { Header = "CONNECTED MAILBOX", Binding = new Binding("Address"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        connections.Columns.Add(new DataGridTextColumn { Header = "AUTO LOGIN CODES", Binding = new Binding("AutomaticLoginCodes"), Width = 160 }); panel.Children.Add(connections);
        var mailButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var connect = new Button { Name = "ConnectGmail", Content = "Connect Gmail", Style = (Style)FindResource("Primary") };
        var test = new Button { Name = "TestGmail", Content = "Test selected" };
        var remove = new Button { Name = "DisconnectGmail", Content = "Disconnect locally" };
        mailButtons.Children.Add(connect); mailButtons.Children.Add(test); mailButtons.Children.Add(remove); panel.Children.Add(mailButtons);
        var auto = new CheckBox { Name = "AutomaticEmailCodes", Content = "Automatically retrieve and fill Roblox email 2FA during account sign-in" }; panel.Children.Add(auto);
        auto.Click += (_, _) =>
        {
            if (busy || connections.SelectedItem is not GmailConnection connection) return;
            bool previous = connection.AutomaticLoginCodes; connection.AutomaticLoginCodes = auto.IsChecked == true;
            try { vault.Save(state); connections.Items.Refresh(); } catch (Exception ex) { connection.AutomaticLoginCodes = previous; auto.IsChecked = previous; feedback.Text = Friendly(ex); }
        };
        connections.SelectionChanged += (_, _) => { auto.IsEnabled = !busy && connections.SelectedItem is GmailConnection; auto.IsChecked = (connections.SelectedItem as GmailConnection)?.AutomaticLoginCodes == true; };
        panel.Children.Add(new TextBlock { Text = "Local account association · " + selected.Count + " selected", FontSize = 17, Margin = new Thickness(0, 18, 0, 7) });
        panel.Children.Add(new TextBlock { Text = "This associates accounts in the manager. It does not change their email on Roblox. Use the JS email verification example to add and verify an email on Roblox.", TextWrapping = TextWrapping.Wrap });
        var address = new TextBox { Name = "AccountEmailAddress", MaxLength = 254, Text = selected.Count == 1 ? selected[0].EmailAddress : "", Margin = new Thickness(0, 8, 0, 5), ToolTip = "Connected Gmail address or a plus alias; blank uses the selected mailbox." }; panel.Children.Add(address);
        var association = new StackPanel { Orientation = Orientation.Horizontal };
        var assign = new Button { Name = "AssignGmail", Content = "Associate selected accounts", IsEnabled = selected.Count > 0 };
        var clear = new Button { Content = "Clear local association", IsEnabled = selected.Count > 0 }; association.Children.Add(assign); association.Children.Add(clear); panel.Children.Add(association);
        var associations = new TextBlock { Name = "GmailAccountAssociations", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(associations);
        void Refresh()
        {
            connections.ItemsSource = state.Gmail.Connections.ToList(); connections.SelectedIndex = state.Gmail.Connections.Count > 0 ? 0 : -1;
            associations.Text = string.Join('\n', selected.Select(a => "@" + a.Username + " · " + a.EmailLabel));
        }
        GmailConnection Selected() => connections.SelectedItem as GmailConnection ?? throw new UserError("Select a connected mailbox first.");
        async Task Run(Func<Task> action)
        {
            if (busy) return; busy = true; import.IsEnabled = connect.IsEnabled = test.IsEnabled = remove.IsEnabled = auto.IsEnabled = assign.IsEnabled = clear.IsEnabled = false;
            try { await action(); if (!closed) Refresh(); }
            catch (OperationCanceledException) { if (!closed) feedback.Text = "Gmail operation canceled."; }
            catch (Exception ex) { if (!closed) feedback.Text = Friendly(ex); }
            finally { busy = false; if (!closed) { import.IsEnabled = connect.IsEnabled = test.IsEnabled = remove.IsEnabled = true; auto.IsEnabled = connections.SelectedItem is GmailConnection; assign.IsEnabled = clear.IsEnabled = selected.Count > 0; } }
        }
        import.Click += async (_, _) => await Run(() =>
        {
            if (state.Gmail.Connections.Count > 0) throw new UserError("Disconnect existing Gmail connections before changing the OAuth client.");
            var file = new OpenFileDialog { Filter = "Google Desktop client JSON|*.json" };
            if (file.ShowDialog(this) == true)
            {
                if (new FileInfo(file.FileName).Length > 16000) throw new UserError("The Google client file is too large.");
                var next = GoogleDesktopClient.Parse(File.ReadAllText(file.FileName)); var previous = state.Gmail.Client; state.Gmail.Client = next;
                try { vault.Save(state); } catch { state.Gmail.Client = previous; throw; }
                clientStatus.Text = "Desktop client configured."; feedback.Text = "Client imported. Connect Gmail to grant read-only access.";
            }
            return Task.CompletedTask;
        });
        connect.Click += async (_, _) => await Run(async () => { feedback.Text = "Complete Google sign-in in your normal browser. No hosted server is needed."; var connected = await gmail.Connect(Open, canceled.Token); if (!closed) feedback.Text = "Connected " + connected.Address + "."; });
        test.Click += async (_, _) => await Run(async () => { await gmail.Test(Selected(), canceled.Token); feedback.Text = "Gmail connection is valid."; });
        remove.Click += async (_, _) => await Run(() => { gmail.Remove(Selected()); feedback.Text = "Disconnected locally. You can also revoke access under Google app permissions."; return Task.CompletedTask; });
        assign.Click += async (_, _) => await Run(() => { gmail.Associate(selected, Selected(), address.Text.Trim(), true); feedback.Text = "Local Gmail association saved. Roblox email settings were not changed."; return Task.CompletedTask; });
        clear.Click += async (_, _) => await Run(() => { gmail.Associate(selected, null, "", false); feedback.Text = "Local Gmail associations cleared."; return Task.CompletedTask; });
        Closed += (_, _) => { closed = true; canceled.Cancel(); canceled.Dispose(); }; Content = root; Refresh();
    }
    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public static string Friendly(Exception error) => error is UserError ? error.Message : error is OperationCanceledException or TimeoutException ? "Google took too long to respond. Retry or reconnect Gmail." : error is System.Net.Http.HttpRequestException ? "Could not reach Google. Check your connection and retry." : Security.Friendly(error);
}
