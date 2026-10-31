using System.Windows;
using System.Windows.Controls;

namespace LocalAccountManager;

public sealed class LoginInputWindow : Window
{
    public List<LoginCredential> Credentials { get; private set; } = [];
    public LoginInputWindow(Window owner)
    {
        Style = (Style)FindResource(typeof(Window)); Owner = owner; Title = "Bulk sign in";
        Width = 610; SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Bulk sign in", FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Choose a format, then enter one account per line. Credential sign-ins fill and press Log In once; complete any verification yourself. Cookie sign-ins are checked with Roblox before saving. Storage follows Vault security.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 14) });
        var format = new ComboBox { Name = "BulkLoginFormat", ItemsSource = new[] { "username:password;group;note", "username:password", "Cookie login" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(format);
        var hint = new TextBlock { Name = "BulkLoginHint", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(hint);
        void Hint() => hint.Text = format.SelectedIndex switch
        {
            0 => "Example: account_one:password_here;Alts;\"Group payouts\"\nGroup and note are optional. Group means a local manager group. Quote fields containing semicolons; use two quotes (\"\") for a quote inside a quoted field.",
            1 => "Example: account_one:password_here\nEverything after the first colon is the password, including spaces, colons, and semicolons.",
            _ => "Paste one raw .ROBLOSECURITY cookie per line. A .ROBLOSECURITY= prefix is also accepted. Cookies grant account access: keep them private. Invalid cookies are skipped; existing saved passwords are kept."
        };
        format.SelectionChanged += (_, _) => Hint(); Hint();
        var lines = new TextBox { Name = "BulkLoginLines", AcceptsReturn = true, Height = 220, MaxLength = 512_000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsUndoEnabled = false, FontFamily = new System.Windows.Media.FontFamily("Consolas") }; panel.Children.Add(lines);
        var feedback = new TextBlock { Name = "BulkLoginFeedback", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var start = new Button { Name = "StartBulkLogin", Content = "Start sign-in queue", Style = (Style)FindResource("Primary") };
        start.Click += (_, _) =>
        {
            try { Credentials = LoginInput.ParseBulk(lines.Text, (BulkLoginFormat)format.SelectedIndex); lines.Clear(); DialogResult = true; }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); }
        };
        buttons.Children.Add(start); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Closed += (_, _) => { lines.Clear(); if (DialogResult != true) foreach (var c in Credentials) c.Dispose(); };
    }

}

public sealed class SavedPasswordWindow : Window
{
    private readonly PasswordBox password;
    public string TakePassword() { string value = password.Password; password.Clear(); return value; }
    public SavedPasswordWindow(Window owner, string username, string saved)
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner; Title = "Saved password · @" + username; Width = 480;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Saved password for @" + username, FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Used by Sign in to fill Roblox's login page and press Log In once. Leave empty to forget it. This changes the local saved password; it does not change your password on Roblox. Storage follows Vault security. No master password (Not recommended) stores it in readable form.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        password = new PasswordBox { Password = saved, MaxLength = 1024 }; panel.Children.Add(password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var save = new Button { Content = "Save password", Style = (Style)FindResource("Primary") };
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        Closed += (_, _) => { if (DialogResult != true) password.Clear(); };
    }
}
