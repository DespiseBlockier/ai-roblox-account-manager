using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace LocalAccountManager;

public sealed partial class AccountBrowser
{
    public bool IsCodeRunning { get; private set; }

    public async Task<BrowserCodeResult> RunCode(BrowserCodeRequest request, Action<string> progress, CancellationToken cancellationToken)
    {
        request = request.Validate();
        if (disposed || loginMode || IsCodeRunning) throw new UserError("Finish the current browser operation before running code.");
        IsCodeRunning = true;
        using var deadline = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, closedCancellation.Token);
        CancellationToken token = linked.Token;
        string key = BrowserCodeRuntime.NewKey();
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
            Title = "Browser code · @" + account.Username;
            Report("Loading the requested Roblox page…");
            await LoadCodePage(request.Url, token);
            Report("Verifying the selected account before running code…");
            await VerifyCodeIdentity(token);
            string installed = await web.CoreWebView2.ExecuteScriptAsync(BrowserCodeRuntime.Install(key, account, request.Code)).WaitAsync(TimeSpan.FromSeconds(5), token);
            using (var json = JsonDocument.Parse(installed))
            {
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("installed", out var ok) || ok.ValueKind != JsonValueKind.True)
                    throw new UserError("The code could not start. Check its JavaScript syntax and starting page.");
            }
            DateTimeOffset nextMembershipCheck = DateTimeOffset.MinValue;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!Security.IsBrowserPage(web.CoreWebView2.Source) || LoginAutofill.IsLoginPage(web.CoreWebView2.Source))
                    throw new UserError("The browser left the signed-in Roblox page. The queue stopped.");
                string raw = await web.CoreWebView2.ExecuteScriptAsync(BrowserCodeRuntime.Read(key)).WaitAsync(TimeSpan.FromSeconds(5), token);
                var state = BrowserCodeRuntime.Parse(raw);
                if (state.Phase != "done" || request.ConfirmGroupId is null) Report(state.Message);
                if (state.Phase == "stopped") return new(BrowserCodeOutcome.Stopped, state.Message);
                if (state.Phase == "failed") return new(BrowserCodeOutcome.Failed, state.Message);
                if (state.Phase == "done" && DateTimeOffset.UtcNow >= nextMembershipCheck)
                {
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
