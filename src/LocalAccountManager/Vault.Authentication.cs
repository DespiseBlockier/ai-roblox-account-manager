using System.Security.Cryptography;
using System.Text.Json;

namespace LocalAccountManager;

public sealed partial class Vault
{
    private sealed record PendingUnlock(byte[] Key, byte[] Salt, AppState State, byte[] FileHash, DateTimeOffset Created)
    { public int Attempts { get; set; } }
    private PendingUnlock? pendingUnlock;
    private string? activeAuthentication;
    public bool HasPendingSecondFactor => pendingUnlock is not null;
    private void RememberAuthentication(AppState state) => activeAuthentication = JsonSerializer.Serialize(state.Authentication);
    private void CheckAuthenticationUnchanged(AppState state)
    {
        if (activeAuthentication != JsonSerializer.Serialize(state.Authentication))
            throw new UserError("Change authenticator settings through Vault security.");
    }
    private void ClearPendingUnlock()
    {
        if (pendingUnlock is { } pending) CryptographicOperations.ZeroMemory(pending.Key);
        pendingUnlock = null;
    }
    public AppState CompleteSecondFactor(string code)
    {
        var pending = pendingUnlock ?? throw new UserError("Enter the master password again.");
        if (DateTimeOffset.UtcNow - pending.Created > TimeSpan.FromMinutes(2) || pending.Attempts >= 5 ||
            !CryptographicOperations.FixedTimeEquals(pending.FileHash, SHA256.HashData(ReadBounded(FilePath))))
        { ClearPendingUnlock(); throw new UserError("The unlock attempt expired or the vault changed. Enter the master password again."); }
        pending.Attempts++;
        VaultAuthentication accepted;
        try { accepted = AcceptFactor(pending.State.Authentication, code); }
        catch
        {
            if (pending.Attempts >= 5) ClearPendingUnlock();
            throw;
        }
        var previous = pending.State.Authentication;
        pending.State.Authentication = accepted;
        try { WriteAuthenticationSnapshots(pending.State, pending.Key, pending.Salt); }
        catch { pending.State.Authentication = previous; throw; }
        masterKey = pending.Key; vaultSalt = pending.Salt; pendingUnlock = null; RememberAuthentication(pending.State);
        return pending.State;
    }
    private static VaultAuthentication AcceptFactor(VaultAuthentication config, string code)
    {
        var next = Authenticator.Copy(config);
        long step = Authenticator.Verify(config.Secret, code, config.LastAcceptedStep, DateTimeOffset.UtcNow);
        if (step >= 0) { next.LastAcceptedStep = step; return next; }
        string trimmed = code.Trim().Replace("-", "");
        if (trimmed.Length == 32 && trimmed.All(char.IsAsciiHexDigit))
        {
            byte[] hash = Convert.FromHexString(Authenticator.RecoveryHash(config.Id, code));
            int index = config.RecoveryHashes.FindIndex(h => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(h), hash));
            if (index >= 0) { next.RecoveryHashes.RemoveAt(index); return next; }
        }
        throw new UserError("The code is invalid or already used. Try a fresh authenticator code or an unused recovery code. Check your device's clock.");
    }
    public List<string> EnableAuthenticator(AppState state, string currentPassword, string secret, string code)
    {
        RequireUnlocked(); CheckAuthenticationUnchanged(state);
        if (unprotected) throw new UserError("Enable a master password before setting up authenticator 2FA.");
        if (state.Authentication.Enabled) throw new UserError("Authenticator 2FA is already enabled.");
        long step = Authenticator.Verify(secret, code, -1, DateTimeOffset.UtcNow);
        if (step < 0) throw new UserError("Enter the current six-digit code from your authenticator app to confirm setup.");
        var codes = Authenticator.NewRecoveryCodes(); var id = Guid.NewGuid();
        RekeyAuthentication(state, currentPassword, new VaultAuthentication
        { Enabled = true, Id = id, Secret = secret, LastAcceptedStep = step, RecoveryHashes = codes.Select(c => Authenticator.RecoveryHash(id, c)).ToList() });
        return codes;
    }
    public List<string> RegenerateRecoveryCodes(AppState state, string currentPassword, string code)
    {
        RequireUnlocked(); CheckAuthenticationUnchanged(state);
        if (!state.Authentication.Enabled) throw new UserError("Set up authenticator 2FA first.");
        var next = AcceptFactor(state.Authentication, code); var codes = Authenticator.NewRecoveryCodes(); next.Id = Guid.NewGuid();
        next.RecoveryHashes = codes.Select(c => Authenticator.RecoveryHash(next.Id, c)).ToList();
        RekeyAuthentication(state, currentPassword, next); return codes;
    }
    public void DisableAuthenticator(AppState state, string currentPassword, string code)
    {
        RequireUnlocked(); CheckAuthenticationUnchanged(state);
        if (!state.Authentication.Enabled) throw new UserError("Authenticator 2FA is already disabled.");
        _ = AcceptFactor(state.Authentication, code);
        RekeyAuthentication(state, currentPassword, new VaultAuthentication());
    }
    private void RekeyAuthentication(AppState state, string password, VaultAuthentication next)
    {
        Validate(state); Authenticator.Validate(next);
        byte[] check = Rfc2898DeriveBytes.Pbkdf2(password, vaultSalt!, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(check, masterKey!)) throw new UserError("The current master password is incorrect.");
            byte[] stored = ReadBounded(FilePath);
            if (!HasPortableHeader(stored) || !stored.AsSpan(8, 16).SequenceEqual(vaultSalt)) throw new UserError("The vault changed. Reopen it first.");
        }
        finally { CryptographicOperations.ZeroMemory(check); }
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var previous = state.Authentication; state.Authentication = next;
        try
        {
            WriteAuthenticationSnapshots(state, key, salt);
            CryptographicOperations.ZeroMemory(masterKey!); masterKey = key; key = null!; vaultSalt = salt; RememberAuthentication(state);
        }
        catch { state.Authentication = previous; throw; }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    private void WriteAuthenticationSnapshots(AppState state, byte[] key, byte[] salt)
    {
        Validate(state);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(state);
        try
        {
            if (clear.Length > MaxBytes) throw new UserError("The account database is too large.");
            byte[] stored = Encode(clear, key, salt, state.Authentication.Enabled);
            try { WriteAtomic(FilePath + ".previous", stored); WriteAtomic(FilePath, stored); }
            finally { CryptographicOperations.ZeroMemory(stored); }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
