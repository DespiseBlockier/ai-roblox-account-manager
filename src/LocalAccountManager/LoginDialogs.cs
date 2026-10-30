using System.Windows;
using System.Windows.Controls;

namespace LocalAccountManager;

public sealed class LoginInputWindow : Window
{
    public List<LoginCredential> Credentials { get; private set; } = [];
    public LoginInputWindow(Window owner, bool bulk, IEnumerable<string>? groups = null)
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner; Title = bulk ? "Bulk sign-in queue" : "Add with username and password";
        Width = 570; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = bulk ? "username;password;group;\"Note\"" : "Enter your Roblox login", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "The queue fills Roblox's login page. Click Log In and complete any verification. It advances after a verified login; closing its browser stops the queue. Passwords and cookies are saved only after connecting, using your chosen vault protection. No master password (Not recommended) leaves them readable.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 14) });
        var username = new TextBox { MaxLength = 20 };
        var password = new PasswordBox { MaxLength = 1024 };
        var group = new ComboBox { IsEditable = true, ItemsSource = (groups ?? []).Distinct().OrderBy(g => g).ToList() };
        var note = new TextBox { MaxLength = 4000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsUndoEnabled = false };
        var format = new ComboBox { ItemsSource = new[] { "Username;password;group;note — last two optional", "Username;password — legacy semicolon passwords" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 10) };
        var lines = new TextBox { AcceptsReturn = true, Height = 210, MaxLength = 512_000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsUndoEnabled = false, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        if (bulk)
        {
            panel.Children.Add(format);
            panel.Children.Add(new TextBlock { Text = "Example: account_one;password_here;Alts;\"Group payouts\"\nGroup means a local manager group. Quote any field containing semicolons; use two quotes (\"\") for a quote inside a quoted field. A missing note keeps the old note; an explicit empty fourth field clears it. Passwords are visible here.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            panel.Children.Add(lines);
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "Username", Margin = new Thickness(0, 0, 0, 6) }); panel.Children.Add(username);
            panel.Children.Add(new TextBlock { Text = "Password", Margin = new Thickness(0, 12, 0, 6) }); panel.Children.Add(password);
            panel.Children.Add(new TextBlock { Text = "Local group · optional (blank keeps existing group)", Margin = new Thickness(0, 12, 0, 6) }); panel.Children.Add(group);
            panel.Children.Add(new TextBlock { Text = "Note · optional (blank keeps existing note)", Margin = new Thickness(0, 12, 0, 6) }); panel.Children.Add(note);
        }
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var start = new Button { Content = "Start sign-in queue", Style = (Style)FindResource("Primary") };
        start.Click += (_, _) =>
        {
            try
            {
                Credentials = bulk ? LoginInput.Parse(lines.Text, groupColumn: format.SelectedIndex == 0) : [LoginInput.Create(username.Text, password.Password, group.Text, note.Text.Length == 0 ? null : note.Text)];
                lines.Clear(); password.Clear(); username.Clear(); note.Clear(); DialogResult = true;
            }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); }
        };
        buttons.Children.Add(start); panel.Children.Add(buttons); Content = panel;
        Closed += (_, _) => { lines.Clear(); password.Clear(); username.Clear(); note.Clear(); if (DialogResult != true) foreach (var c in Credentials) c.Dispose(); };
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
        panel.Children.Add(new TextBlock { Text = "Used by Sign in to fill Roblox's login page. Leave empty to forget it. This changes the local saved password; it does not change your password on Roblox. Storage follows Vault security. No master password (Not recommended) stores it in readable form.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        password = new PasswordBox { Password = saved, MaxLength = 1024 }; panel.Children.Add(password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var save = new Button { Content = "Save password", Style = (Style)FindResource("Primary") };
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        Closed += (_, _) => { if (DialogResult != true) password.Clear(); };
    }
}
