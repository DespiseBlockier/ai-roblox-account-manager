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
        if (e.Args.FirstOrDefault() == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await UpdateInstaller.RunHelperAsync(e.Args)); return;
        }
        UpdateInstaller.CleanupCompleted(AppContext.BaseDirectory);
        string root = Vault.DefaultRoot(AppContext.BaseDirectory);
        // This explicit option permits isolated, credential-free UI and storage tests.
        if (e.Args.Length == 2 && e.Args[0] == "--data-dir") root = Path.GetFullPath(e.Args[1]);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
        instance = new Mutex(true, "Local\\LocalRobloxAccountManager-" + key, out bool created);
        if (!created) { MessageBox.Show("This account manager is already open for this data folder."); Shutdown(); return; }
        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            vault = new Vault(root);
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalRobloxAccountManager", "accounts.vault");
            AppState state;
            if (vault.IsPasswordless) state = vault.UnlockWithoutPassword();
            else
            {
                var unlock = new UnlockVaultWindow(vault, legacy);
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
            MessageBox.Show(Security.Friendly(ex) + "\n\nData folder: " + root + "\nThe app has stopped to preserve your data.", "Account Manager", MessageBoxButton.OK, MessageBoxImage.Error);
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
