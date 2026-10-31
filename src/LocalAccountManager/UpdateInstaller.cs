using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public static class UpdateInstaller
{
    public const string ExeName = "RobloxAccountManager.exe";
    private static readonly HashSet<string> Files = new(StringComparer.Ordinal)
        { ExeName, "START-HERE.txt", "SHA256SUMS.txt", "Docs/README.md", "Docs/NOTICE.md", "Docs/LICENSE", "Docs/examples/join-group.js" };
    public static bool AllowedFile(string relative) => Files.Contains(relative) || Regex.IsMatch(relative, @"^Docs/ThirdPartyNotices/[A-Za-z0-9._-]+\.txt$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    public static string Digest(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    public static void NoLinks(string root, string target)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); target = Path.GetFullPath(target);
        if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) && !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UserError("The update path left its application folder.");
        for (string? path = target; path is not null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new UserError("Updates cannot use symbolic links or junctions. Use a normal local application folder.");
    }
    public static Dictionary<string, string> ExtractVerified(string archive, string destination, SignedUpdate update, byte[]? publicKey = null)
    {
        update = UpdateTrust.Verify(update.Bytes, update.Signature, update.Manifest.Version, publicKey);
        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != update.Manifest.Size || Convert.ToHexStringLower(SHA256.HashData(file)) != update.Manifest.Sha256) throw new UserError("The update ZIP failed SHA-256 verification. Nothing was installed.");
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 3 or > 128 || Directory.Exists(destination)) throw new UserError("The update package layout is invalid.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            // Windows ZIP producers/readers can expose DOS separators. Normalize before all path and duplicate checks.
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith("RobloxAccountManager/", StringComparison.Ordinal) || name.Any(char.IsControl) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new UserError("Unsafe entry in the update ZIP.");
            string relative = name["RobloxAccountManager/".Length..];
            if (relative.Split('/').Any(part => part is "." or ".." || part.Contains(':'))) throw new UserError("Unsafe update path.");
            if (name.EndsWith('/'))
            {
                if (relative is not ("" or "Docs/" or "Docs/ThirdPartyNotices/" or "Docs/examples/")) throw new UserError("Unexpected folder in the update ZIP.");
                continue;
            }
            if (!AllowedFile(relative) || !entries.TryAdd(relative, entry) || entry.Length is < 1 or > UpdateTrust.MaxDownload) throw new UserError("Unexpected file in the update ZIP.");
            total += entry.Length; if (total > 768L * 1024 * 1024) throw new UserError("The extracted update is too large.");
        }
        if (!Files.All(entries.ContainsKey)) throw new UserError("The update package is incomplete.");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entries)
        {
            string path = Path.GetFullPath(Path.Combine(destination, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
            NoLinks(destination, path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = pair.Value.Open(); using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long copied = 0; byte[] buffer = new byte[64 * 1024]; int count;
                while ((count = input.Read(buffer)) > 0) { copied += count; if (copied > pair.Value.Length) throw new UserError("The update entry exceeded its declared size."); output.Write(buffer, 0, count); }
                if (copied != pair.Value.Length) throw new UserError("The update entry is truncated.");
                output.Flush(true);
            }
            hashes[pair.Key] = Digest(path);
        }
        byte[] executable = new byte[2]; using (var input = File.OpenRead(Path.Combine(destination, ExeName))) input.ReadExactly(executable);
        if (executable[0] != 'M' || executable[1] != 'Z') throw new UserError("The update executable is invalid.");
        var checkedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(Path.Combine(destination, "SHA256SUMS.txt")))
        {
            string[] parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2 || !checkedFiles.Add(parts[1].Replace('\\', '/')) || !hashes.TryGetValue(parts[1].Replace('\\', '/'), out string? digest) || parts[0] != digest) throw new UserError("An update file failed checksum verification.");
        }
        if (!checkedFiles.SetEquals(hashes.Keys.Where(name => name != "SHA256SUMS.txt"))) throw new UserError("The update checksum list is incomplete.");
        return hashes;
    }
    public static void ApplyPayload(string payload, string root, string backup, IReadOnlyDictionary<string, string> hashes)
    {
        var changed = new List<(string Name, bool Existed)>();
        try
        {
            foreach (var pair in hashes.OrderBy(p => p.Key == ExeName).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!AllowedFile(pair.Key)) throw new UserError("Blocked an unexpected installation file.");
                string relative = pair.Key.Replace('/', Path.DirectorySeparatorChar);
                string source = Path.Combine(payload, relative), target = Path.Combine(root, relative), old = Path.Combine(backup, relative);
                NoLinks(payload, source); NoLinks(root, target); NoLinks(backup, old);
                bool existed = File.Exists(target);
                if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(old)!); File.Copy(target, old, false); }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".update-tmp";
                try
                {
                    File.Copy(source, temp, false);
                    if (Digest(temp) != pair.Value) throw new UserError("A staged update file changed. Installation was stopped.");
                    File.Move(temp, target, true); changed.Add((pair.Key, existed));
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        catch
        {
            bool restored = true;
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                try
                {
                    string relative = item.Name.Replace('/', Path.DirectorySeparatorChar), target = Path.Combine(root, relative);
                    if (item.Existed) File.Copy(Path.Combine(backup, relative), target, true); else File.Delete(target);
                }
                catch { restored = false; }
            }
            if (!restored) throw new UserError("The update failed and rollback was incomplete. Keep the backup folder under .updates and restore the previous app files. Your Data folder was not changed.");
            throw new UserError("The update failed. Previous app files were restored; your Data folder was not changed.");
        }
    }
    public static async Task<Process> StartHelperAsync(string root, string work, string dataRoot)
    {
        string executable = Environment.ProcessPath ?? throw new UserError("Cannot find the running executable.");
        if (!Path.GetFullPath(executable).Equals(Path.Combine(Path.GetFullPath(root), ExeName), StringComparison.OrdinalIgnoreCase)) throw new UserError("Install updates from the extracted portable release, not a development build.");
        string helper = Path.Combine(work, "Updater.exe"); File.Copy(executable, helper, false);
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in new[] { "--apply-update", root, work, current.Id.ToString(), current.StartTime.ToUniversalTime().Ticks.ToString(), dataRoot }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new UserError("Windows could not start the updater.");
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(Path.Combine(work, "failure.txt")) || process.HasExited) throw new UserError("The updater could not prepare the release. The current app is still running.");
                if (File.Exists(Path.Combine(work, "ready"))) return process;
                await Task.Delay(150);
            }
            throw new UserError("The updater did not become ready in time. Try again.");
        }
        catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
    }
    public static async Task<int> RunHelperAsync(string[] args)
    {
        bool parentExited = false, validWork = false;
        string root = "", work = "", dataRoot = "";
        try
        {
            if (args.Length != 6) throw new UserError("Invalid update arguments.");
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1])); work = Path.GetFullPath(args[2]); dataRoot = Path.GetFullPath(args[5]);
            string parent = Path.Combine(root, ".updates");
            string helper = Environment.ProcessPath ?? throw new UserError("Cannot identify the updater executable.");
            if (!string.Equals(Path.GetDirectoryName(work), parent, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(work), "^[a-f0-9]{32}$") || !helper.Equals(Path.Combine(work, "Updater.exe"), StringComparison.OrdinalIgnoreCase)) throw new UserError("Invalid updater location.");
            NoLinks(root, work); validWork = true;
            if (Digest(Path.Combine(root, ExeName)) != Digest(helper)) throw new UserError("The original application changed before updating.");
            byte[] bytes = ReadSmall(Path.Combine(work, "manifest.json"), 4096), signature = ReadSmall(Path.Combine(work, "manifest.sig"), 64);
            using var json = System.Text.Json.JsonDocument.Parse(bytes);
            string version = json.RootElement.GetProperty("version").GetString() ?? "";
            var update = UpdateTrust.Verify(bytes, signature, version);
            if (UpdateTrust.ParseVersion(version) < UpdateTrust.ParseVersion(UpdateTrust.CurrentVersion)) throw new UserError("An older release cannot replace this version.");
            string payload = Path.Combine(work, "payload"); var hashes = ExtractVerified(Path.Combine(work, "release.zip"), payload, update);
            Process? process = null;
            try
            {
                process = Process.GetProcessById(int.Parse(args[3]));
                if (process.StartTime.ToUniversalTime().Ticks != long.Parse(args[4]) || !string.Equals(process.MainModule?.FileName, Path.Combine(root, ExeName), StringComparison.OrdinalIgnoreCase)) throw new UserError("The original application identity changed.");
            }
            catch (ArgumentException) { process?.Dispose(); process = null; }
            File.WriteAllText(Path.Combine(work, "ready"), "ready");
            if (process is not null)
            {
                using (process) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await process.WaitForExitAsync(timeout.Token); }
            }
            parentExited = true;
            using (var installLock = new FileStream(Path.Combine(parent, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                ApplyPayload(payload, root, Path.Combine(work, "backup"), hashes);
            File.WriteAllText(Path.Combine(work, "completed"), "completed");
            File.Delete(Path.Combine(parent, "last-error.txt"));
            Restart(root, dataRoot, work); return 0;
        }
        catch (Exception error)
        {
            if (validWork)
            {
                string reason = error is UserError ? error.Message : "The update could not read or replace its application files. Check folder permissions and available disk space.";
                try { File.WriteAllText(Path.Combine(work, "failure.txt"), reason); } catch { }
                if (parentExited)
                {
                    try { File.WriteAllText(Path.Combine(root, ".updates", "last-error.txt"), "Update failed; check the backup in .updates if recovery is needed."); Restart(root, dataRoot, work); } catch { }
                }
            }
            return 1;
        }
    }
    private static byte[] ReadSmall(string path, int max)
    {
        using var input = File.OpenRead(path); if (input.Length > max) throw new UserError("Update information is too large.");
        byte[] result = new byte[(int)input.Length]; input.ReadExactly(result); return result;
    }
    private static void Restart(string root, string dataRoot, string work)
    {
        var start = new ProcessStartInfo(Path.Combine(root, ExeName)) { UseShellExecute = false, WorkingDirectory = root };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(dataRoot);
        string token = Path.Combine(work, "unlock-once.dpapi");
        if (File.Exists(token)) { start.ArgumentList.Add("--unlock-token"); start.ArgumentList.Add(token); }
        Process.Start(start)?.Dispose();
    }
    public static void CleanupCompleted(string root)
    {
        string parent = Path.Combine(root, ".updates");
        try
        {
            NoLinks(root, parent); if (!Directory.Exists(parent)) return;
            foreach (string folder in Directory.GetDirectories(parent))
            {
                if (!Regex.IsMatch(Path.GetFileName(folder), "^[a-f0-9]{32}$") || !File.Exists(Path.Combine(folder, "completed"))) continue;
                NoLinks(root, folder);
                // Keep the recovery backup. Remove only known download, preview and helper files after success.
                foreach (string name in new[] { "release.zip", "Updater.exe" })
                {
                    string path = Path.Combine(folder, name); NoLinks(root, path); try { File.Delete(path); } catch (IOException) { }
                }
            }
        }
        catch { /* Cleanup never blocks startup or touches Data. */ }
    }
}
