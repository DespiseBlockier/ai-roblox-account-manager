using System.Windows;
using System.Windows.Controls;

namespace LocalAccountManager;

public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message, string title = "Account Manager", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) => Show(null, message, title, buttons, image);
    public static MessageBoxResult Show(Window? owner, string message, string title = "Account Manager", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        if (Application.Current?.TryFindResource(typeof(Window)) is not Style style)
            return owner is null ? MessageBox.Show(message, title, buttons, image) : MessageBox.Show(owner, message, title, buttons, image);
        var dialog = new Window { Style = style, Title = title, Width = 560, SizeToContent = SizeToContent.Height, MaxHeight = SystemParameters.WorkArea.Height - 60,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        if (owner is not null) dialog.Owner = owner;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } });
        MessageBoxResult result = buttons is MessageBoxButton.YesNo ? MessageBoxResult.No : buttons is MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
        MessageBoxResult[] choices = buttons switch
        {
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK]
        };
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice.ToString(), MinWidth = 75, IsCancel = choice == choices[^1], IsDefault = choice == result };
            button.Click += (_, _) => { result = choice; dialog.DialogResult = true; }; actions.Children.Add(button);
        }
        dialog.Content = panel; dialog.ShowDialog(); return result;
    }
}
