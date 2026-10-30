using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LocalAccountManager;

public sealed class UnlockVaultWindow : Window
{
    public AppState? State { get; private set; }
    public UnlockVaultWindow(Vault vault, string? legacyFile = null, AppState? existingState = null)
    {
        Style = (Style)FindResource(typeof(Window));
        bool changing = existingState is not null, create = !vault.Exists, convert = vault.Exists && !vault.IsPortable;
        bool chooseProtection = changing || create || convert;
        Title = changing ? "Vault security" : create ? "Create portable vault" : convert ? "Convert to portable vault" : "Unlock portable vault";
        Width = 570; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Your vault stays in Data beside the EXE. A master password protects saved accounts, passwords, and cookies when you copy the folder between PCs.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 12) });
        panel.Children.Add(new TextBlock { Text = vault.Root, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        var current = new PasswordBox { Name = "CurrentMasterPassword", MaxLength = 1024 };
        if (changing && vault.UsesMasterPassword)
        {
            panel.Children.Add(new TextBlock { Text = "Current master password", Margin = new Thickness(0, 16, 0, 6) }); panel.Children.Add(current);
        }
        var noPassword = new CheckBox { Name = "NoMasterPassword", Content = "No master password (Not recommended)", IsChecked = changing && !vault.UsesMasterPassword, Visibility = chooseProtection ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 18, 0, 6) };
        panel.Children.Add(noPassword);
        var warning = new StackPanel();
        warning.Children.Add(new TextBlock { Text = "Not recommended: saved passwords and cookies will be stored in readable form. Anyone with this folder can access your accounts. Opening the app will not ask for a password.", Foreground = System.Windows.Media.Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        var acknowledge = new CheckBox { Name = "AcknowledgeNoPassword", Content = new TextBlock { Text = "I understand that anyone with this folder can read my saved passwords and cookies.", TextWrapping = TextWrapping.Wrap } };
        warning.Children.Add(acknowledge); panel.Children.Add(warning);
        var protectedFields = new StackPanel();
        protectedFields.Children.Add(new TextBlock { Text = changing ? "New master password · at least 12 characters" : "Master password · at least 12 characters", Margin = new Thickness(0, 14, 0, 6) });
        var password = new PasswordBox { Name = "MasterPassword", MaxLength = 1024 }; protectedFields.Children.Add(password);
        var confirm = new PasswordBox { Name = "ConfirmPassword", MaxLength = 1024 };
        if (chooseProtection)
        {
            protectedFields.Children.Add(new TextBlock { Text = "Confirm master password", Margin = new Thickness(0, 12, 0, 6) }); protectedFields.Children.Add(confirm);
            protectedFields.Children.Add(new TextBlock { Text = "Keep this password safe. The app cannot recover it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        }
        panel.Children.Add(protectedFields);
        void SelectionChanged()
        {
            bool plain = noPassword.IsChecked == true;
            warning.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
            protectedFields.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
            password.Clear(); confirm.Clear(); acknowledge.IsChecked = false;
        }
        noPassword.Checked += (_, _) => SelectionChanged(); noPassword.Unchecked += (_, _) => SelectionChanged(); SelectionChanged();
        bool hasLegacy = !changing && create && legacyFile is not null && File.Exists(legacyFile);
        var import = new CheckBox { Content = "Import my accounts from the previous local installation on this PC", IsChecked = hasLegacy, Visibility = hasLegacy ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(import);
        if (convert) panel.Children.Add(new TextBlock { Text = "The old Windows-bound vault will be converted on this PC.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var feedback = new TextBlock { Name = "VaultFeedback", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var unlock = new Button { Name = "UnlockVault", Content = changing ? "Save protection settings" : create || convert ? "Create portable vault" : "Unlock", IsDefault = true, Style = (Style)FindResource("Primary") };
        unlock.Click += (_, _) =>
        {
            try
            {
                bool plain = chooseProtection && noPassword.IsChecked == true;
                if (plain && acknowledge.IsChecked != true) throw new UserError("Read and acknowledge the Not recommended warning first.");
                if (!plain && chooseProtection && password.Password != confirm.Password) throw new UserError("The master passwords do not match.");
                if (changing) { vault.ChangeProtection(existingState!, plain ? null : password.Password, current.Password); State = existingState; }
                else
                {
                    var state = plain ? vault.UnlockWithoutPassword(convertLegacy: convert) : vault.Unlock(password.Password, convertLegacy: convert);
                    if (hasLegacy && import.IsChecked == true) state = Vault.ReadLegacyWindows(legacyFile!);
                    vault.Save(state); State = state;
                }
                current.Clear(); password.Clear(); confirm.Clear(); DialogResult = true;
            }
            catch (Exception ex) { if (!changing) vault.Dispose(); feedback.Text = Security.Friendly(ex); }
        };
        buttons.Children.Add(unlock); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { if (changing && vault.UsesMasterPassword) current.Focus(); else if (noPassword.IsChecked != true) password.Focus(); };
        Closed += (_, _) => { current.Clear(); password.Clear(); confirm.Clear(); };
    }
}
