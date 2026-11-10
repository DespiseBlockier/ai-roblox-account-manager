using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Xaml.Styling;

namespace LocalAccountManager.Linux;

internal static class Program
{
    internal const string Version = "1.17.2-linux.1";
    internal static string DataRoot { get; private set; } = "";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--help"] || args is ["--version"])
        {
            Console.WriteLine($"Roblox Account Manager {Version}\nUsage: RobloxAccountManager [--data-dir DIRECTORY]");
            return 0;
        }
        if (args.Length != 0 && args is not ["--data-dir", _])
        {
            Console.Error.WriteLine("Usage: RobloxAccountManager [--data-dir DIRECTORY]");
            return 2;
        }
        try
        {
            DataRoot = args.Length == 2 ? Path.GetFullPath(args[1]) :
                Vault.DefaultRoot(Environment.GetEnvironmentVariable("RAM_PORTABLE_DIR") ?? AppContext.BaseDirectory);
            return AppBuilder.Configure<ManagerApp>().UsePlatformDetect()
                .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is ArgumentException ? "The data directory path is invalid." :
                "The manager could not start. Run it from an X11 or XWayland desktop with the dependencies in the Linux instructions.");
            Console.Error.WriteLine($"Startup error: {ex.GetType().Name}");
            return 1;
        }
    }
}

internal sealed class ManagerApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://RobloxAccountManager/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vault = new Vault(Program.DataRoot);
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None
                };
                if (OperatingSystem.IsLinux())
                {
                    Directory.CreateDirectory(vault.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                else Directory.CreateDirectory(vault.Root);
                var instanceLock = new FileStream(Path.Combine(vault.Root, ".manager.lock"), options);
                desktop.Exit += (_, _) => { vault.Dispose(); instanceLock.Dispose(); };
                var unlock = new UnlockWindow(vault, state =>
                {
                    var manager = new MainWindow(vault, state);
                    desktop.MainWindow = manager;
                    manager.Show();
                });
                desktop.MainWindow = unlock;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var error = new Window { Title = "Could not open data folder", Width = 520, Height = 210 };
                error.Content = Ui.Stack(
                    Ui.Label("Close any other Linux manager using this folder and check that it is writable."),
                    Ui.Label(vault.Root), Ui.Button("Close", error.Close));
                desktop.MainWindow = error;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class UnlockWindow : Window
{
    public UnlockWindow(Vault vault, Action<AppState> open)
    {
        bool creating = !vault.Exists;
        Title = creating ? "Create your portable vault" : "Unlock your portable vault";
        Width = 545;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var password = Ui.Secret("Master password");
        var confirmation = Ui.Secret("Repeat password");
        var code = Ui.Secret("Authenticator code or recovery code");
        var unprotected = new CheckBox { Content = "No master password (not recommended)", IsVisible = creating };
        var status = Ui.Label("");
        var fields = Ui.Stack(Ui.Label(vault.Root), password, confirmation, unprotected, code);
        var warning = Ui.Label("Use a long password to protect the passwords and sessions in this folder.");
        fields.Children.Add(warning);
        var unlock = new Button { Content = creating ? "Create vault" : "Unlock", IsDefault = true };
        fields.Children.Add(unlock);
        fields.Children.Add(status);
        Content = fields;

        bool passwordless = false;
        try { passwordless = !creating && vault.IsPasswordless; }
        catch (Exception ex) { status.Text = Ui.Error(ex); }
        confirmation.IsVisible = creating;
        code.IsVisible = false;
        password.IsVisible = !passwordless;
        Opened += (_, _) =>
        {
            if (passwordless) unlock.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            else password.Focus();
        };
        unprotected.IsCheckedChanged += (_, _) =>
        {
            password.IsEnabled = confirmation.IsEnabled = unprotected.IsChecked != true;
            warning.Text = unprotected.IsChecked == true
                ? "Anyone with this folder can read the saved passwords and sessions."
                : "Use a long password to protect the passwords and sessions in this folder.";
        };
        bool opening = false;
        Closing += (_, e) => e.Cancel = opening;
        unlock.Click += async (_, _) =>
        {
            opening = true;
            unlock.IsEnabled = false;
            var entry = password.Text ?? "";
            var secondFactor = code.Text ?? "";
            code.Text = "";
            try
            {
                bool noPassword = passwordless || unprotected.IsChecked == true;
                if (creating && !noPassword && entry != confirmation.Text) throw new UserError("The passwords do not match.");
                var state = await Task.Run(() =>
                {
                    var unlocked = vault.HasPendingSecondFactor ? vault.CompleteSecondFactor(secondFactor) :
                        noPassword ? vault.UnlockWithoutPassword() : vault.Unlock(entry);
                    if (creating || AccountEnrollment.RemoveLegacyPlaceholders(unlocked) > 0) vault.Save(unlocked);
                    return unlocked;
                });
                open(state);
                opening = false;
                Close();
            }
            catch (SecondFactorRequiredError ex)
            {
                password.IsVisible = false;
                code.IsVisible = true;
                status.Text = ex.Message;
                code.Focus();
            }
            catch (Exception ex)
            {
                password.IsVisible = !passwordless && !vault.HasPendingSecondFactor;
                code.IsVisible = vault.HasPendingSecondFactor;
                status.Text = Ui.Error(ex);
            }
            finally
            {
                opening = false;
                password.Text = confirmation.Text = "";
                unlock.IsEnabled = true;
            }
        };
    }
}

internal static class Ui
{
    internal static TextBlock Label(string text) => new() { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    internal static TextBox Secret(string placeholder) => new() { PlaceholderText = placeholder, PasswordChar = '●', MaxLength = 1024 };
    internal static Button Button(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }
    internal static StackPanel Stack(params Control[] controls)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 9 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }
    internal static WrapPanel Row(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls)
        {
            control.Margin = new Thickness(0, 0, 8, 5);
            panel.Children.Add(control);
        }
        return panel;
    }
    internal static string Error(Exception ex) => ex is UnauthorizedAccessException
        ? "Access to the data folder was denied." : Security.Friendly(ex);
    internal static Window Dialog(string title, Control content, double width = 530, double height = 410) => new()
    {
        Title = title, Width = width, Height = height, MinWidth = 360, MinHeight = 200,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content
    };
    internal static async Task Message(Window owner, string title, string message)
    {
        var output = new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var content = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        var dialog = Dialog(title, content);
        var close = Button("Close", dialog.Close);
        DockPanel.SetDock(close, Dock.Bottom);
        content.Children.Add(close);
        content.Children.Add(new ScrollViewer { Content = output, Margin = new Thickness(0, 0, 0, 12) });
        await dialog.ShowDialog(owner);
    }
}
