using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalAccountManager;

public sealed class VaultSecurityWindow : Window
{
    public VaultSecurityWindow(Window owner, Vault vault, AppState state)
    {
        Style = (Style)FindResource(typeof(Window)); Owner = owner; Title = "Vault security";
        Width = 590; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "Vault security", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var protection = new TextBlock { Name = "ProtectionSummary", Margin = new Thickness(0, 16, 0, 12), TextWrapping = TextWrapping.Wrap }; panel.Children.Add(protection);
        var change = new Button { Content = "Change master password / protection", HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(change);
        panel.Children.Add(new TextBlock { Text = "Authenticator-app 2FA", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 26, 0, 8) });
        var status = new TextBlock { Name = "AuthenticatorStatus", TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Text = "Requires a master password. Adds an authenticator or recovery-code check when the app opens. Updates continue the session you already unlocked.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "This is a local app-unlock check. It does not add another layer of offline encryption: someone with your master password and the vault file can bypass the check using other software.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 14) });
        var manage = new Button { Name = "ManageAuthenticator", HorizontalAlignment = HorizontalAlignment.Left };
        manage.Click += (_, _) => { new AuthenticatorSettingsWindow(this, vault, state).ShowDialog(); Refresh(); }; panel.Children.Add(manage);
        var done = new Button { Content = "Done", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) }; panel.Children.Add(done);
        Content = panel; Refresh();
        change.Click += (_, _) => { new UnlockVaultWindow(vault, existingState: state) { Owner = this }.ShowDialog(); Refresh(); };
        void Refresh()
        {
            protection.Text = vault.UsesMasterPassword ? "Master password protection enabled · Portable vault" : "No master password (Not recommended) · Saved credentials are readable";
            status.Text = state.Authentication.Enabled ? $"Enabled · {state.Authentication.RecoveryHashes.Count} recovery codes remaining" : "Disabled";
            manage.Content = state.Authentication.Enabled ? "Manage authenticator…" : "Set up authenticator…";
            manage.IsEnabled = vault.UsesMasterPassword;
        }
    }
}

public sealed class AuthenticatorSettingsWindow : Window
{
    public AuthenticatorSettingsWindow(Window owner, Vault vault, AppState state)
    {
        Style = (Style)FindResource(typeof(Window)); Owner = owner; Title = "Authenticator-app 2FA";
        Width = 590; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        bool enabled = state.Authentication.Enabled;
        string secret = enabled ? "" : Authenticator.NewSecret();
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = enabled ? "Manage authenticator" : "Set up authenticator", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var inputs = new StackPanel(); panel.Children.Add(inputs);
        if (!enabled)
        {
            inputs.Children.Add(new TextBlock { Text = "In your authenticator app, add an account using a setup key. Choose time-based (TOTP), six digits, 30 seconds.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 8) });
            inputs.Children.Add(new TextBlock { Text = "Account name: Roblox Account Manager (local vault)", Margin = new Thickness(0, 0, 0, 8) });
            inputs.Children.Add(new TextBox { Name = "AuthenticatorSetupKey", Text = secret, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 18 });
            inputs.Children.Add(new TextBlock { Text = "Keep this key private. Never paste it into an online QR-code generator.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 8, 0, 10) });
        }
        inputs.Children.Add(new TextBlock { Text = "Current master password", Margin = new Thickness(0, 14, 0, 6) });
        var password = new PasswordBox { Name = "CurrentMasterPassword", MaxLength = 1024 }; inputs.Children.Add(password);
        inputs.Children.Add(new TextBlock { Text = enabled ? "Fresh authenticator code or unused recovery code" : "Current six-digit authenticator code", Margin = new Thickness(0, 14, 0, 6) });
        var code = new PasswordBox { Name = "AuthenticatorCode", MaxLength = 64 }; inputs.Children.Add(code);
        var feedback = new TextBlock { Name = "AuthenticatorFeedback", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) }; panel.Children.Add(buttons);
        var primary = new Button { Name = "SaveAuthenticator", Content = enabled ? "New recovery codes" : "Enable 2FA", Style = (Style)FindResource("Primary") }; buttons.Children.Add(primary);
        var disable = new Button { Name = "DisableAuthenticator", Content = "Disable 2FA", Visibility = enabled ? Visibility.Visible : Visibility.Collapsed }; buttons.Children.Add(disable);
        var cancel = new Button { Content = "Cancel", IsCancel = true }; buttons.Children.Add(cancel);
        void ShowRecovery(List<string> codes)
        {
            inputs.Visibility = buttons.Visibility = Visibility.Collapsed;
            feedback.Text = "2FA is enabled. Save these recovery codes somewhere separate from the vault. Each works once. The app will not show them again; generating new codes invalidates all previous codes.";
            panel.Children.Add(new TextBox { Name = "RecoveryCodes", Text = string.Join(Environment.NewLine, codes), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 15, Margin = new Thickness(0, 14, 0, 10) });
            var acknowledge = new CheckBox { Name = "RecoveryAcknowledgment", Content = "I saved my recovery codes somewhere safe." }; panel.Children.Add(acknowledge);
            var done = new Button { Content = "Done", Name = "RecoveryDone", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            acknowledge.Checked += (_, _) => done.IsEnabled = true; acknowledge.Unchecked += (_, _) => done.IsEnabled = false;
            done.Click += (_, _) => DialogResult = true; panel.Children.Add(done);
        }
        primary.Click += (_, _) =>
        {
            try
            {
                var codes = enabled ? vault.RegenerateRecoveryCodes(state, password.Password, code.Password) : vault.EnableAuthenticator(state, password.Password, secret, code.Password);
                password.Clear(); code.Clear(); secret = ""; ShowRecovery(codes);
            }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); code.Clear(); }
        };
        disable.Click += (_, _) =>
        {
            try { vault.DisableAuthenticator(state, password.Password, code.Password); DialogResult = true; }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); code.Clear(); }
        };
        Content = panel;
        Loaded += (_, _) => password.Focus();
        Closed += (_, _) =>
        {
            password.Clear(); code.Clear(); secret = "";
            foreach (var text in inputs.Children.OfType<TextBox>()) text.Clear();
            foreach (var text in panel.Children.OfType<TextBox>()) text.Clear();
        };
    }
}
