using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalAccountManager;

public partial class MainWindow
{
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => AccountsGrid.UnselectAll();
    private void Background_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null;)
        {
            if (node is DataGridRow or ButtonBase or TextBoxBase or PasswordBox or ComboBox or ScrollBar or Thumb or TabItem or Hyperlink) return;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        AccountsGrid.UnselectAll();
    }
}
