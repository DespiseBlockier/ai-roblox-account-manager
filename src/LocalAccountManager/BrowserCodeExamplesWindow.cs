using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalAccountManager;

public sealed class BrowserCodeExamplesWindow : Window
{
    private readonly ListBox list = new() { Name = "ExampleList", DisplayMemberPath = "Name" };
    private readonly Button use = new() { Name = "UseExample", Content = "Use example" };
    private readonly bool guest;
    private readonly Func<bool> running;
    public BrowserCodeExamplesWindow(Window owner, bool guest, Func<bool> running, Action<BrowserCodeExample> apply)
    {
        Owner = owner; this.guest = guest; this.running = running; Style = (Style)FindResource(typeof(Window));
        Title = "JavaScript examples"; Width = 960; Height = 720; MinWidth = 760; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(22) }; root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        list.ItemsSource = BrowserCodeExamples.All; list.Margin = new Thickness(0, 0, 12, 0); root.Children.Add(list);
        var detail = new DockPanel(); Grid.SetColumn(detail, 1); root.Children.Add(detail);
        var description = new TextBlock { Name = "ExampleDescription", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(description, Dock.Top); detail.Children.Add(description);
        use.Style = (Style)FindResource("Primary"); use.Margin = new Thickness(0, 10, 0, 0); use.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(use, Dock.Bottom); detail.Children.Add(use);
        var preview = new TextBox { Name = "ExamplePreview", IsReadOnly = true, AcceptsReturn = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12 }; detail.Children.Add(preview);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not BrowserCodeExample example) return;
            description.Text = example.Description + (guest && example.RequiresAccount ? " Select a saved account to use this example." : example.GuestOnly && !guest ? " Open the editor without an account selected for guest mode." : "");
            preview.Text = example.Code; RefreshAvailability();
        };
        use.Click += (_, _) => { RefreshAvailability(); if (use.IsEnabled && list.SelectedItem is BrowserCodeExample example) { apply(example); Close(); } };
        list.SelectedIndex = guest ? 1 : 0; Content = root;
    }
    public void RefreshAvailability() => use.IsEnabled = !running() && list.SelectedItem is BrowserCodeExample example && (!example.RequiresAccount || !guest) && (!example.GuestOnly || guest);
}
