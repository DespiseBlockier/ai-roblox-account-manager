using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAccountManager;

try { await RunChecks(); return 0; }
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

static async Task RunChecks()
{
int passed = 0;
void Assert(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name)
{
    bool rejected = false;
    try { action(); } catch (Exception ex) when (ex is UserError or CryptographicException) { rejected = true; }
    Assert(rejected, name);
}
Assert(GameTarget.Parse("123").PlaceId == 123, "numeric place IDs");
Assert(GameTarget.Parse("https://www.roblox.com/games/123/My-Game").PlaceId == 123, "Roblox game links");
Assert(GameTarget.Parse("https://www.roblox.com/games/123?privateServerLinkCode=abc_DEF-12").PrivateUrl is not null, "private server links");
Assert(GameTarget.Parse("https://www.roblox.com/share?code=abcdef&type=Server").PlaceId is null, "server share links");
foreach (string invalid in new[] { "0", "-1", "1e10", "9223372036854775808", "file:///C:/Windows/system32/cmd.exe", "http://www.roblox.com/games/1", "https://roblox.com.evil.test/games/1", "https://evil.test/?https://roblox.com/games/1", "https://roblox.com@evil.test/games/1", "https://name@www.roblox.com/games/1", "https://www.roblox.com:444/games/1", "javascript:alert(1)", "roblox-player:evil", "https://www.roblox.com/users/1", "https://www.roblox.com/games/1?privateServerLinkCode=x&privateServerLinkCode=y", "https://www.roblox.com/games/1?privateServerLinkCode=%0D%0Abad", "https://www.roblox.com/share?code=x&type=Game" })
    Reject(() => GameTarget.Parse(invalid), "reject untrusted target " + invalid.Split('?')[0]);
Assert(GameTarget.ValidateJob("11111111-2222-3333-4444-555555555555") == "11111111-2222-3333-4444-555555555555", "valid Job ID");
Reject(() => GameTarget.ValidateJob("x&placeId=99"), "Job ID query injection blocked");
Assert(!Security.IsApiUrl(new Uri("https://auth.roblox.com.evil.test/")), "API host boundary");
Assert(!Security.IsApiUrl(new Uri("http://auth.roblox.com/")), "API HTTPS enforced");
Assert(!Security.IsApiUrl(new Uri("https://auth.roblox.com:4430/")), "API port boundary");
Assert(!Security.IsBrowserPage("https://roblox.com.evil.test"), "browser top-level host boundary");
Assert(Security.CanLaunchFromBrowser("roblox-player:1", "https://www.roblox.com", "https://www.roblox.com/games/123"), "Roblox-origin player handoff allowed");
Assert(!Security.CanLaunchFromBrowser("file:///C:/evil.exe", "https://www.roblox.com", "https://www.roblox.com"), "other browser protocols blocked");
Assert(!Security.CanLaunchFromBrowser("roblox-player:1", "https://evil.test", "https://www.roblox.com"), "third-party frame protocol handoff blocked");
Assert(!Security.CanLaunchFromBrowser("roblox-player:1", "https://www.roblox.com", "https://evil.test"), "non-Roblox top-level protocol handoff blocked");
Assert(!Security.CanLaunchFromBrowser("roblox-player:1", "", "https://www.roblox.com"), "missing protocol origin blocked");
Assert(LoginAutofill.IsLoginPage("https://www.roblox.com/login?returnUrl=home"), "autofill only on official login path");
Assert(GroupTarget.Parse("42") == 42 && GroupTarget.Parse("https://www.roblox.com/communities/42/Test") == 42 && GroupTarget.Parse("https://www.roblox.com/groups/42/Test") == 42, "group ID and both Roblox group-link formats");
foreach (string invalid in new[] { "0", "-2", "9007199254740992", "https://www.roblox.com.evil.test/groups/42", "http://www.roblox.com/groups/42", "https://www.roblox.com/users/42", "https://user@www.roblox.com/groups/42" })
    Reject(() => GroupTarget.Parse(invalid), "invalid group target rejected");
Reject(() => new BrowserCodeRequest("https://evil.test/", "ram.done();", TimeSpan.FromSeconds(5)).Validate(), "code start URL confined to Roblox");
Reject(() => new BrowserCodeRequest("https://www.roblox.com/login", "ram.done();", TimeSpan.FromSeconds(5)).Validate(), "custom code cannot start on login page");
Reject(() => new BrowserCodeRequest("https://www.roblox.com/home", "", TimeSpan.FromSeconds(5)).Validate(), "empty browser code rejected");
Reject(() => new BrowserCodeRequest("https://www.roblox.com/home", "ram.done();", TimeSpan.FromHours(3)).Validate(), "browser-code timeout bounded");
string runKey = BrowserCodeRuntime.NewKey();
var scriptAccount = new Account { UserId = 42, Username = "fixture_user", SavedPassword = "do-not-expose-password", SessionCookie = "do-not-expose-cookie" };
string wrapper = BrowserCodeRuntime.Install(runKey, scriptAccount, "ram.done();");
Assert(!wrapper.Contains(scriptAccount.SavedPassword) && !wrapper.Contains(scriptAccount.SessionCookie) && wrapper.Contains("userId") && wrapper.Contains("fixture_user"), "script helper receives identity but never saved credentials");
Reject(() => BrowserCodeRuntime.Read("bad-key');alert(1)"), "run key cannot inject script syntax");
Assert(BrowserCodeRuntime.Parse("{\"phase\":\"done\",\"message\":\"complete\"}").Phase == "done", "browser completion status parsed");
Reject(() => BrowserCodeRuntime.Parse("null"), "navigation loss stops code instead of replaying it");
Reject(() => BrowserCodeRuntime.Parse("{\"phase\":\"execute-shell\",\"message\":\"bad\"}"), "unsupported browser status rejected");
string groupExample = BrowserCodeRuntime.JoinGroupExample(42);
Assert(groupExample.Contains("const groupId = 42;") && groupExample.Contains("ram.done()") && groupExample.Contains("while (!await isMember())"), "group example waits for membership before completion");
var codeQueue = new BrowserCodeQueue();
int codeInvocations = 0;
var codeResults = await codeQueue.Run([scriptAccount, new Account(), new Account()], (_, _) => Task.FromResult(new BrowserCodeResult(++codeInvocations == 1 ? BrowserCodeOutcome.Completed : BrowserCodeOutcome.Stopped, "test")), CancellationToken.None);
Assert(codeInvocations == 2 && codeResults.Count == 2 && !codeQueue.IsRunning, "browser stop result leaves remaining accounts untouched");
using (var canceledCode = new CancellationTokenSource())
{
    canceledCode.Cancel(); bool canceled = false;
    try { await codeQueue.Run([scriptAccount], (_, _) => throw new Exception("Must not run"), canceledCode.Token); } catch (OperationCanceledException) { canceled = true; }
    Assert(canceled && !codeQueue.IsRunning, "canceled browser queue executes no accounts");
}
foreach (string url in new[] { "https://www.roblox.com/home", "https://www.roblox.com/login/extra", "https://roblox.com/login", "http://www.roblox.com/login", "https://www.roblox.com.evil.test/login", "https://www.roblox.com:444/login", "about:blank" })
    Assert(!LoginAutofill.IsLoginPage(url), "autofill rejects " + url);
var credentials = LoginInput.Parse("test_one;one;two  \r\n\ntest_two; pass two ", groupColumn: false);
Assert(credentials.Count == 2 && credentials[0].Password == "one;two  " && credentials[1].Password == " pass two ", "bulk parser preserves password delimiters and spaces");
Assert(!credentials[0].ToString().Contains("one;two"), "credential diagnostics redacted");
foreach (var c in credentials) c.Dispose();
Assert(credentials.All(c => c.Password.Length == 0), "queue credential references cleared");
var grouped = LoginInput.Parse("test_group;\"pass;word\";Farm\r\ntest_quote;\"pa\"\"ss;word\";\"Farm;A\"\nplain_user;pw");
Assert(grouped[0].Password == "pass;word" && grouped[0].Group == "Farm", "three-column group import with quoted semicolon password");
Assert(grouped[1].Password == "pa\"ss;word" && grouped[1].Group == "Farm;A" && grouped[2].Group is null, "quoted quotes, group delimiters, and optional group supported");
Assert(LoginInput.Create("single_user", "semi;password", " Alts ").Group == "Alts", "single import stores group without parsing its password");
Reject(() => LoginInput.Parse("test_one;pw;A;B"), "unquoted extra columns rejected");
Reject(() => LoginInput.Parse("test_one;\"pw;A"), "unterminated quoted password rejected");
Reject(() => LoginInput.Create("test_one", "pw", new string('x', 81)), "overlong import group rejected");
foreach (string invalid in new[] { "", "test_one", "test_one;", "test_one;x\nTEST_ONE;y", "bad user;x", "test_one;x\rsecret" })
    Reject(() => LoginInput.Parse(invalid), "invalid credential list rejected without echoing input");
var enrollState = new AppState(); int enrollmentSaves = 0;
var enrollment = new AccountEnrollment(enrollState, () => enrollmentSaves++);
var pending = enrollment.Begin();
Assert(enrollState.Accounts.Count == 0 && enrollmentSaves == 0, "starting a login never saves a placeholder");
Reject(() => enrollment.Begin(), "repeated add cannot create parallel pending rows");
enrollment.Finish();
Assert(enrollState.Accounts.Count == 0, "canceling a login leaves no account row");
using var entered = new LoginCredential("test_one", "secret-test-password", "Imported");
pending = enrollment.Begin(entered);
Reject(() => enrollment.Commit(pending, new(1, "wrong_user", "Wrong"), "fake-cookie"), "queued username mismatch rejected");
Assert(enrollState.Accounts.Count == 0 && pending.SavedPassword.Length == 0, "wrong login does not save credentials");
enrollment.Commit(pending, new(1, "test_one", "Test"), "fake-cookie");
Assert(enrollState.Accounts.Count == 1 && pending.SavedPassword == "secret-test-password" && pending.SessionCookie == "fake-cookie", "verified enrollment saves password and cookie together");
Assert(pending.Group == "Imported", "import group is applied only after verified login");
enrollment.Finish();
using var update = new LoginCredential("TEST_ONE", "new-password", "Updated");
Assert(ReferenceEquals(enrollment.Begin(update), pending), "bulk input reuses an existing username");
enrollment.Commit(pending, new(1, "test_one", "Test"), "fake-cookie", saveCredential: false);
Assert(pending.SavedPassword == "secret-test-password", "background session check cannot commit pending password");
Assert(pending.Group == "Imported", "background session check cannot commit pending group");
enrollment.Commit(pending, new(1, "test_one", "Test"), "new-cookie");
Assert(enrollState.Accounts.Count == 1 && pending.SavedPassword == "new-password", "reauth updates existing row without duplication");
Assert(pending.Group == "Updated", "verified reimport updates group on existing account");
enrollment.Finish();
var brokenState = new AppState();
var brokenEnrollment = new AccountEnrollment(brokenState, () => throw new IOException("test failure"));
var unsaved = brokenEnrollment.Begin(entered);
try { brokenEnrollment.Commit(unsaved, new(1, "test_one", "Test"), "fake-cookie"); } catch (IOException) { }
Assert(brokenState.Accounts.Count == 0 && unsaved.SavedPassword.Length == 0 && unsaved.SessionCookie.Length == 0, "failed vault save rolls back pending enrollment");
Assert(unsaved.Group == "Default", "failed enrollment save restores previous group");
var editA = new Account { Username = "edit_one", DisplayName = "One", Group = "Old A", Alias = "First", Notes = "note", SavedPassword = "keep-password", SessionCookie = "keep-cookie" };
var editB = new Account { Username = "edit_two", DisplayName = "Two", Group = "Old B", Alias = "Second" };
var edits = new BulkChanges(Group: "Farm", AliasTemplate: "{index}-{username}", Notes: "added", AppendNotes: true, Favorite: true);
var editPreview = BulkEditing.Preview([editA, editB], edits);
Assert(editA.Group == "Old A" && editPreview[1].Alias == "2-edit_two", "bulk preview expands aliases without mutating accounts");
int editSaves = 0;
BulkEditing.Apply([editA, editB], edits, () => editSaves++);
Assert(editSaves == 1 && editA.Group == "Farm" && editB.Favorite && editA.Notes == "note\nadded", "bulk editing applies group, aliases, notes and favorites in one save");
Assert(editA.SavedPassword == "keep-password" && editA.SessionCookie == "keep-cookie", "bulk metadata editing preserves credentials");
try { BulkEditing.Apply([editA, editB], new(Group: "Rollback"), () => throw new IOException("test")); } catch (IOException) { }
Assert(editA.Group == "Farm" && editB.Group == "Farm", "failed bulk save rolls back every selected account");
BulkEditing.Apply([editA, editB], new(AliasTemplate: ""), () => { });
Assert(editA.Alias == "" && editB.Group == "Farm", "unchecked bulk fields remain unchanged and explicit blank clears alias");
Reject(() => BulkEditing.Preview([editA, editB], new(AliasTemplate: new string('x', 79) + "{username}")), "bulk alias expansion validated before mutation");
enrollState.Accounts.Add(new Account()); enrollState.Accounts.Add(new Account { Alias = "keep custom record" });
Assert(AccountEnrollment.RemoveLegacyPlaceholders(enrollState) == 1 && enrollState.Accounts.Count == 2, "migration removes only old blank placeholders");
var queue = new LoginQueue();
var queued = LoginInput.Parse("test_one;pw1\ntest_two;pw2\ntest_three;pw3");
var visited = new List<int>();
await queue.Run(queued, (entry, index) => { visited.Add(index); return Task.FromResult(index == 0); });
Assert(queue.Completed == 1 && visited.SequenceEqual(new[] { 0, 1 }) && !queue.IsRunning, "closing current login stops queue before later accounts");
Assert(queued.All(c => c.Password.Length == 0), "cancel discards remaining queued password references");
int liveCallbacks = 0; bool ordered = true;
var allQueued = LoginInput.Parse("test_one;pw1\ntest_two;pw2");
await queue.Run(allQueued, async (entry, index) => { liveCallbacks++; ordered &= liveCallbacks == 1; await Task.Delay(1); liveCallbacks--; return true; });
Assert(ordered && queue.Completed == 2, "login queue processes accounts sequentially");
SessionHealth.Failure(pending, new RobloxApiError(401, "expired"));
Assert(pending.SessionStatus.StartsWith("Expired") && pending.SavedPassword == "new-password", "401 marks expired while preserving saved login");
SessionHealth.Failure(pending, new HttpRequestException("offline"));
Assert(pending.SessionStatus.StartsWith("Check failed"), "network failures are not mislabeled expired");
SessionHealth.Failure(pending, new RobloxApiError(403, "verify"));
Assert(pending.SessionStatus == "Verification needed", "verification challenge distinguished from expiration");
string protocol = Security.BuildLaunchUri(123, "", "ticket+launchmode:evil");
Assert(protocol.Contains("gameinfo:ticket%2Blaunchmode%3Aevil") && !protocol.Contains("+launchmode:evil"), "ticket protocol injection escaped");
Assert(Uri.UnescapeDataString(protocol).Contains("request=RequestGame&") && Uri.UnescapeDataString(protocol).Contains("placeId=123"), "constructed launch target");
Reject(() => Security.BuildLaunchUri(0, "", "x"), "invalid launch ID blocked");
Reject(() => Security.BuildLaunchUri(123, "", "x\r\ny"), "launch header injection blocked");

string tempRoot = Path.Combine(Environment.CurrentDirectory, "test-results", "security-" + Guid.NewGuid().ToString("N"));
string copiedRoot = tempRoot + "-copied";
Directory.CreateDirectory(tempRoot);
Directory.CreateDirectory(copiedRoot);
Vault? vault = null;
try
{
    vault = new Vault(tempRoot);
    Reject(() => vault.Save(new AppState()), "locked portable vault cannot save");
    vault.Unlock("portable-test-master-password");
    var state = new AppState { Accounts = [new Account { UserId = 123, Username = "fake_test_user", DisplayName = "Test", Notes = "sensitive-test-note", SavedPassword = "synthetic-secret-password", SessionCookie = "synthetic-cookie" }], Favorites = [new GameBookmark { Name = "Test", Target = "123" }] };
    Assert(vault.Load().Accounts.Count == 0, "missing vault starts empty");
    vault.Save(state);
    byte[] sealedData = File.ReadAllBytes(vault.FilePath);
    Assert(!Encoding.UTF8.GetString(sealedData).Contains("sensitive-test-note"), "portable vault contains no plaintext notes");
    Assert(vault.Load().Accounts.Single().Username == "fake_test_user", "portable encrypted vault round trip");
    Assert(!Encoding.UTF8.GetString(sealedData).Contains("synthetic-secret-password") && !Encoding.UTF8.GetString(sealedData).Contains("synthetic-cookie"), "saved credentials never written in plaintext");
    Assert(vault.Load().Accounts[0].SavedPassword == "synthetic-secret-password" && vault.Load().Accounts[0].SessionCookie == "synthetic-cookie", "saved credentials restored from user-encrypted vault");
    Assert(vault.Load().Version == 2, "credential vault uses schema rejected by the old app");
    string copiedFile = Path.Combine(copiedRoot, "accounts.vault");
    File.Copy(vault.FilePath, copiedFile);
    using (var moved = new Vault(copiedRoot))
    {
        Reject(() => moved.Unlock("wrong-master-password"), "wrong portable master password rejected");
        var movedState = moved.Unlock("portable-test-master-password");
        Assert(movedState.Accounts[0].SavedPassword == "synthetic-secret-password" && movedState.Accounts[0].SessionCookie == "synthetic-cookie", "copied vault unlocks in a different folder with credentials preserved");
        moved.Dispose(); Reject(() => moved.Load(), "disposing vault clears its unlocked key");
    }
    byte[] corruptCopy = File.ReadAllBytes(copiedFile); corruptCopy[^1] ^= 1; File.WriteAllBytes(copiedFile, corruptCopy);
    using (var moved = new Vault(copiedRoot)) Reject(() => moved.Unlock("portable-test-master-password"), "portable vault tampering rejected");
    Assert(File.ReadAllBytes(copiedFile).SequenceEqual(corruptCopy), "wrong password and tampering do not reset portable file");
    Assert(vault.BrowserRoot.StartsWith(vault.Root + Path.DirectorySeparatorChar) && Vault.DefaultRoot(copiedRoot) == Path.Combine(copiedRoot, "Data"), "all new account storage stays inside the portable folder");
    state.Accounts[0].Alias = "new alias"; vault.Save(state);
    Assert(File.Exists(vault.FilePath + ".previous"), "atomic save keeps previous encrypted copy");
    Assert(vault.Load().Accounts[0].Alias == "new alias", "atomic replacement round trip");
    string backup = Path.Combine(tempRoot, "layout.ramlayout");
    Vault.ExportLayout(backup, state, "test-passphrase-123456");
    Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(backup)).Contains("fake_test_user"), "backup contains no plaintext account metadata");
    var restored = Vault.ImportLayout(backup, "test-passphrase-123456");
    Assert(restored.Accounts[0].Notes == "sensitive-test-note", "AES-GCM backup round trip");
    Assert(restored.Accounts[0].SavedPassword.Length == 0 && restored.Accounts[0].SessionCookie.Length == 0, "layout export and import exclude saved passwords and cookies");
    Reject(() => Vault.ImportLayout(backup, "wrong-password"), "wrong backup password rejected");
    byte[] tampered = File.ReadAllBytes(backup); tampered[^1] ^= 0x40; File.WriteAllBytes(backup, tampered);
    Reject(() => Vault.ImportLayout(backup, "test-passphrase-123456"), "backup tampering rejected");
    Reject(() => Vault.ExportLayout(backup, state, "short"), "short backup password rejected");
    state.Accounts.Add(state.Accounts[0]); Reject(() => vault.Save(state), "duplicate profile IDs rejected"); state.Accounts.RemoveAt(1);
    Assert(vault.Load().Accounts.Count == 1, "invalid save preserves previous vault");
    string serialized = JsonSerializer.Serialize(Vault.LayoutOnly(state));
    Assert(!serialized.Contains("SessionStatus") && !serialized.Contains("synthetic-secret-password") && !serialized.Contains("synthetic-cookie") && !serialized.Contains("Ticket"), "layout schema excludes credential values and transient state");
    state.Version = 99; Reject(() => vault.Save(state), "unsupported schema fails closed"); state.Version = 2;
    var legacy = new AppState { Version = 1, Accounts = [new Account { UserId = 777, Username = "legacy_user", DisplayName = "Legacy" }] };
    byte[] legacyBytes = JsonSerializer.SerializeToUtf8Bytes(legacy);
    File.WriteAllBytes(vault.FilePath, Dpapi.Protect(legacyBytes, Encoding.UTF8.GetBytes("LocalAccountManager.Metadata.v1")));
    vault.Dispose(); vault = new Vault(tempRoot);
    Reject(() => vault.Unlock("portable-test-master-password"), "legacy conversion requires explicit choice");
    var migrated = vault.Unlock("portable-test-master-password", convertLegacy: true);
    Assert(migrated.Version == 2 && migrated.Accounts.Single().UserId == 777, "v1 vault migrates while retaining existing accounts");
    vault.Save(migrated);
    Assert(vault.Load().Version == 2, "migrated vault persists in the new format");
    sealedData[sealedData.Length / 2] ^= 0x40; File.WriteAllBytes(vault.FilePath, sealedData);
    Reject(() => vault.Load(), "corrupt vault fails closed");
    Assert(File.ReadAllBytes(vault.FilePath).SequenceEqual(sealedData), "corrupt vault is preserved");
    using (var big = new FileStream(backup, FileMode.Create)) big.SetLength(9 * 1024 * 1024);
    Reject(() => Vault.ImportLayout(backup, "test-passphrase-123456"), "oversized backup rejected before decryption");
}
finally
{
    vault?.Dispose();
    foreach (string root in new[] { tempRoot, copiedRoot }) { foreach (string file in Directory.EnumerateFiles(root)) File.Delete(file); Directory.Delete(root); }
}

var seen = new List<(string Url, string? Cookie, string? Csrf)>();
int ticketCalls = 0;
using (var api = new RobloxApi(new FakeHandler(request =>
{
    string url = request.RequestUri!.AbsoluteUri;
    string? cookie = request.Headers.TryGetValues("Cookie", out var cv) ? cv.Single() : null;
    string? csrf = request.Headers.TryGetValues("X-CSRF-TOKEN", out var tv) ? tv.Single() : null;
    seen.Add((url, cookie, csrf));
    if (url.Contains("users/authenticated")) return Response(HttpStatusCode.OK, "{\"id\":123,\"name\":\"test\",\"displayName\":\"Test\"}");
    if (url.Contains("authentication-ticket"))
    {
        ticketCalls++;
        var response = Response(ticketCalls == 1 ? HttpStatusCode.Forbidden : HttpStatusCode.OK, "{}");
        response.Headers.Add(ticketCalls == 1 ? "x-csrf-token" : "rbx-authentication-ticket", ticketCalls == 1 ? "csrf-test" : "ticket-test");
        return response;
    }
    return Response(HttpStatusCode.OK, "{\"data\":[{\"id\":\"11111111-2222-3333-4444-555555555555\",\"playing\":2,\"maxPlayers\":10,\"ping\":42,\"fps\":59.9}],\"nextPageCursor\":\"abc\"}");
})))
{
    Assert((await api.Identity("fake-session")).Id == 123, "Roblox identity parsing");
    Assert(await api.Ticket("fake-session") == "ticket-test" && ticketCalls == 2, "CSRF challenge retried exactly once");
    Assert(seen[2].Csrf == "csrf-test", "CSRF token scoped to launch retry");
    var page = await api.Servers(123, "x&injected=yes", true);
    Assert(page.Servers.Count == 1 && page.NextCursor == "abc", "server pagination parsing");
    Assert(seen[^1].Cookie is null, "public server requests carry no session cookie");
    Assert(seen[^1].Url.Contains("cursor=x%26injected%3Dyes"), "server cursor query injection escaped");
    Assert(seen.Take(3).All(x => x.Cookie == ".ROBLOSECURITY=fake-session"), "only required authenticated calls carry session");
    bool blocked = false;
    try { await api.Identity("session\r\nInjected: yes"); } catch (UserError) { blocked = true; }
    Assert(blocked && seen.Count == 4, "cookie header injection blocked before network");
}
foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect })
{
    using var api = new RobloxApi(new FakeHandler(_ => Response(code, "secret-body-must-never-be-shown")));
    bool safe = false;
    try { await api.Identity("fake"); } catch (UserError ex) { safe = !ex.Message.Contains("secret-body"); }
    Assert(safe, "sanitized failure for HTTP " + (int)code);
}
Console.WriteLine($"All {passed} security checks passed. No real credentials were used.");

static HttpResponseMessage Response(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
