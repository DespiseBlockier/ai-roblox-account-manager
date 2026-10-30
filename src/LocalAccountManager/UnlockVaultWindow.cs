using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LocalAccountManager;

public sealed class UnlockVaultWindow : Window
{
    public AppState? State { get; private set; }
    public UnlockVaultWindow(Vault vault, string? legacyFile = null)
    {
        Style = (Style)FindResource(typeof(Window));
        bool create = !vault.Exists, convert = vault.Exists && !vault.IsPortable;
        Title = create ? "Create portable vault" : convert ? "Convert to portable vault" : "Unlock portable vault";
        Width = 550; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Accounts, saved passwords, and cookies stay encrypted inside this manager's Data folder. Copy the whole folder to a USB and use the same master password on another PC.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 12) });
        panel.Children.Add(new TextBlock { Text = vault.Root, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        panel.Children.Add(new TextBlock { Text = "Master password · at least 12 characters", Margin = new Thickness(0, 20, 0, 6) });
        var password = new PasswordBox { Name = "MasterPassword", MaxLength = 1024 }; panel.Children.Add(password);
        var confirm = new PasswordBox { Name = "ConfirmPassword", MaxLength = 1024 };
        if (create || convert)
        {
            panel.Children.Add(new TextBlock { Text = "Confirm master password", Margin = new Thickness(0, 12, 0, 6) }); panel.Children.Add(confirm);
            panel.Children.Add(new TextBlock { Text = "Keep this password safe. It is required on other PCs, and the app cannot recover it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        }
        bool hasLegacy = create && legacyFile is not null && File.Exists(legacyFile);
        var import = new CheckBox { Content = "Import my accounts from the previous local installation on this PC", IsChecked = hasLegacy, Visibility = hasLegacy ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(import);
        if (convert) panel.Children.Add(new TextBlock { Text = "The old Windows-bound vault will be converted on this PC. Its encrypted previous copy is kept.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var unlock = new Button { Name = "UnlockVault", Content = create || convert ? "Create portable vault" : "Unlock", IsDefault = true, Style = (Style)FindResource("Primary") };
        unlock.Click += (_, _) =>
        {
            try
            {
                if ((create || convert) && password.Password != confirm.Password) throw new UserError("The master passwords do not match.");
                var state = vault.Unlock(password.Password, convertLegacy: convert);
                if (hasLegacy && import.IsChecked == true) state = Vault.ReadLegacyWindows(legacyFile!);
                vault.Save(state);
                State = state; password.Clear(); confirm.Clear(); DialogResult = true;
            }
            catch (Exception ex) { vault.Dispose(); feedback.Text = Security.Friendly(ex); }
        };
        buttons.Children.Add(unlock); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => password.Focus();
        Closed += (_, _) => { password.Clear(); confirm.Clear(); };
    }
}
