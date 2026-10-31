using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace LocalAccountManager;

public partial class App : Application
{
    private Mutex? instance;
    private Vault? vault;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.FirstOrDefault() == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await UpdateInstaller.RunHelperAsync(e.Args)); return;
        }
        try
        {
            using var options = LaunchOptions.Parse(e.Args);
            if (options.Help) { MessageBox.Show(LaunchOptions.Usage, "Launch arguments", MessageBoxButton.OK, MessageBoxImage.Information); Shutdown(); return; }
            UpdateInstaller.CleanupCompleted(AppContext.BaseDirectory);
            Vault.CleanupExpiredUpdateTokens(AppContext.BaseDirectory);
            string root = options.DataDirectory ?? Vault.DefaultRoot(AppContext.BaseDirectory);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
            instance = new Mutex(true, "Local\\LocalRobloxAccountManager-" + key, out bool created);
            if (!created) { MessageBox.Show("This account manager is already open for this data folder."); Shutdown(); return; }
            vault = new Vault(root);
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalRobloxAccountManager", "accounts.vault");
            AppState? state = options.TryUnlock(vault, AppContext.BaseDirectory, out string feedback);
            if (state is null && vault.IsPasswordless) state = vault.UnlockWithoutPassword();
            if (state is null)
            {
                var unlock = new UnlockVaultWindow(vault, legacy, initialFeedback: feedback);
                if (unlock.ShowDialog() != true || unlock.State is null) { Shutdown(); return; }
                state = unlock.State;
            }
            var window = new MainWindow(vault, state);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Security.Friendly(ex) + (vault is null ? "" : "\n\nData folder: " + vault.Root) + "\nThe app has stopped to preserve your data.", "Account Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        // Ownership remains on the WPF UI thread for the lifetime of the application.
        try { instance?.ReleaseMutex(); } catch (ApplicationException) { }
        instance?.Dispose();
        vault?.Dispose();
        base.OnExit(e);
    }
}
