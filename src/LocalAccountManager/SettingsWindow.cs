using System.Windows;
using System.Windows.Controls;

namespace LocalAccountManager;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(Window owner, AppSettings settings, Action<bool> save)
    {
        Style = (Style)FindResource(typeof(Window)); Owner = owner; Title = "Settings";
        Width = 550; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "Settings", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var automatic = new CheckBox { Name = "AutomaticUpdates", Content = "Automatically install updates", IsChecked = settings.AutomaticUpdates, Margin = new Thickness(0, 22, 0, 10) }; panel.Children.Add(automatic);
        panel.Children.Add(new TextBlock { Text = "Off by default. When enabled, verified updates download, install, and restart the manager automatically. Checks run at startup and every 30 minutes. Installation waits for login queues, browsers, and dialogs to close. Your account data stays in place.", TextWrapping = TextWrapping.Wrap });
        var feedback = new TextBlock { Name = "SettingsFeedback", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var apply = new Button { Name = "SaveSettings", Content = "Save", IsDefault = true, Style = (Style)FindResource("Primary") };
        apply.Click += (_, _) =>
        {
            try { save(automatic.IsChecked == true); DialogResult = true; }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); }
        };
        buttons.Children.Add(apply); panel.Children.Add(buttons); Content = panel;
    }
}
