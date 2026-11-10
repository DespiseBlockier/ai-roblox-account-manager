using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using QRCoder;

namespace LocalAccountManager.Linux;

internal static class VaultDialogs
{
    internal static async Task<List<LoginCredential>?> BulkImport(Window owner)
    {
        var formats = new ComboBox
        {
            ItemsSource = new[] { "Username and password with optional group and note", "Username and password", "Session cookies" },
            SelectedIndex = 0
        };
        var example = Ui.Label("");
        void ShowFormat() => example.Text = formats.SelectedIndex switch
        {
            0 => "One account per line. Example: username:password;group;\"Note\"",
            1 => "One username:password pair per line.",
            _ => "One Roblox session cookie per line."
        };
        formats.SelectionChanged += (_, _) => ShowFormat();
        ShowFormat();
        var text = new TextBox { AcceptsReturn = true, MinHeight = 160, MaxHeight = 240, PlaceholderText = "Paste accounts here" };
        var status = Ui.Label("");
        var panel = Ui.Stack(formats, example, text, status);
        var dialog = Ui.Dialog("Bulk sign-in", panel, 680, 470);
        panel.Children.Add(Ui.Row(Ui.Button("Start", () =>
        {
            try { dialog.Close(LoginInput.ParseBulk(text.Text ?? "", (BulkLoginFormat)formats.SelectedIndex)); }
            catch (Exception ex) { status.Text = Ui.Error(ex); }
        }), Ui.Button("Cancel", () => dialog.Close())));
        try { return await dialog.ShowDialog<List<LoginCredential>?>(owner); }
        finally { text.Text = ""; }
    }

    internal static async Task Edit(Window owner, Account[] accounts, Action save)
    {
        var account = accounts.Length == 1 ? accounts[0] : null;
        var group = new TextBox { Text = account?.Group, PlaceholderText = "Group", MaxLength = 80 };
        var alias = new TextBox { Text = account?.Alias, PlaceholderText = "Alias", MaxLength = 80 };
        var note = new TextBox { Text = account?.Notes, PlaceholderText = "Note", AcceptsReturn = true, MaxLength = 4000, MinHeight = 74, MaxHeight = 150 };
        var applyGroup = new CheckBox { Content = "Set group", IsChecked = account is not null };
        var applyAlias = new CheckBox { Content = "Set alias · supports {username}, {displayName}, {index}", IsChecked = account is not null };
        var applyNote = new CheckBox { Content = "Set note", IsChecked = account is not null };
        var append = new CheckBox { Content = "Append to existing notes" };
        var favorites = new ComboBox
        {
            ItemsSource = new[] { "Keep favorites", "Mark as favorite", "Remove favorite" },
            SelectedIndex = account is null ? 0 : account.Favorite ? 1 : 2
        };
        var status = Ui.Label("");
        var panel = Ui.Stack(applyGroup, group, applyAlias, alias, applyNote, note, append, favorites, status);
        var dialog = Ui.Dialog(account is null ? $"Edit {accounts.Length} accounts" : $"Edit @{account.Username}",
            new ScrollViewer { Content = panel }, 570, 515);
        panel.Children.Add(Ui.Button("Save", () =>
        {
            try
            {
                BulkEditing.Apply(accounts, new BulkChanges(
                    applyGroup.IsChecked == true ? group.Text ?? "" : null,
                    applyAlias.IsChecked == true ? alias.Text ?? "" : null,
                    applyNote.IsChecked == true ? note.Text ?? "" : null, append.IsChecked == true,
                    favorites.SelectedIndex == 0 ? null : favorites.SelectedIndex == 1), save);
                dialog.Close();
            }
            catch (Exception ex) { status.Text = Ui.Error(ex); }
        }));
        await dialog.ShowDialog(owner);
    }

    internal static async Task Security(Window owner, Vault vault, AppState state)
    {
        var current = Ui.Secret("Current master password");
        current.IsVisible = vault.UsesMasterPassword;
        var factor = Ui.Secret("Authenticator code or recovery code");
        factor.IsVisible = state.Authentication.Enabled;
        var next = Ui.Secret("New master password");
        var repeat = Ui.Secret("Repeat new master password");
        var clear = new CheckBox { Content = "No master password (not recommended)" };
        clear.IsCheckedChanged += (_, _) => next.IsEnabled = repeat.IsEnabled = clear.IsChecked != true;
        var status = Ui.Label("");
        var panel = Ui.Stack(Ui.Label(state.Authentication.Enabled ? "Authenticator 2FA is enabled." : "Authenticator 2FA is disabled."),
            current, factor, Ui.Label("Use a long password. Without a master password, anyone with the data folder can read your saved logins."),
            next, repeat, clear, status);
        var dialog = Ui.Dialog("Vault security", new ScrollViewer { Content = panel }, 590, 485);
        var change = Ui.Button("Change master password", () =>
        {
            try
            {
                if (clear.IsChecked != true && (next.Text ?? "") != (repeat.Text ?? "")) throw new UserError("The passwords do not match.");
                vault.ChangeProtection(state, clear.IsChecked == true ? null : next.Text ?? "", current.Text ?? "", factor.Text ?? "");
                dialog.Close();
            }
            catch (Exception ex) { status.Text = Ui.Error(ex); }
            finally { next.Text = repeat.Text = factor.Text = ""; }
        });
        var actions = Ui.Row(change);
        if (state.Authentication.Enabled)
        {
            actions.Children.Add(Ui.Button("Disable 2FA", () =>
            {
                try { vault.DisableAuthenticator(state, current.Text ?? "", factor.Text ?? ""); dialog.Close(); }
                catch (Exception ex) { status.Text = Ui.Error(ex); }
                finally { factor.Text = ""; }
            }));
            var regenerate = new Button { Content = "New recovery codes" };
            regenerate.Click += async (_, _) =>
            {
                try
                {
                    var codes = vault.RegenerateRecoveryCodes(state, current.Text ?? "", factor.Text ?? "");
                    factor.Text = "";
                    await ShowCodes(dialog, codes);
                }
                catch (Exception ex) { status.Text = Ui.Error(ex); }
            };
            actions.Children.Add(regenerate);
        }
        else if (vault.UsesMasterPassword)
        {
            var enable = new Button { Content = "Set up 2FA" };
            enable.Click += async (_, _) =>
            {
                try
                {
                    if (await SetUpFactor(dialog, vault, state)) dialog.Close();
                }
                catch (Exception ex) { status.Text = Ui.Error(ex); }
            };
            actions.Children.Add(enable);
        }
        panel.Children.Add(actions);
        try { await dialog.ShowDialog(owner); }
        finally { current.Text = next.Text = repeat.Text = factor.Text = ""; }
    }

    private static async Task<bool> SetUpFactor(Window owner, Vault vault, AppState state)
    {
        string secret = Authenticator.NewSecret();
        const string issuer = "Roblox Account Manager";
        string uri = $"otpauth://totp/{Uri.EscapeDataString(issuer + ":Local vault")}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
        byte[] bytes = PngByteQRCodeHelper.GetQRCode(uri, QRCodeGenerator.ECCLevel.Q, 5);
        using var stream = new MemoryStream(bytes);
        using var qr = new Bitmap(stream);
        CryptographicOperations.ZeroMemory(bytes);
        var password = Ui.Secret("Current master password");
        var code = Ui.Secret("Current six-digit code");
        var status = Ui.Label("");
        var panel = Ui.Stack(Ui.Label("Scan this QR code with your authenticator app."), new Image { Source = qr, Height = 210 },
            new SelectableTextBlock { Text = secret }, password, code, status);
        var dialog = Ui.Dialog("Set up vault authenticator", new ScrollViewer { Content = panel }, 500, 545);
        var enable = new Button { Content = "Enable 2FA" };
        enable.Click += async (_, _) =>
        {
            try
            {
                var codes = vault.EnableAuthenticator(state, password.Text ?? "", secret, code.Text ?? "");
                password.Text = code.Text = "";
                await ShowCodes(dialog, codes);
                dialog.Close(true);
            }
            catch (Exception ex) { status.Text = Ui.Error(ex); }
        };
        panel.Children.Add(enable);
        try { return await dialog.ShowDialog<bool>(owner); }
        finally { password.Text = code.Text = ""; }
    }

    private static Task ShowCodes(Window owner, IEnumerable<string> codes) =>
        Ui.Message(owner, "Vault recovery codes",
            "Save these recovery codes somewhere secure. Each can be used once.\n\n" + string.Join("\n", codes));

    internal static async Task Layout(Window owner, Vault vault, AppState state, Action refresh)
    {
        var password = Ui.Secret("Layout backup password");
        var status = Ui.Label("");
        var panel = Ui.Stack(Ui.Label("Layout backups contain account names, groups, notes and bookmarks. Saved logins stay in the portable vault."),
            password, status);
        var dialog = Ui.Dialog("Encrypted layout backup", panel, 550, 280);
        var export = new Button { Content = "Export layout" };
        var import = new Button { Content = "Merge layout" };
        var buttons = Ui.Row(export, import);
        panel.Children.Add(buttons);
        export.Click += async (_, _) =>
        {
            buttons.IsEnabled = false;
            try
            {
                var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save encrypted layout", SuggestedFileName = "roblox.ramlayout", DefaultExtension = "ramlayout"
                });
                if (file is null) return;
                string path = file.TryGetLocalPath() ?? throw new UserError("Select a local folder for the backup.");
                Vault.ExportLayout(path, state, password.Text ?? "");
                status.Text = "Encrypted layout saved.";
            }
            catch (Exception ex) { status.Text = Ui.Error(ex); }
            finally { password.Text = ""; buttons.IsEnabled = true; }
        };
        import.Click += async (_, _) =>
        {
            buttons.IsEnabled = false;
            try
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open encrypted layout" });
                if (files.Count == 0) return;
                string path = files[0].TryGetLocalPath() ?? throw new UserError("Select a local backup file.");
                var incoming = Vault.ImportLayout(path, password.Text ?? "");
                var accounts = state.Accounts;
                var bookmarks = state.Favorites;
                state.Accounts = [.. accounts, .. incoming.Accounts.Where(a => !accounts.Any(b =>
                    a.Id == b.Id || a.UserId > 0 && a.UserId == b.UserId || a.Username.Equals(b.Username, StringComparison.OrdinalIgnoreCase)))];
                state.Favorites = [.. bookmarks, .. incoming.Favorites.Where(a => !bookmarks.Any(b => a.Target == b.Target && a.Job == b.Job))];
                try { vault.Save(state); }
                catch { state.Accounts = accounts; state.Favorites = bookmarks; throw; }
                refresh();
                status.Text = "Layout merged. Use Sign in to connect imported accounts.";
            }
            catch (Exception ex) { status.Text = Ui.Error(ex); }
            finally { password.Text = ""; buttons.IsEnabled = true; }
        };
        try { await dialog.ShowDialog(owner); }
        finally { password.Text = ""; }
    }
}
