using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LocalAccountManager;

public partial class MainWindow
{
    private readonly UpdateClient updates;
    private readonly Func<SignedUpdate, Task>? installUpdateOverride;
    private readonly bool updateChecksEnabled;
    private readonly CancellationTokenSource updateCancel = new();
    private readonly DispatcherTimer automaticUpdateTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private SignedUpdate? availableUpdate;
    private bool checkingUpdates;
    private bool automaticUpdatesPaused;
    private DateTimeOffset lastUpdateCheck = DateTimeOffset.MinValue;

    private void InitializeUpdateChecks()
    {
        automaticUpdateTimer.Tick += async (_, _) => await AutomaticUpdateTickAsync();
        if (!updateChecksEnabled) return;
        Loaded += async (_, _) =>
        {
            ConfigureAutomaticUpdates();
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, ".updates", "last-error.txt")))
            {
                automaticUpdatesPaused = true;
                UpdateStatus.Text = "Last update failed. Automatic updates paused; use Check for updates. Data was preserved.";
            }
            else await CheckUpdatesAsync();
        };
    }
    private void ConfigureAutomaticUpdates()
    {
        if (updateChecksEnabled && state.Settings.AutomaticUpdates && !closing) automaticUpdateTimer.Start();
        else automaticUpdateTimer.Stop();
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        bool saved = false;
        await Run(() =>
        {
            var dialog = new SettingsWindow(this, state.Settings, enabled =>
            {
                bool previous = state.Settings.AutomaticUpdates;
                state.Settings.AutomaticUpdates = enabled;
                try { vault.Save(state); }
                catch { state.Settings.AutomaticUpdates = previous; throw; }
            });
            saved = dialog.ShowDialog() == true;
            return Task.CompletedTask;
        });
        if (!saved || closing) return;
        ConfigureAutomaticUpdates();
        Status.Text = state.Settings.AutomaticUpdates ? "Settings saved. Automatic updates enabled." : "Settings saved. Automatic updates disabled.";
        if (state.Settings.AutomaticUpdates) { automaticUpdatesPaused = false; await CheckUpdatesAsync(); }
        else UpdateStatus.Text = availableUpdate is null ? "Automatic updates off. Use Check for updates anytime." : $"Version {availableUpdate.Manifest.Version} available. Use View update to install.";
    }
    private async Task AutomaticUpdateTickAsync()
    {
        if (closing || !state.Settings.AutomaticUpdates || automaticUpdatesPaused || checkingUpdates) return;
        if (DateTimeOffset.UtcNow - lastUpdateCheck >= TimeSpan.FromMinutes(30)) await CheckUpdatesAsync();
        else await TryAutomaticUpdateAsync();
    }
    private async Task CheckUpdatesAsync(bool allowAutomaticInstall = true)
    {
        if (checkingUpdates || closing) return;
        checkingUpdates = true; lastUpdateCheck = DateTimeOffset.UtcNow;
        UpdateButton.IsEnabled = false; UpdateStatus.Text = "Checking GitHub…"; UpdateStatus.ToolTip = null;
        try
        {
            availableUpdate = await updates.CheckAsync(UpdateTrust.CurrentVersion, updateCancel.Token);
            if (closing) return;
            UpdateStatus.Text = availableUpdate is null ? "You're up to date." : $"Version {availableUpdate.Manifest.Version} available. Data stays in place.";
            UpdateButton.Content = availableUpdate is null ? "Check for updates" : "View update";
        }
        catch (Exception ex)
        {
            if (!closing) { UpdateStatus.Text = "Update check unavailable. Try again."; UpdateStatus.ToolTip = ex is UserError ? ex.Message : "Check your internet connection, then retry."; }
            return;
        }
        finally { checkingUpdates = false; if (!closing) UpdateButton.IsEnabled = true; }
        if (allowAutomaticInstall) await TryAutomaticUpdateAsync();
    }
    private bool CanAutomaticallyRestart => !closing && !checkingSessions &&
        addition is not { IsCompleted: false } && enrollment.Current is null && !BrowserCodeActive && browsers.Count == 0 && OwnedWindows.Count == 0;
    private async Task TryAutomaticUpdateAsync()
    {
        if (!state.Settings.AutomaticUpdates || availableUpdate is null || automaticUpdatesPaused || checkingUpdates || closing) return;
        if (busy || !CanAutomaticallyRestart)
        { UpdateStatus.Text = $"Version {availableUpdate.Manifest.Version} ready. Automatic install is waiting for current work to finish."; return; }
        await InstallUpdateAsync(availableUpdate, automatic: true);
    }
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        if (availableUpdate is null) { await CheckUpdatesAsync(allowAutomaticInstall: false); return; }
        var update = availableUpdate;
        if (new UpdateReviewWindow(this, UpdateTrust.CurrentVersion, update).ShowDialog() != true) return;
        await InstallUpdateAsync(update, automatic: false);
    }
    private Task InstallUpdateAsync(SignedUpdate update, bool automatic) => Run(async () =>
    {
        if (addition is { IsCompleted: false } || enrollment.Current is not null) throw new UserError("Finish or close the current sign-in queue before updating.");
        UpdateButton.IsEnabled = false;
        string? unlockToken = null;
        try
        {
            vault.Save(state);
            UpdateStatus.Text = automatic ? "Automatically downloading verified update…" : "Downloading verified update…";
            if (installUpdateOverride is not null) { await installUpdateOverride(update); return; }
            var progress = new Progress<double>(value => { if (!closing) UpdateStatus.Text = $"Downloading update… {value:P0}"; });
            string folder = await updates.PrepareAsync(update, AppContext.BaseDirectory, progress, updateCancel.Token);
            // A dialog may have opened during the download. Leave it open and retry when the app is idle.
            if (automatic && !CanAutomaticallyRestart) { UpdateStatus.Text = "Automatic install is waiting for current work to finish."; return; }
            UpdateStatus.Text = "Preparing update and restart…";
            unlockToken = vault.CreateUpdateToken(AppContext.BaseDirectory, folder);
            using var helper = await UpdateInstaller.StartHelperAsync(AppContext.BaseDirectory, folder, vault.Root);
            busy = false;
            try { Close(); }
            finally { if (!closing && !helper.HasExited) helper.Kill(); }
            if (!closing) throw new UserError("The manager could not close for the update. Your data was preserved.");
        }
        catch
        {
            if (automatic) { automaticUpdatesPaused = true; UpdateStatus.Text = "Automatic update failed and is paused. Use View update to retry."; }
            throw;
        }
        finally
        {
            if (!closing)
            {
                if (unlockToken is not null) { try { Vault.DeleteUpdateToken(AppContext.BaseDirectory, unlockToken); } catch { } }
                UpdateButton.IsEnabled = true;
            }
        }
    }, showErrors: !automatic);
}
