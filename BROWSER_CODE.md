# Open browser with code

Select accounts using Ctrl/Shift, click **Open browser with code…**, then review the selected-account list. The editor runs browser **JavaScript**, not in-game Luau. Code is kept in the editor's memory and is not automatically saved or run on startup.

Set a Roblox starting URL, enter your code, choose a timeout per account, and click **Run on selected accounts**. Accounts run sequentially with a short gap between them. Every account is checked against its actual browser session before code runs. Expired or mismatched sessions stop the queue; use the manager's Sign in action and rerun after reconnecting.

| Helper | Behavior |
| --- | --- |
| `ram.done()` | Finish the current account, close its browser, and move to the next selected account. Put it at the end of your work. |
| `ram.stop('reason')` | Close the current browser and stop the entire queue. Remaining accounts are not run. |
| `ram.status('message')` | Show a local status message in the browser and queue window. Messages are not saved as log files. |
| `await ram.sleep(1000)` | Wait in milliseconds; cooperates with Stop. |
| `ram.signal` | Abort signal to pass to `fetch`, for example `fetch(url, {signal: ram.signal})`. |
| `ram.account.username` | Selected Roblox username. |
| `ram.account.userId` | Selected Roblox user ID, represented as a string. |

Returning from code alone does not advance the queue. Call `ram.done()`. Exceptions, timeouts, page reloads that lose the script, closing the browser, or clicking Stop end the queue without marking that account completed. Code is not automatically replayed after navigation. Stop closes the browser document but cannot undo requests already processed by Roblox.

```js
ram.status('Working as @' + ram.account.username);
// Your browser JavaScript goes here.
await ram.sleep(1000);
ram.done();
```

## Join a group/community

1. Enter your group/community ID or Roblox community link in the generator field.
2. Click **Generate join-group example**. This fills the starting URL, generates the JavaScript with your ID, and enables **Confirm actual membership**.
3. Review the code and selected accounts, then click **Run on selected accounts**.
4. The example checks whether each account is already a member. Otherwise it sends the normal Roblox join request, with at most one ordinary CSRF-token retry.
5. If verification or manual approval is required, complete it through Roblox. The example waits for actual membership; a successful request or pending approval does not count as joining. You can increase the per-account timeout up to 120 minutes when waiting for approval.
6. After the script calls `ram.done()`, the manager independently verifies the account identity and membership through Roblox before closing that browser and advancing.

The example stops on rate limiting and does not solve CAPTCHA or bypass verification. If a page reload ends the run, finish any verification and rerun: already-member accounts do not send another join request. Changing the generator's group ID requires regenerating the example before running.

The example source is [examples/join-group.js](examples/join-group.js). Replace the clearly marked sample ID when editing this standalone file, or use the editor's generator. This feature joins a community; it does not make Robux payouts or establish payout eligibility. [Roblox's joining instructions](https://en.help.roblox.com/hc/en-us/articles/203313490-Finding-and-Joining-a-Community) describe approval requirements; the [official group API reference](https://create.roblox.com/docs/cloud/reference/features/groups) documents the join endpoint.

## Code permissions

Custom code runs with the selected website session's Roblox permissions. Untrusted code could perform account actions or send page-accessible data to other services. Browser JavaScript is not a safe sandbox for arbitrary pasted code. The manager does not pass saved passwords or session-cookie values to the script helper and does not enable native host objects, web-message bridges, shell access, remote code downloads, or a control server. Browser-origin and normal TLS protections remain in place. Code can still issue web requests under normal browser rules, so review it before running it.

The generated example is compiled into the app and only joins the requested community and checks identity/membership. Its integration tests use synthetic cookies and intercepted test HTML/API responses; real Roblox group joining still needs a live account test.
