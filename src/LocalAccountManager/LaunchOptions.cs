using System.IO;
using System.Security.Cryptography;

namespace LocalAccountManager;

public sealed class LaunchOptions : IDisposable
{
    public string? DataDirectory { get; private set; }
    public string? Password { get; private set; }
    public string? UnlockToken { get; private set; }
    public bool Help { get; private set; }
    public override string ToString() => "LaunchOptions [redacted]";
    public const string Usage = """
        RobloxAccountManager.exe ["master password"]
        RobloxAccountManager.exe --password "master password"
        RobloxAccountManager.exe --data-dir "D:\Manager\Data" ["master password"]

        Quote passwords and paths containing spaces.
        The password unlocks an existing master-password vault.
        Incorrect passwords return to the normal unlock screen.
        First-run setup and legacy conversion still use the setup screen.

        Command-line passwords can appear in process listings and shell history.
        Update restarts use a short-lived, Windows-protected one-use unlock token.
        """;

    public static LaunchOptions Parse(string[] args)
    {
        var result = new LaunchOptions();
        bool positional = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (!positional && arg == "--") { positional = true; continue; }
                if (!positional && arg is "--help" or "-h") { result.Help = true; continue; }
                if (!positional && arg is "--data-dir" or "--password" or "--unlock-token")
                {
                    if (++i >= args.Length || args[i].Length == 0) throw Invalid();
                    if (arg == "--data-dir")
                    {
                        if (result.DataDirectory is not null) throw Invalid();
                        result.DataDirectory = Path.GetFullPath(args[i]);
                    }
                    else if (arg == "--unlock-token")
                    {
                        if (result.UnlockToken is not null) throw Invalid();
                        result.UnlockToken = Path.GetFullPath(args[i]);
                    }
                    else { result.SetPassword(args[i]); args[i] = ""; }
                }
                else
                {
                    if (!positional && arg.StartsWith("--", StringComparison.Ordinal)) throw Invalid();
                    result.SetPassword(arg); args[i] = "";
                }
            }
            if (result.Password is not null && result.UnlockToken is not null || result.Help && (result.Password is not null || result.UnlockToken is not null || result.DataDirectory is not null)) throw Invalid();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private void SetPassword(string value)
    {
        if (Password is not null || value.Length is < 1 or > 1024 || value.Contains('\0')) throw Invalid();
        Password = value;
    }

    public AppState? TryUnlock(Vault vault, string appRoot, out string feedback)
    {
        feedback = "";
        try
        {
            if (UnlockToken is not null) return vault.UnlockUpdateToken(appRoot, UnlockToken);
            if (Password is null || !vault.Exists || vault.IsPasswordless || !vault.IsPortable) return null;
            return vault.Unlock(Password);
        }
        catch (Exception error) when (error is UserError or CryptographicException or IOException or UnauthorizedAccessException)
        {
            vault.Dispose();
            feedback = UnlockToken is not null ? "The update unlock token could not be used. Enter your master password below." :
                "The command-line master password could not unlock this vault. Enter it below.";
            return null;
        }
        finally { Password = null; }
    }

    private static UserError Invalid() => new("Invalid launch arguments. Use --help for supported options. No argument values were logged.");
    public void Dispose() { Password = null; UnlockToken = null; }
}
