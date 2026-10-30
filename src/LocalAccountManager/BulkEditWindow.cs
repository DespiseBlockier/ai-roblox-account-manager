using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LocalAccountManager;

public sealed class BulkEditWindow : Window
{
    public BulkEditWindow(Window owner, IReadOnlyList<Account> accounts, IEnumerable<string> groups, Action<BulkChanges> apply)
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner; Title = "Bulk edit · " + accounts.Count + " accounts"; Width = 880; Height = 720;
        MinWidth = 760; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var fields = new StackPanel();
        fields.Children.Add(new TextBlock { Text = "Edit " + accounts.Count + " selected accounts", FontSize = 23, FontWeight = FontWeights.SemiBold });
        fields.Children.Add(new TextBlock { Text = "Check the fields to apply. Unchecked fields keep each account's existing values. Groups here are local manager groups.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 14) });
        var group = new ComboBox { Name = "BulkGroup", IsEditable = true, ItemsSource = groups.Distinct().OrderBy(g => g).ToList() };
        var alias = new TextBox { Name = "BulkAlias", MaxLength = 80 };
        var notes = new TextBox { Name = "BulkNotes", MaxLength = 4000, AcceptsReturn = true, Height = 75, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
        var starred = new CheckBox { Name = "BulkFavorite", Content = "Favorite" };
        var append = new CheckBox { Content = "Append notes instead of replacing" };
        CheckBox Row(string label, FrameworkElement input, string name)
        {
            var line = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(135) }); line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var enabled = new CheckBox { Content = label, Name = name, VerticalAlignment = VerticalAlignment.Top };
            input.IsEnabled = false;
            enabled.Checked += (_, _) => input.IsEnabled = true; enabled.Unchecked += (_, _) => input.IsEnabled = false;
            line.Children.Add(enabled); Grid.SetColumn(input, 1); line.Children.Add(input); fields.Children.Add(line); return enabled;
        }
        var useGroup = Row("Apply group", group, "ApplyGroup");
        var useAlias = Row("Apply alias", alias, "ApplyAlias");
        fields.Children.Add(new TextBlock { Text = "Alias templates: {username}, {displayName}, {index}. Blank alias clears it; blank group uses Default.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(135, 0, 0, 10) });
        var useNotes = Row("Apply notes", notes, "ApplyNotes"); fields.Children.Add(append);
        var useFavorite = Row("Apply favorite", starred, "ApplyFavorite");
        root.Children.Add(fields);
        var preview = new DataGrid { RowHeight = 38, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(0, 6, 0, 10) };
        foreach (var col in new[] { ("ACCOUNT", "Username", 160.0), ("GROUP", "Group", 150.0), ("ALIAS", "Alias", 180.0), ("FAVORITE", "Favorite", 90.0) })
            preview.Columns.Add(new DataGridTextColumn { Header = col.Item1, Binding = new Binding(col.Item2), Width = col.Item3 });
        preview.Columns.Add(new DataGridTextColumn { Header = "NOTES", Binding = new Binding("Notes"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(preview, 1); root.Children.Add(preview);
        var footer = new DockPanel(); var feedback = new TextBlock { Name = "BulkFeedback", Text = "Choose fields, then preview or apply.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var view = new Button { Name = "PreviewBulk", Content = "Preview" };
        var save = new Button { Name = "ApplyBulk", Content = "Apply to " + accounts.Count + " accounts", Style = (Style)FindResource("Primary") };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true }); buttons.Children.Add(view); buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons); footer.Children.Add(feedback); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        BulkChanges Changes() => new(useGroup.IsChecked == true ? group.Text : null, useAlias.IsChecked == true ? alias.Text : null,
            useNotes.IsChecked == true ? notes.Text : null, append.IsChecked == true, useFavorite.IsChecked == true ? starred.IsChecked == true : null);
        view.Click += (_, _) =>
        {
            try { preview.ItemsSource = BulkEditing.Preview(accounts, Changes()); feedback.Text = "Preview only. Saved values have not changed."; }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); }
        };
        save.Click += (_, _) =>
        {
            try { var changes = Changes(); preview.ItemsSource = BulkEditing.Preview(accounts, changes); apply(changes); DialogResult = true; }
            catch (Exception ex) { feedback.Text = Security.Friendly(ex); }
        };
    }
}
