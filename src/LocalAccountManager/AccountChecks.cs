namespace LocalAccountManager;

internal sealed class AccountChecks(
    RobloxApi api,
    Func<Account, Task<string>> readCookie,
    Func<Account, RobloxIdentity, string, Task> onIdentity,
    Func<Account, bool>? isOnModerationPage = null)
{
    public async Task<string> VerifiedCookie(Account account) => await VerifyCookie(account, await ReadCookieForCheck(account));

    private async Task<string> ReadCookieForCheck(Account account)
    {
        try { return await readCookie(account); }
        catch (Exception ex) { SessionHealth.Failure(account, ex); throw; }
    }

    private async Task<string> VerifyCookie(Account account, string cookie)
    {
        try
        {
            var identity = await api.Identity(cookie);
            if (account.UserId == 0 || identity.Id != account.UserId)
            { account.SessionStatus = "Wrong account · sign in"; throw new UserError("This session belongs to a different account. Click Sign in to reconnect the selected account."); }
            await onIdentity(account, identity, cookie);
            account.SessionStatus = "Valid session"; account.SessionCheckedAt = DateTimeOffset.UtcNow;
            return cookie;
        }
        catch (Exception ex) { if (!account.SessionStatus.StartsWith("Wrong account")) SessionHealth.Failure(account, ex); throw; }
    }

    public async Task CheckHealth(Account account)
    {
        string cookie;
        try { cookie = await ReadCookieForCheck(account); }
        catch (Exception ex) { Moderation.Failure(account, ex); AgeGroups.Failure(account, ex); RobuxBalances.Failure(account, ex); throw; }
        RobloxApiError? restrictedIdentity = null;
        try { await VerifyCookie(account, cookie); }
        // Moderation can remain readable when a restriction blocks the normal identity endpoint.
        catch (RobloxApiError ex) when (ex.StatusCode is 401 or 403) { restrictedIdentity = ex; }
        catch (Exception ex) { Moderation.Failure(account, ex); AgeGroups.Failure(account, ex); RobuxBalances.Failure(account, ex); throw; }
        Exception? moderationError = null;
        try
        {
            await CheckModeration(account, cookie, restrictedIdentity is not null);
        }
        catch (Exception ex)
        {
            Moderation.Failure(account, ex);
            if (ex is RobloxApiError { StatusCode: 429 }) { AgeGroups.Failure(account, ex); RobuxBalances.Failure(account, ex); throw; }
            moderationError = ex;
        }
        // Never attach age information from a session whose identity could not be verified.
        if (restrictedIdentity is not null) { AgeGroups.Failure(account, restrictedIdentity); RobuxBalances.Failure(account, restrictedIdentity); }
        else
        {
            try { AgeGroups.Apply(account, await api.AgeGroup(cookie)); }
            catch (Exception ex)
            {
                AgeGroups.Failure(account, ex);
                if (ex is RobloxApiError { StatusCode: 429 }) { RobuxBalances.Failure(account, ex); throw; }
            }
            try { RobuxBalances.Apply(account, await api.RobuxBalance(cookie)); }
            catch (Exception ex)
            {
                RobuxBalances.Failure(account, ex);
                if (ex is RobloxApiError { StatusCode: 429 }) throw;
            }
        }
        if (moderationError is not null) throw moderationError;
    }

    private async Task CheckModeration(Account account, string cookie, bool identityBlocked)
    {
        long userId = account.UserId;
        string savedCookie = account.SessionCookie;
        if (identityBlocked && (userId <= 0 || cookie != savedCookie))
            throw new UserError("Sign in to the saved account before checking its moderation status.");

        var restriction = await api.Restriction(cookie, userId);
        if (account.UserId != userId || account.SessionCookie != savedCookie)
            throw new UserError("The saved session changed during the moderation check. Try again.");

        bool keepBrowserWarning = restriction is null && account.Restriction is { IsBrowserWarning: true } &&
            (identityBlocked || isOnModerationPage?.Invoke(account) == true);
        if (restriction is null && identityBlocked && account.Restriction is { IsPermanentBan: true })
            account.ModerationCheckStatus = "Using the last confirmed permanent ban";
        else if (keepBrowserWarning) account.ModerationCheckStatus = "Using the last warning seen in the account browser";
        else Moderation.Apply(account, restriction);
        if (account.HasRestriction && identityBlocked) account.SessionStatus = "Restricted · open browser";
    }

    public async Task<string> CheckGameAccess(Account account, long placeId)
    {
        string cookie = await ReadCookieForCheck(account);
        try { await VerifyCookie(account, cookie); }
        catch (RobloxApiError ex) when (ex.StatusCode is 401 or 403)
        {
            try { await CheckModeration(account, cookie, identityBlocked: true); }
            catch (Exception error) { Moderation.Failure(account, error); throw; }
            return account.Restriction is { } restriction
                ? restriction.Label(DateTimeOffset.UtcNow) + " · open the account browser to review Roblox's notice."
                : Security.Friendly(ex);
        }
        return await api.CheckGameAccess(placeId, account.UserId, cookie);
    }
}
