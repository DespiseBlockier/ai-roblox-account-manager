using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace LocalAccountManager;

public sealed partial class AccountBrowser
{
    public bool IsCodeRunning { get; private set; }

    public async Task<BrowserCodeResult> RunCode(BrowserCodeRequest request, Action<string> progress, CancellationToken cancellationToken)
    {
        request = (request with { Guest = IsGuest }).Validate();
        if (disposed || loginMode || IsCodeRunning) throw new UserError("Finish the current browser operation before running code.");
        IsCodeRunning = true;
        using var deadline = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, closedCancellation.Token);
        CancellationToken token = linked.Token;
        string key = BrowserCodeRuntime.NewKey();
        using var solvers = solverFactory();
        var solverJobs = new List<Task>();
        var solverIds = new HashSet<int>();
        Task? accountSave = null;
        Task? signupJob = null;
        bool signupFailed = false;
        var signupIds = new HashSet<int>();
        var accountRequestIds = new HashSet<int>();
        async Task AddCurrentAccount(AddAccountRequest call)
        {
            object reply;
            try
            {
                if (onAddAccount is null) throw new UserError("This browser is not connected to an account manager.");
                BrowserAccountStore.ValidatePassword(call.Password);
                string cookie = await ReadSession().WaitAsync(token);
                var identity = await api.Identity(cookie).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (disposed || !Security.IsBrowserPage(web.CoreWebView2.Source) || await ReadSession().WaitAsync(token) != cookie)
                    throw new UserError("The browser session changed. Try adding the current account again.");
                if (!IsGuest && identity.Id != account.UserId) throw new UserError("This browser no longer matches its selected account. Use a guest browser to add a different account.");
                var saved = await onAddAccount(identity, cookie, call.Password, token);
                AccountAdded?.Invoke(saved);
                reply = new { success = true, userId = saved.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), username = saved.Username };
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { reply = new { success = false, error = Security.Friendly(ex) }; }
            finally { call.Password = null; }
            if (!disposed && !token.IsCancellationRequested)
                try { await web.CoreWebView2.ExecuteScriptAsync(BrowserCodeRuntime.ReplyAccount(key, call.Id, reply)); } catch { }
        }
        async Task CreateAccount(SignupRequest call)
        {
            object reply;
            try
            {
                if (!IsGuest) throw new UserError("Open the editor with no account selected to create an account.");
                call.Validate();
                if (onAddAccount is null) throw new UserError("This browser is not connected to an account manager.");
                reply = await CreateAccountInBrowser(call, solvers, message => { if (!disposed && !token.IsCancellationRequested) { status.Text = message; progress(message); } }, token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { signupFailed = true; reply = new { success = false, error = Security.Friendly(ex) }; }
            finally { call.ClearSecrets(); }
            if (!disposed && !token.IsCancellationRequested)
                try { await web.CoreWebView2.ExecuteScriptAsync(BrowserCodeRuntime.ReplySignup(key, call.Id, reply)); } catch { }
        }
        async Task Solve(SolverRequest call)
        {
            try
            {
                object result = await solvers.Execute(call, token);
                if (!disposed && !token.IsCancellationRequested) await web.CoreWebView2.ExecuteScriptAsync(BrowserCodeRuntime.Reply(key, call.Id, result));
            }
            catch (OperationCanceledException) { }
            catch { /* Navigation or close disposes the page and its pending callbacks. */ }
        }
        string lastMessage = "";
        void Report(string message)
        {
            if (message == lastMessage || disposed) return;
            lastMessage = message; status.Text = message; progress(message);
        }
        try
        {
            await Ready.WaitAsync(token);
            ShowForUser();
            Title = IsGuest ? "Browser code · Guest" : "Browser code · @" + account.Username;
            Report("Loading the requested Roblox page…");
            await LoadCodePage(request.Url, token);
            if (!IsGuest) { Report("Verifying the selected account before running code…"); await VerifyCodeIdentity(token); }
            string installed = await web.CoreWebView2.ExecuteScriptAsync(BrowserCodeRuntime.Install(key, account, request.Code, IsGuest)).WaitAsync(TimeSpan.FromSeconds(5), token);
            using (var json = JsonDocument.Parse(installed))
            {
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("installed", out var ok) || ok.ValueKind != JsonValueKind.True)
                    throw new UserError("The code could not start. Check its JavaScript syntax and starting page.");
            }
            DateTimeOffset nextMembershipCheck = DateTimeOffset.MinValue;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!Security.IsBrowserPage(web.CoreWebView2.Source) || !IsGuest && LoginAutofill.IsLoginPage(web.CoreWebView2.Source))
                    throw new UserError("The browser left the signed-in Roblox page. The queue stopped.");
                string raw = await web.CoreWebView2.ExecuteScriptAsync(BrowserCodeRuntime.Read(key)).WaitAsync(TimeSpan.FromSeconds(5), token);
                var state = BrowserCodeRuntime.Parse(raw);
                if (state.Accounts is { Count: > 0 } additions)
                {
                    var call = additions[0];
                    if (signupActive || signupJob is { IsCompleted: false } || accountSave is { IsCompleted: false } || !accountRequestIds.Add(call.Id)) throw new UserError("Invalid concurrent account save.");
                    accountSave = AddCurrentAccount(call);
                }
                if (state.Signups is { Count: > 0 } signups)
                {
                    if (signupFailed || signupJob is { IsCompleted: false } || accountSave is { IsCompleted: false } || !signupIds.Add(signups[0].Id)) throw new UserError("Signups must run sequentially and stop after a failed attempt.");
                    signupJob = CreateAccount(signups[0]);
                }
                solverJobs.RemoveAll(job => job.IsCompleted);
                if (state.Requests is { Count: > 0 } calls)
                {
                    foreach (var call in calls)
                    {
                        if (solverJobs.Count >= 4 || !solverIds.Add(call.Id)) throw new UserError("Invalid or excessive solver requests. The queue stopped.");
                        solverJobs.Add(Solve(call));
                    }
                }
                if (!signupActive && (state.Phase != "done" || request.ConfirmGroupId is null)) Report(state.Message);
                if (state.Phase == "stopped") return new(BrowserCodeOutcome.Stopped, state.Message);
                if (state.Phase == "failed") return new(BrowserCodeOutcome.Failed, state.Message);
                if (state.Phase == "done" && DateTimeOffset.UtcNow >= nextMembershipCheck)
                {
                    if (IsGuest) return new(BrowserCodeOutcome.Completed, "Guest code completed.");
                    string cookie = await VerifyCodeIdentity(token);
                    if (request.ConfirmGroupId is long group && !await api.IsGroupMember(cookie, account.UserId, group).WaitAsync(token))
                    {
                        nextMembershipCheck = DateTimeOffset.UtcNow.AddSeconds(5);
                        Report("Waiting for Roblox to confirm actual group membership. A pending request does not complete this account.");
                    }
                    else return new(BrowserCodeOutcome.Completed, request.ConfirmGroupId is null ? "Code completed." : "Group membership independently confirmed.");
                }
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException)
        {
            return deadline.IsCancellationRequested
                ? new(BrowserCodeOutcome.Failed, "The account timed out. The queue stopped without marking it completed.")
                : new(BrowserCodeOutcome.Stopped, "Browser closed or Stop requested. Remaining accounts were not run.");
        }
        catch (TimeoutException) { return new(BrowserCodeOutcome.Failed, "Browser code stopped responding. The browser was closed and the queue stopped."); }
        catch (Exception ex)
        {
            if (ex is RobloxApiError or MissingSessionError) SessionHealth.Failure(account, ex);
            return new(BrowserCodeOutcome.Failed, Security.Friendly(ex));
        }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(solverJobs.Concat(new[] { accountSave, signupJob }.OfType<Task>())).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            signupActive = signupControlsBrowser = false;
            IsCodeRunning = false;
            if (!disposed)
            {
                try { await web.CoreWebView2.ExecuteScriptAsync(BrowserCodeRuntime.Stop(key)).WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
                if (!disposed) Close(); // Disposes the document, even if custom code ignores the abort signal.
            }
        }
    }

    private async Task<string> VerifyCodeIdentity(CancellationToken token)
    {
        string cookie = await ReadSession().WaitAsync(token);
        var identity = await api.Identity(cookie).WaitAsync(token);
        if (account.UserId <= 0 || identity.Id != account.UserId)
        {
            account.SessionStatus = "Wrong account · sign in";
            throw new UserError("The browser identity does not match the selected account. The queue stopped.");
        }
        account.SessionStatus = "Valid session"; account.SessionCheckedAt = DateTimeOffset.UtcNow;
        return cookie;
    }

    private async Task LoadCodePage(string url, CancellationToken token)
    {
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigation = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs e) { navigation ??= e.NavigationId; }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        { if (navigation == e.NavigationId) loaded.TrySetResult(e.IsSuccess); }
        var core = web.CoreWebView2;
        core.NavigationStarting += Starting; core.NavigationCompleted += Completed;
        try
        {
            core.Navigate(url);
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), token)) throw new UserError("The Roblox page could not load. The queue stopped before running code.");
        }
        finally { core.NavigationStarting -= Starting; core.NavigationCompleted -= Completed; }
    }
}
