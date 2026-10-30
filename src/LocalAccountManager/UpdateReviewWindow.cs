using System.Windows;
using System.Windows.Controls;

namespace LocalAccountManager;

public sealed class UpdateReviewWindow : Window
{
    public UpdateReviewWindow(Window owner, string currentVersion, SignedUpdate update)
    {
        Owner = owner; Style = (Style)FindResource(typeof(Window));
        Title = "What's new · Version " + update.Manifest.Version;
        Width = 800; Height = 680; MinWidth = 580; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(new TextBlock { Text = "What's new", FontSize = 26, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = $"Version {currentVersion} → {update.Manifest.Version} · {update.Manifest.Size / 1024d / 1024d:0.0} MB", Margin = new Thickness(0, 8, 0, 0) });
        header.Children.Add(new TextBlock { Text = "Review the release log below. Installing preserves your Data folder and restarts the app.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(new Button { Name = "CancelUpdate", Content = "Cancel", IsCancel = true });
        var install = new Button { Name = "InstallUpdate", Content = "Install update & restart", Style = (Style)FindResource("Primary") };
        install.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(install); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        // Release text is displayed literally: no HTML, scripts, images, or remote content is executed.
        root.Children.Add(new TextBox
        {
            Name = "UpdateReleaseNotes", Text = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "No release log was provided for this version." : update.ReleaseNotes,
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14), FontSize = 13
        });
        Content = root;
    }
}
