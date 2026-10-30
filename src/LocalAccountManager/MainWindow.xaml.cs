using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LocalAccountManager;

public partial class MainWindow : Window
{
    private readonly Vault vault;
    private readonly AppState state;
    private readonly RobloxApi api;
    private readonly BrowserHub browsers;
    private readonly AccountEnrollment enrollment;
    private readonly LoginQueue loginQueue = new();
    private readonly DispatcherTimer sessionTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private AccountBrowser? activeLoginBrowser;
    private Task? addition;
    private bool closing;
    private bool checkingSessions;
    private BrowserCodeWindow? codeWindow;
    private bool BrowserCodeActive => codeWindow?.IsRunning == true;
    private Mutex? multi;
    private bool busy;
    private bool refreshing;
    private readonly UpdateClient updates = new();
    private readonly CancellationTokenSource updateCancel = new();
    private SignedUpdate? availableUpdate;
    private bool checkingUpdates;
    private readonly Dictionary<Guid, string> noteDrafts = [];
    private Account? noteAccount;
    private bool loadingNote;

    public MainWindow(Vault vault, AppState state, RobloxApi? api = null, bool monitorSessions = true, bool checkUpdates = true)
    {
        this.vault = vault; this.state = state; this.api = api ?? new RobloxApi();
        int removed = AccountEnrollment.RemoveLegacyPlaceholders(state);
        if (removed > 0) vault.Save(state);
        enrollment = new AccountEnrollment(state, () => vault.Save(state));
        browsers = new BrowserHub(vault, this.api, Connected);
        InitializeComponent();
        Refresh();
        UpdateProtectionLabel();
        if (checkUpdates) Loaded += async (_, _) =>
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, ".updates", "last-error.txt")))
                UpdateStatus.Text = "Last update failed. Keep .updates for recovery; Data was preserved.";
            else await CheckUpdatesAsync();
        };
        if (removed > 0) Status.Text = $"Cleared {removed} unfinished login placeholder(s).";
        if (monitorSessions)
        {
            Loaded += async (_, _) => { await CheckSessions(state.Accounts.ToList(), false); if (!closing) sessionTimer.Start(); };
            sessionTimer.Tick += async (_, _) => await CheckSessions(state.Accounts.ToList(), false);
        }
        Closing += (_, e) =>
        {
            if (busy) { e.Cancel = true; Status.Text = "Wait for the current operation to finish before closing."; }
            else
            {
                try { SavePendingNotes(); }
                catch (Exception ex) { e.Cancel = true; Status.Text = "Notes could not be saved. " + Security.Friendly(ex); }
            }
        };
        Closed += (_, _) => { closing = true; updateCancel.Cancel(); updates.Dispose(); updateCancel.Dispose(); sessionTimer.Stop(); if (codeWindow is { WasClosed: false }) codeWindow.Close(); browsers.CloseAll(); DisableMulti(); this.api.Dispose(); };
    }

    private void UpdateProtectionLabel()
    {
        ProtectionStatus.Text = vault.UsesMasterPassword ? "●  Master password protection · Portable" : "⚠  No master password · Not recommended";
        ProtectionStatus.Foreground = vault.UsesMasterPassword ? (System.Windows.Media.Brush)FindResource("Muted") : System.Windows.Media.Brushes.Salmon;
    }
    private async void VaultSecurity_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var dialog = new UnlockVaultWindow(vault, existingState: state) { Owner = this };
        if (dialog.ShowDialog() == true) { UpdateProtectionLabel(); Status.Text = vault.UsesMasterPassword ? "Master password protection enabled." : "No master password (Not recommended). Anyone with the folder can read saved credentials."; }
        return Task.CompletedTask;
    });
    private async Task CheckUpdatesAsync()
    {
        if (checkingUpdates || closing) return;
        checkingUpdates = true; UpdateButton.IsEnabled = false; UpdateStatus.Text = "Checking GitHub…"; UpdateStatus.ToolTip = null;
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
        }
        finally { checkingUpdates = false; if (!closing) UpdateButton.IsEnabled = true; }
    }
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is null) { await CheckUpdatesAsync(); return; }
        if (new UpdateReviewWindow(this, UpdateTrust.CurrentVersion, availableUpdate).ShowDialog() != true) return;
        await Run(async () =>
        {
            if (addition is { IsCompleted: false } || enrollment.Current is not null) throw new UserError("Finish or close the current sign-in queue before updating.");
            UpdateButton.IsEnabled = false;
            try
            {
                vault.Save(state);
                UpdateStatus.Text = "Downloading verified update…";
                var progress = new Progress<double>(value => { if (!closing) UpdateStatus.Text = $"Downloading update… {value:P0}"; });
                string folder = await updates.PrepareAsync(availableUpdate, AppContext.BaseDirectory, progress, updateCancel.Token);
                UpdateStatus.Text = "Preparing update and restart…";
                using var helper = await UpdateInstaller.StartHelperAsync(AppContext.BaseDirectory, folder, vault.Root);
                busy = false;
                try { Close(); }
                finally { if (!closing && !helper.HasExited) helper.Kill(); }
            }
            finally { if (!closing) UpdateButton.IsEnabled = true; }
        });
    }

    private Task Connected(Account account, RobloxIdentity identity, string cookie, bool fromLogin)
    {
        enrollment.Commit(account, identity, cookie, saveCredential: fromLogin); Refresh();
        Status.Text = "Connected @" + identity.Name + ".";
        return Task.CompletedTask;
    }

    private async Task Run(Func<Task> work)
    {
        if (busy) return;
        if (BrowserCodeActive) { Status.Text = "Stop the browser-code queue before changing accounts or launching another action."; return; }
        busy = true; AddButton.IsEnabled = false;
        try { SavePendingNotes(); await work(); }
        catch (Exception ex) { Status.Text = Security.Friendly(ex); MessageBox.Show(this, Security.Friendly(ex), "Account Manager", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { busy = false; AddButton.IsEnabled = true; }
    }

    private Account Selected() => AccountsGrid.SelectedItem as Account ?? throw new UserError("Select an account first.");
    private List<Account> SelectedMany()
    {
        var accounts = AccountsGrid.SelectedItems.Cast<Account>().ToList();
        if (accounts.Count == 0) throw new UserError("Select at least one account first.");
        return accounts;
    }
    private void SaveRefresh(string message) { vault.Save(state); Refresh(); Status.Text = message; }

    private void Refresh()
    {
        if (AccountsGrid is null) return;
        var selection = AccountsGrid.SelectedItems.Cast<Account>().Select(a => a.Id).ToHashSet();
        string group = Groups.SelectedItem as string ?? "All groups";
        refreshing = true;
        var groups = new[] { "All groups" }.Concat(state.Accounts.Select(a => a.Group).Distinct().OrderBy(x => x)).ToList();
        Groups.ItemsSource = groups; Groups.SelectedItem = groups.Contains(group) ? group : "All groups";
        refreshing = false;
        group = Groups.SelectedItem as string ?? "All groups";
        string search = Search.Text.Trim();
        var list = state.Accounts.Where(a => (group == "All groups" || a.Group == group) &&
            (StarredOnly.IsChecked != true || a.Favorite) &&
            (search.Length == 0 || (a.Username + " " + a.DisplayName + " " + a.Alias + " " + a.Group + " " + a.Notes).Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        refreshing = true;
        AccountsGrid.ItemsSource = list;
        foreach (var a in list.Where(a => selection.Contains(a.Id))) AccountsGrid.SelectedItems.Add(a);
        refreshing = false;
        CountLabel.Text = $"{list.Count} of {state.Accounts.Count} accounts";
        EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = state.Accounts.Count == 0 ? "A home for every account" : "No matching accounts";
        EmptyCopy.Text = state.Accounts.Count == 0 ? "Add an account and sign in on Roblox to get started." : "Try a different search or group filter.";
        FavoritesList.ItemsSource = null; FavoritesList.ItemsSource = state.Favorites;
        RecentList.ItemsSource = null; RecentList.ItemsSource = state.Recent;
        SelectionLabel();
    }

    private void SelectionLabel()
    {
        int count = AccountsGrid.SelectedItems.Count;
        SelectedLabel.Text = count == 0 ? "Select an account" : count > 1 ? $"{count} accounts selected" : "@" + Selected().Username;
        if (!refreshing) RefreshNotes();
    }
    private void RefreshNotes()
    {
        if (NotesEditor is null) return;
        noteAccount = AccountsGrid.SelectedItems.Count == 1 ? AccountsGrid.SelectedItem as Account : null;
        loadingNote = true;
        NotesEditor.Text = noteAccount is null ? "" : noteDrafts.GetValueOrDefault(noteAccount.Id, noteAccount.Notes);
        NotesEditor.IsEnabled = noteAccount is not null;
        NoteOwner.Text = noteAccount is null ? "Select one account" : "Note for @" + noteAccount.Username;
        loadingNote = false;
        NoteDraftStatus();
    }
    private void NoteDraftStatus()
    {
        SaveNotesButton.IsEnabled = noteDrafts.Count > 0;
        NotesTab.Header = noteDrafts.Count > 0 ? "Notes *" : "Notes";
        NotesHint.Text = noteAccount is null ? "Select an account to read its full note." :
            noteDrafts.ContainsKey(noteAccount.Id) ? "Unsaved note. Drafts are kept when switching accounts; save here or close the manager to save." :
            noteAccount.Notes.Length == 0 ? "No note yet. Type below to add one." : "Saved note. You can edit it directly below.";
    }
    private void Notes_Changed(object sender, TextChangedEventArgs e)
    {
        if (loadingNote || noteAccount is null) return;
        if (NotesEditor.Text == noteAccount.Notes) noteDrafts.Remove(noteAccount.Id);
        else noteDrafts[noteAccount.Id] = NotesEditor.Text;
        NoteDraftStatus();
    }
    private void SavePendingNotes()
    {
        if (noteDrafts.Count == 0) return;
        BulkEditing.ApplyNotes(state.Accounts, noteDrafts, () => vault.Save(state));
        noteDrafts.Clear();
        AccountsGrid.Items.Refresh(); RefreshNotes();
    }
    private async void SaveNotes_Click(object sender, RoutedEventArgs e) => await Run(() =>
    { Status.Text = "Account notes saved."; return Task.CompletedTask; });
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (!refreshing) Refresh(); }
    private void Group_Changed(object sender, SelectionChangedEventArgs e) { if (!refreshing) Refresh(); }
    private void FavoriteFilter_Changed(object sender, RoutedEventArgs e) { if (!refreshing) Refresh(); }
    private void Accounts_Changed(object sender, SelectionChangedEventArgs e)
    {
        SelectionLabel();
        if (refreshing) return;
        if (AccountsGrid.SelectedItems.Count == 1 && AccountsGrid.SelectedItem is Account a && a.SavedTarget.Length > 0)
        { PlaceInput.Text = a.SavedTarget; JobInput.Text = a.SavedJob; }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserCodeActive) { Status.Text = "Stop the browser-code queue before adding accounts."; return; }
        if (loginQueue.IsRunning) { ResumeLogin(); return; }
        AddButton.ContextMenu.PlacementTarget = AddButton; AddButton.ContextMenu.IsOpen = true;
    }
    private void ResumeLogin()
    {
        if (activeLoginBrowser is { IsClosed: false } browser) { browser.Show(); browser.Activate(); }
    }
    private void ManualAdd_Click(object sender, RoutedEventArgs e) => StartAddition([null]);
    private void SingleAdd_Click(object sender, RoutedEventArgs e) => AddCredentials(false);
    private void BulkAdd_Click(object sender, RoutedEventArgs e) => AddCredentials(true);
    private void AddCredentials(bool bulk)
    {
        if (loginQueue.IsRunning) { ResumeLogin(); return; }
        var dialog = new LoginInputWindow(this, bulk, state.Accounts.Select(a => a.Group));
        if (dialog.ShowDialog() == true) StartAddition(dialog.Credentials.Cast<LoginCredential?>().ToList(), allowSkip: bulk);
    }

    private void StartAddition(IReadOnlyList<LoginCredential?> entries, bool allowSkip = false)
    {
        if (BrowserCodeActive) { foreach (var c in entries) c?.Dispose(); Status.Text = "Stop the browser-code queue before signing in."; return; }
        if (loginQueue.IsRunning || closing) { foreach (var c in entries) c?.Dispose(); ResumeLogin(); return; }
        try { SavePendingNotes(); addition = ProcessAddition(entries, allowSkip); }
        catch (Exception ex) { foreach (var c in entries) c?.Dispose(); Status.Text = Security.Friendly(ex); }
    }
    private async Task ProcessAddition(IReadOnlyList<LoginCredential?> entries, bool allowSkip)
    {
        try
        {
            await loginQueue.Run(entries, async (credential, index) =>
            {
                var account = enrollment.Begin(credential);
                AddButton.Content = $"Sign-in {index + 1}/{entries.Count} · Resume";
                Status.Text = $"Sign-in queue {index + 1}/{entries.Count}. " + (allowSkip ? "Skip advances without saving; " : "") + "close its browser to stop.";
                try
                {
                    activeLoginBrowser = await browsers.Open(account, visible: false);
                    await activeLoginBrowser.BeginLogin(credential, $"{index + 1}/{entries.Count}" + (credential is null ? "" : " · @" + credential.Username), allowSkip);
                    var outcome = await activeLoginBrowser.LoginCompleted;
                    if (outcome == LoginOutcome.Connected && !closing)
                    {
                        Search.Text = ""; StarredOnly.IsChecked = false; Groups.SelectedItem = "All groups";
                        Refresh(); AccountsGrid.SelectedItem = account;
                        activeLoginBrowser.Close();
                    }
                    return closing ? LoginOutcome.Stopped : outcome;
                }
                finally
                {
                    bool committed = enrollment.Committed;
                    enrollment.Finish(); activeLoginBrowser = null;
                    if (!committed && account.UserId == 0 && !closing)
                    {
                        try { await browsers.Clear(account); }
                        catch { Status.Text = "Sign-in canceled. Temporary browser storage could not be cleared; no account was saved."; }
                    }
                }
            });
            if (!closing) Status.Text = $"Sign-in queue finished: {loginQueue.Completed} connected, {loginQueue.Skipped} skipped, {entries.Count - loginQueue.Completed - loginQueue.Skipped} not completed. Unsaved logins discarded.";
        }
        catch (Exception ex) { if (!closing) { Status.Text = Security.Friendly(ex); MessageBox.Show(this, Security.Friendly(ex), "Sign-in queue"); } }
        finally { if (!closing) AddButton.Content = "+  Add account ▾"; addition = null; }
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try { var a = Selected(); StartAddition([new LoginCredential(a.Username, a.SavedPassword)]); }
        catch (Exception ex) { Status.Text = Security.Friendly(ex); }
    }
    private async void Password_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var account = Selected();
        var dialog = new SavedPasswordWindow(this, account.Username, account.SavedPassword);
        if (dialog.ShowDialog() == true)
        {
            string oldPassword = account.SavedPassword; account.SavedPassword = dialog.TakePassword();
            try { SaveRefresh("Saved password updated using the current vault protection setting."); }
            catch { account.SavedPassword = oldPassword; throw; }
        }
        return Task.CompletedTask;
    });
    private async void Browser_Click(object sender, RoutedEventArgs e) => await Run(async () => { await browsers.Open(Selected()); Status.Text = "Account browser opened."; });
    private async void Accounts_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { if (AccountsGrid.SelectedItem is Account) await Run(async () => { await browsers.Open(Selected()); }); }
    private void BrowserCode_Click(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        if (codeWindow is { WasClosed: false }) { codeWindow.Show(); codeWindow.Activate(); return; }
        if (BrowserCodeActive || loginQueue.IsRunning) { Status.Text = "Finish or stop the current queue first."; return; }
        try
        {
            SavePendingNotes();
            var selected = SelectedMany();
            var window = new BrowserCodeWindow(this, selected, browsers, () =>
                busy || closing || loginQueue.IsRunning ? "Finish the current account operation first." :
                selected.Any(a => !state.Accounts.Contains(a)) ? "The selected accounts changed. Close this editor and select them again." : null);
            codeWindow = window;
            window.Closed += (_, _) => { if (!window.IsRunning && ReferenceEquals(codeWindow, window)) codeWindow = null; };
            window.QueueEnded += (_, _) => { if (!closing) Refresh(); if (window.WasClosed && ReferenceEquals(codeWindow, window)) codeWindow = null; };
            window.Show();
        }
        catch (Exception ex) { Status.Text = Security.Friendly(ex); }
    }
    private async void Check_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await CheckSessions(SelectedMany(), true);
    });

    private async Task CheckSessions(List<Account> accounts, bool manual)
    {
        if (checkingSessions || closing || BrowserCodeActive || (!manual && (busy || loginQueue.IsRunning))) return;
        checkingSessions = true;
        bool rateLimited = false;
        try
        {
            foreach (var a in accounts)
            {
                if (closing || !state.Accounts.Contains(a)) break;
                if (ReferenceEquals(enrollment.Current, a)) continue;
                a.SessionStatus = "Checking…"; Refresh();
                try { await browsers.VerifiedCookie(a); }
                catch (RobloxApiError ex) when (ex.StatusCode == 429) { rateLimited = true; break; }
                catch { /* BrowserHub records expired, verification, and transient failures separately. */ }
                if (!closing) Refresh();
                await Task.Delay(650);
            }
            if (manual && !closing) Status.Text = rateLimited ? "Roblox rate limited session checks. Remaining accounts were left unchanged; retry later." : "Session check finished. Expired accounts can be reconnected with Sign in.";
        }
        finally { checkingSessions = false; }
    }

    private async void Join_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var accounts = SelectedMany();
        string rawTarget = PlaceInput.Text.Trim();
        var target = GameTarget.Parse(rawTarget); string job = GameTarget.ValidateJob(JobInput.Text);
        if (target.PrivateUrl is not null && job.Length > 0) throw new UserError("Clear the Job ID when using a private server link.");
        foreach (var a in accounts)
        {
            Status.Text = "Verifying @" + a.Username + "…";
            string cookie = await browsers.VerifiedCookie(a);
            if (target.PrivateUrl is not null)
            {
                await browsers.Open(a, target.PrivateUrl);
                Status.Text = "Private server opened for @" + a.Username + ". Click Play on Roblox to join.";
            }
            else
            {
                string ticket = await api.Ticket(cookie);
                Security.LaunchRoblox(Security.BuildLaunchUri(target.PlaceId!.Value, job, ticket));
                a.LastUsed = DateTimeOffset.UtcNow;
                Status.Text = "Launch request sent for @" + a.Username + ".";
            }
            var recent = new GameBookmark { Name = target.PlaceId is long p ? "Place " + p : "Private server", Target = rawTarget, Job = job };
            state.Recent.RemoveAll(g => g.Target == recent.Target && g.Job == job); state.Recent.Insert(0, recent);
            if (state.Recent.Count > 30) state.Recent.RemoveRange(30, state.Recent.Count - 30);
            vault.Save(state);
            if (accounts.Count > 1) await Task.Delay(1700);
        }
        Refresh();
    });

    private async void BulkEdit_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        EditMany(SelectedMany()); return Task.CompletedTask;
    });
    private void EditMany(List<Account> accounts)
    {
        if (accounts.Any(a => ReferenceEquals(a, enrollment.Current))) throw new UserError("Finish or close the active sign-in before editing that account.");
        var dialog = new BulkEditWindow(this, accounts, state.Accounts.Select(a => a.Group), changes =>
        {
            BulkEditing.Apply(accounts, changes, () => vault.Save(state));
            Refresh(); Status.Text = "Updated " + accounts.Count + " selected accounts.";
        });
        dialog.ShowDialog();
    }
    private async void Edit_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var selected = SelectedMany();
        if (selected.Count > 1) { EditMany(selected); return Task.CompletedTask; }
        var a = selected[0];
        var dialog = new FormDialog(this, "Edit account", "Add a label, group, and notes for @" + a.Username,
            [("Alias", a.Alias, 80), ("Group", a.Group, 80), ("Notes", a.Notes, 4000)]);
        if (dialog.ShowDialog() == true)
        { a.Alias = dialog.Values[0].Trim(); a.Group = string.IsNullOrWhiteSpace(dialog.Values[1]) ? "Default" : dialog.Values[1].Trim(); a.Notes = dialog.Values[2]; SaveRefresh("Account details saved."); }
        return Task.CompletedTask;
    });
    private async void Star_Click(object sender, RoutedEventArgs e) => await Run(() => { foreach (var a in SelectedMany()) a.Favorite = !a.Favorite; SaveRefresh("Favorites updated."); return Task.CompletedTask; });
    private async void Up_Click(object sender, RoutedEventArgs e) => await Move(-1);
    private async void Down_Click(object sender, RoutedEventArgs e) => await Move(1);
    private async Task Move(int delta) => await Run(() =>
    {
        var a = Selected(); int index = state.Accounts.IndexOf(a); int next = index + delta;
        if (next >= 0 && next < state.Accounts.Count) { state.Accounts.RemoveAt(index); state.Accounts.Insert(next, a); SaveRefresh("Account order saved."); }
        return Task.CompletedTask;
    });
    private async void SaveTarget_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        GameTarget.Parse(PlaceInput.Text); string job = GameTarget.ValidateJob(JobInput.Text);
        foreach (var a in SelectedMany()) { a.SavedTarget = PlaceInput.Text.Trim(); a.SavedJob = job; }
        SaveRefresh("Default game target saved for selected accounts."); return Task.CompletedTask;
    });
    private async void SignOut_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var accounts = SelectedMany();
        if (MessageBox.Show(this, $"Clear local browser sessions for {accounts.Count} selected account(s)? You will need to sign in again. This does not revoke sessions on other devices.", "Sign out locally", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        foreach (var a in accounts) await browsers.Clear(a);
        SaveRefresh("Selected local sessions cleared. Saved passwords were kept for signing in again.");
    });
    private async void Remove_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var accounts = SelectedMany();
        if (accounts.Any(a => ReferenceEquals(a, enrollment.Current))) throw new UserError("Close the active sign-in browser before removing this account.");
        if (MessageBox.Show(this, $"Remove {accounts.Count} selected account(s) and clear their local browser sessions?", "Remove accounts", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        foreach (var a in accounts) { await browsers.Clear(a); state.Accounts.Remove(a); vault.Save(state); }
        Refresh(); Status.Text = "Selected accounts removed.";
    });

    private async void Bookmark_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        GameTarget.Parse(PlaceInput.Text); string job = GameTarget.ValidateJob(JobInput.Text);
        var dialog = new FormDialog(this, "Favorite game", "Save this game or private server link for quick access.", [("Name", "", 100)]);
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Values[0]))
        { state.Favorites.Add(new() { Name = dialog.Values[0].Trim(), Target = PlaceInput.Text.Trim(), Job = job }); SaveRefresh("Game added to favorites."); }
        return Task.CompletedTask;
    });
    private void LoadBookmark(GameBookmark? game) { if (game is not null) { PlaceInput.Text = game.Target; JobInput.Text = game.Job; Status.Text = "Loaded " + game.Name + "."; } }
    private void Bookmark_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LoadBookmark(FavoritesList.SelectedItem as GameBookmark);
    private void Recent_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LoadBookmark(RecentList.SelectedItem as GameBookmark);
    private async void RemoveBookmark_Click(object sender, RoutedEventArgs e) => await Run(() =>
    { if (FavoritesList.SelectedItem is GameBookmark g) { state.Favorites.Remove(g); SaveRefresh("Favorite removed."); } return Task.CompletedTask; });

    private async void Servers_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var target = GameTarget.Parse(PlaceInput.Text);
        if (target.PrivateUrl is not null || target.PlaceId is null) throw new UserError("Enter a public game place ID to browse its servers.");
        var dialog = new ServerWindow(this, api, target.PlaceId.Value);
        if (dialog.ShowDialog() == true) { JobInput.Text = dialog.SelectedJob; Status.Text = "Server loaded. Click Join server to launch the selected account(s)."; }
        return Task.CompletedTask;
    });

    private void Multi_Changed(object sender, RoutedEventArgs e)
    {
        if (MultiInstance.IsChecked != true) { DisableMulti(); return; }
        try
        {
            foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta")) { p.Dispose(); throw new UserError("Close Roblox before enabling multi-instance mode."); }
            multi = new Mutex(true, "ROBLOX_singletonMutex", out bool created);
            if (!created) { multi.Dispose(); multi = null; throw new UserError("The legacy Roblox mutex is already in use. Close other launchers and Roblox, then retry."); }
            Status.Text = "Legacy multi-instance mode enabled. Current Roblox versions may still limit simultaneous clients.";
        }
        catch (Exception ex) { MultiInstance.IsChecked = false; Status.Text = Security.Friendly(ex); }
    }
    private void DisableMulti() { if (multi is not null) { multi.ReleaseMutex(); multi.Dispose(); multi = null; } }
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Roblox Account Manager · Portable 1.5.0\n\n• Single and bulk sign-in queues\n• Optional master-password protection for portable accounts and cookies\n• Session checks on startup and every five minutes\n• Autofill restricted to Roblox's HTTPS login page\n• Publisher-signed updates installed only by your request\n• No remote API, telemetry, or plugins\n\nYou complete Roblox login and verification yourself. Layout exports exclude passwords and cookies. Software running as your Windows user may access unlocked data.\n\nGPL-3.0-only. No warranty. Corresponding source and attribution are provided with the release.\n\nSee LICENSE, NOTICE.md and README.md in Docs for details.", "About", MessageBoxButton.OK);
    private async void Data_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var dialog = new DataWindow(this, vault, state, restored =>
        {
            var merged = new AppState { Accounts = [.. state.Accounts], Favorites = [.. state.Favorites], Recent = [.. state.Recent] };
            // Re-key imported entries: a layout backup must never bind to an unrelated local browser profile.
            foreach (var a in restored.Accounts)
            {
                if (a.UserId > 0 && merged.Accounts.Any(existing => existing.UserId == a.UserId)) continue;
                a.Id = Guid.NewGuid(); a.SessionStatus = "Sign in required"; merged.Accounts.Add(a);
            }
            foreach (var game in restored.Favorites) if (!merged.Favorites.Any(g => g.Target == game.Target && g.Job == game.Job)) merged.Favorites.Add(game);
            vault.Save(merged);
            state.Accounts = merged.Accounts; state.Favorites = merged.Favorites;
            Refresh(); Status.Text = "Layout restored. Sign in to each imported account to reconnect its session.";
        });
        dialog.ShowDialog(); return Task.CompletedTask;
    });
}
