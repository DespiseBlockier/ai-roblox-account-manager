using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;

namespace LocalAccountManager.Linux;

internal sealed class MainWindow : Window
{
    private readonly Vault vault;
    private readonly AppState state;
    private readonly RobloxApi api = new();
    private readonly AccountChecks checks;
    private readonly LinuxBrowser browser;
    private readonly LoginQueue queue = new();
    private readonly DataGrid table = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
    };
    private readonly TextBox filter = new() { PlaceholderText = "Search names, groups or notes" };
    private readonly CheckBox starred = new() { Content = "Favorites only" };
    private readonly TextBox game = new() { PlaceholderText = "Place ID or Roblox game link", MinWidth = 310, MaxLength = 4096 };
    private readonly TextBlock status = Ui.Label("Ready");
    private readonly TextBlock details = Ui.Label("Select an account to see its note.");
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly StackPanel commands = new() { Spacing = 3 };
    private readonly Button stop = new() { Content = "Stop", IsVisible = false };
    private readonly Button skip = new() { Content = "Skip sign-in", IsVisible = false };
    private CancellationTokenSource? current;
    private bool busy;
    private bool changingRows;

    internal MainWindow(Vault vault, AppState state)
    {
        this.vault = vault;
        this.state = state;
        browser = new(api);
        var enrollment = new AccountEnrollment(state, Save);
        checks = new AccountChecks(api, account => account.SessionCookie.Length == 0
            ? Task.FromException<string>(new MissingSessionError()) : Task.FromResult(account.SessionCookie),
            (account, identity, cookie) =>
            {
                if (account.Username != identity.Name || account.DisplayName != identity.DisplayName)
                    enrollment.Commit(account, identity, cookie, saveCredential: false);
                return Task.CompletedTask;
            });

        Title = $"Roblox Account Manager · {Program.Version}";
        Width = 1080; Height = 735; MinWidth = 680; MinHeight = 475;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        table.Columns.Add(Column("Account", account => string.Join(" ", account.Star, account.Label).Trim() + "\n" + account.Handle, 230));
        table.Columns.Add(Column("Group", account => account.Group, 110));
        table.Columns.Add(Column("Session", account => account.SessionStatus, 150, account => account.SessionColor));
        table.Columns.Add(Column("Moderation", account => string.IsNullOrEmpty(account.ModerationTiming)
            ? account.ModerationLabel : account.ModerationLabel + "\n" + account.ModerationTiming, 170, account => account.ModerationColor));
        table.Columns.Add(Column("Age group", account => account.AgeGroupLabel["Age group: ".Length..], 90));
        table.Columns.Add(Column("Robux", account => account.RobuxLabel["Robux: ".Length..], 100));
        table.Columns.Add(Column("Note", account => account.NotePreview, 170));
        table.SelectionChanged += (_, _) =>
        {
            if (changingRows) return;
            UpdateDetails();
            if (Selected().Length == 1 && game.Text is null or "") game.Text = Selected()[0].SavedTarget;
        };
        filter.TextChanged += (_, _) => Refresh();
        starred.IsCheckedChanged += (_, _) => Refresh();
        stop.Click += (_, _) => current?.Cancel();
        skip.Click += (_, _) => attemptCancellation?.Cancel();

        commands.Children.Add(Ui.Row(
            Command("Add account", token => SignIn(null, null, SaveIdentity, token)),
            Command("Bulk sign-in", BulkSignIn), Command("Sign in · saved login", Reconnect),
            Command("Open browser", Browse), Command("Check status", RefreshSelected)));
        commands.Children.Add(Ui.Row(Command("Edit / bulk edit", Edit), Command("Remove", Remove),
            Command("Deselect", _ => { table.SelectedItems.Clear(); return Task.CompletedTask; }),
            Command("Vault security", _ => VaultDialogs.Security(this, vault, state)),
            Command("Layout backup", _ => VaultDialogs.Layout(this, vault, state, Refresh))));
        commands.Children.Add(Ui.Row(game, Command("Check game access", GameAccess),
            Command("Open game page", OpenGame), Command("Public servers", Servers)));
        commands.Children.Add(Ui.Row(Command("Save game target", SaveTarget), Command("Game bookmarks", Bookmarks),
            Command("Save bookmark", SaveBookmark)));
        var heading = Ui.Stack(new TextBlock { Text = "Accounts", FontSize = 24, FontWeight = FontWeight.SemiBold },
            commands, Ui.Row(filter, starred, stop, skip));
        var footer = Ui.Stack(details, status);
        details.MaxHeight = 84;
        var layout = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(footer);
        table.Margin = new Thickness(16, 0);
        layout.Children.Add(table);
        Content = layout;
        Refresh();
        Opened += (_, _) => { timer.Start(); AutoRefresh(); };
        timer.Tick += (_, _) => AutoRefresh();
        Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            status.Text = "Stop the current operation or wait for it to finish before closing.";
        };
        Closed += (_, _) => { timer.Stop(); api.Dispose(); };
    }

    private static DataGridTemplateColumn Column(string header, Func<Account, string> content, int width,
        Func<Account, string>? color = null) => new()
    {
        Header = header, Width = new DataGridLength(width),
        CellTemplate = new FuncDataTemplate<Account>((account, _) =>
        {
            var cell = Ui.Label(account is null ? "" : content(account));
            cell.Margin = new Thickness(5, 4);
            cell.TextTrimming = TextTrimming.CharacterEllipsis;
            cell.MaxLines = 2;
            if (account is not null && color is not null) cell.Foreground = Brush.Parse(color(account));
            if (account is not null && header == "Moderation") ToolTip.SetTip(cell, account.ModerationDetails);
            return cell;
        }, supportsRecycling: false)
    };

    private void Save() => vault.Save(state);
    private void SaveIdentity(RobloxIdentity identity, string cookie) =>
        BrowserAccountStore.SaveVerified(state, identity, cookie, null, Save);
    private Account[] Selected() => table.SelectedItems.OfType<Account>().ToArray();
    private Account[] Selection()
    {
        var selected = Selected();
        if (selected.Length == 0) throw new UserError("Select one or more accounts first.");
        return selected;
    }
    private Account? BrowserSelection()
    {
        var selected = Selected();
        if (selected.Length > 1) throw new UserError("Select one account, or deselect all to open a guest browser.");
        return selected.FirstOrDefault();
    }
    private void UpdateDetails()
    {
        var selected = Selected();
        details.Text = selected.Length switch
        {
            1 => selected[0].Handle + " · " + selected[0].CredentialLabel + "\nNote: " +
                (selected[0].Notes.Length > 0 ? selected[0].Notes : "None"),
            0 => $"{state.Accounts.Count} saved accounts · select an account to see its note.",
            _ => $"{selected.Length} accounts selected"
        };
    }
    private void Refresh()
    {
        var selected = Selected();
        string query = filter.Text?.Trim() ?? "";
        var visible = state.Accounts.Where(a => (starred.IsChecked != true || a.Favorite) &&
            (query.Length == 0 || new[] { a.Username, a.Label, a.Group, a.Notes }
                .Any(field => field.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(a => a.Favorite).ThenBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Username, StringComparer.OrdinalIgnoreCase).ToArray();
        changingRows = true;
        try
        {
            table.ItemsSource = visible;
            foreach (var account in selected.Intersect(visible)) table.SelectedItems.Add(account);
        }
        finally { changingRows = false; }
        UpdateDetails();
    }
    private Button Command(string label, Func<CancellationToken, Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await Run(action);
        return button;
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true;
        using var cancel = new CancellationTokenSource();
        current = cancel;
        commands.IsEnabled = false;
        stop.IsVisible = true;
        try { await action(cancel.Token); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { status.Text = "Stopped."; }
        catch (Exception ex) { status.Text = Ui.Error(ex); }
        finally
        {
            busy = false; current = null; commands.IsEnabled = true;
            stop.IsVisible = skip.IsVisible = false;
            Refresh();
        }
    }
    private async void AutoRefresh()
    {
        if (busy) return;
        var accounts = state.Accounts.Where(a => a.SessionCookie.Length > 0).ToArray();
        if (accounts.Length > 0) await Run(token => CheckStatus(accounts, token));
    }
    private Task RefreshSelected(CancellationToken token) =>
        CheckStatus(Selected().Length > 0 ? Selected() : state.Accounts.ToArray(), token);
    private async Task CheckStatus(Account[] accounts, CancellationToken token)
    {
        int issues = 0;
        for (int i = 0; i < accounts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var account = accounts[i];
            status.Text = $"Checking status {i + 1}/{accounts.Length} · {account.Handle}";
            try { await checks.CheckHealth(account); }
            catch (RobloxApiError ex) when (ex.StatusCode == 429) { Save(); throw; }
            catch (UserError) { issues++; }
            catch (System.Net.Http.HttpRequestException) { issues++; }
            catch (TaskCanceledException) { issues++; }
            Save();
        }
        status.Text = $"Status checked for {accounts.Length} account(s). {issues} need attention.";
    }

    private async Task SignIn(Account? account, LoginCredential? credential, Action<RobloxIdentity, string> save, CancellationToken token)
    {
        status.Text = "Finish signing in in Chromium.";
        bool connected = await browser.SignIn(account, credential, save, text => status.Text = text, token);
        status.Text = connected ? "Signed in and saved the account." : "Sign-in browser closed.";
    }
    private async Task Reconnect(CancellationToken token)
    {
        var account = BrowserSelection() ?? throw new UserError("Select the account you want to reconnect.");
        using var credential = new LoginCredential(account.Username, account.SavedPassword);
        var enrollment = new AccountEnrollment(state, Save);
        await SignIn(account, credential, (identity, cookie) => enrollment.Commit(account, identity, cookie, false), token);
    }
    private CancellationTokenSource? attemptCancellation;
    private async Task BulkSignIn(CancellationToken token)
    {
        var entries = await VaultDialogs.BulkImport(this);
        if (entries is null) return;
        var enrollment = new AccountEnrollment(state, Save);
        int failures = 0;
        skip.IsVisible = true;
        await queue.Run(entries, async (entry, index) =>
        {
            if (entry is null || token.IsCancellationRequested) return LoginOutcome.Stopped;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attemptCancellation = attempt;
            status.Text = $"Sign-in {index + 1}/{entries.Count}" + (entry.Username.Length > 0 ? $" · @{entry.Username}" : " · checking cookie");
            try
            {
                if (entry.SessionCookie.Length > 0)
                {
                    var identity = await api.Identity(entry.SessionCookie).WaitAsync(attempt.Token);
                    attempt.Token.ThrowIfCancellationRequested();
                    SaveIdentity(identity, entry.SessionCookie);
                    return LoginOutcome.Connected;
                }
                var pending = enrollment.Begin(entry);
                return await browser.SignIn(pending, entry, (identity, cookie) => enrollment.Commit(pending, identity, cookie),
                    text => status.Text = $"Sign-in {index + 1}/{entries.Count} · {text}", attempt.Token)
                    ? LoginOutcome.Connected : LoginOutcome.Stopped;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return LoginOutcome.Stopped; }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { return LoginOutcome.Skipped; }
            catch (RobloxApiError ex) when (ex.StatusCode == 401 && entry.SessionCookie.Length > 0)
            {
                failures++;
                return LoginOutcome.Skipped;
            }
            finally { attemptCancellation = null; enrollment.Finish(); Refresh(); }
        });
        status.Text = $"Bulk sign-in finished · {queue.Completed} saved · {queue.Skipped - failures} skipped · {failures} expired cookies.";
    }
    private async Task Browse(CancellationToken token) => await Browse(LinuxBrowser.Home, token);
    private async Task Browse(string url, CancellationToken token)
    {
        var account = BrowserSelection();
        string cookie = account?.SessionCookie ?? "";
        status.Text = account is null ? "Guest browser open." : $"Browser open for {account.Handle}.";
        await browser.Browse(url, cookie, token);
        status.Text = "Browser closed.";
    }

    private Task Edit(CancellationToken _) => VaultDialogs.Edit(this, Selection(), Save);
    private async Task Remove(CancellationToken _)
    {
        var selected = Selection();
        var panel = Ui.Stack(Ui.Label($"Remove {selected.Length} account(s) from this vault?"));
        var dialog = Ui.Dialog("Remove accounts", panel, 460, 215);
        panel.Children.Add(Ui.Row(Ui.Button("Remove accounts", () => dialog.Close(true)), Ui.Button("Cancel", () => dialog.Close(false))));
        if (!await dialog.ShowDialog<bool>(this)) return;
        var original = state.Accounts.ToList();
        state.Accounts.RemoveAll(selected.Contains);
        try { Save(); }
        catch { state.Accounts = original; throw; }
        status.Text = $"Removed {selected.Length} account(s).";
    }
    private GameTarget Target() => GameTarget.Parse(game.Text ?? "");
    private long Place() => Target().PlaceId ?? throw new UserError("Enter the game's place ID or game page link.");
    private async Task GameAccess(CancellationToken token)
    {
        var accounts = Selection();
        long place = Place();
        var results = new List<string>();
        for (int i = 0; i < accounts.Length; i++)
        {
            var account = accounts[i];
            if (token.IsCancellationRequested)
            {
                results.Add($"{account.Handle} — stopped before check");
                continue;
            }
            status.Text = $"Checking game access {i + 1}/{accounts.Length} · {account.Handle}";
            try { results.Add($"{account.Handle} — {await checks.CheckGameAccess(account, place)}"); }
            catch (RobloxApiError ex) when (ex.StatusCode == 429)
            {
                results.Add($"{account.Handle} — {ex.Message}");
                results.AddRange(accounts.Skip(i + 1).Select(a => $"{a.Handle} — skipped because Roblox rate limited the check"));
                break;
            }
            catch (Exception ex) { results.Add($"{account.Handle} — {Ui.Error(ex)}"); }
        }
        Save();
        status.Text = $"Game access results for place {place}.";
        await Ui.Message(this, "Game access results", string.Join("\n\n", results));
    }
    private Task OpenGame(CancellationToken token)
    {
        var target = Target();
        return Browse(target.PrivateUrl ?? $"https://www.roblox.com/games/{target.PlaceId}", token);
    }
    private async Task Servers(CancellationToken token)
    {
        long place = Place();
        var page = await api.Servers(place, null, true).WaitAsync(token);
        await Ui.Message(this, "Public servers", page.Servers.Count == 0 ? "No public servers found." :
            string.Join("\n\n", page.Servers.Select(s => $"{s.Playing}/{s.MaxPlayers} players · {s.Ping?.ToString() ?? "?"} ms\n{s.Id}")));
    }
    private Task SaveTarget(CancellationToken _)
    {
        var target = Target();
        string value = target.PrivateUrl ?? target.PlaceId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var previous = Selection().ToDictionary(a => a, a => a.SavedTarget);
        foreach (var account in previous.Keys) account.SavedTarget = value;
        try { Save(); }
        catch { foreach (var entry in previous) entry.Key.SavedTarget = entry.Value; throw; }
        status.Text = "Saved the target for the selected accounts.";
        return Task.CompletedTask;
    }
    private async Task Bookmarks(CancellationToken _)
    {
        var items = state.Favorites.Concat(state.Recent).ToArray();
        var picker = new ListBox { ItemsSource = items.Select(x => $"{x.Name} · {x.Target}").ToArray(), MinHeight = 230 };
        var panel = Ui.Stack(picker);
        var dialog = Ui.Dialog("Game bookmarks and recent games", panel);
        panel.Children.Add(Ui.Button("Use selected target", () =>
        {
            if (picker.SelectedIndex < 0) return;
            game.Text = items[picker.SelectedIndex].Target;
            dialog.Close();
        }));
        await dialog.ShowDialog(this);
    }
    private async Task SaveBookmark(CancellationToken _)
    {
        var target = Target();
        string value = target.PrivateUrl ?? target.PlaceId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var name = new TextBox { PlaceholderText = "Bookmark name", MaxLength = 80 };
        var panel = Ui.Stack(name);
        var dialog = Ui.Dialog("Save game bookmark", panel, 450, 220);
        panel.Children.Add(Ui.Button("Save bookmark", () => dialog.Close(name.Text)));
        string? label = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(label)) return;
        var bookmark = new GameBookmark { Name = label.Trim(), Target = value };
        state.Favorites.Add(bookmark);
        try { Save(); }
        catch { state.Favorites.Remove(bookmark); throw; }
        status.Text = "Game bookmark saved.";
    }
}
