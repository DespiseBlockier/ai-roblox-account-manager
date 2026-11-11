using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace LocalAccountManager;

internal sealed class TemporaryLivePlayer
{
    private const string FolderPrefix = "ram-live-player-";
    private readonly List<DirectoryInfo> ownedFolders = [];
    private readonly List<Process> players = [];
    private DirectoryInfo? installed;

    public async Task<string> Install(IProgress<string> progress, CancellationToken cancel)
    {
        var folder = Directory.CreateTempSubdirectory(FolderPrefix);
        ownedFolders.Add(folder);
        try
        {
            var version = await LivePlayerDownload.Install(folder.FullName, progress, cancel);
            cancel.ThrowIfCancellationRequested();
            installed = folder;
            return version;
        }
        catch
        {
            if (TryRemove(folder)) ownedFolders.Remove(folder);
            throw;
        }
    }

    public void Launch(string uri)
    {
        if (installed is null) { Security.LaunchRoblox(uri); return; }
        if (!Security.IsPlayerLaunchUri(uri)) throw new UserError("Blocked an invalid Roblox launch request.");

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(installed.FullName, LivePlayerDownload.ExecutableName),
            WorkingDirectory = installed.FullName,
            UseShellExecute = false
        };
        start.ArgumentList.Add(uri);
        try
        {
            var player = Process.Start(start) ?? throw new InvalidOperationException();
            players.Add(player);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new UserError("Windows could not open the temporary LIVE player.");
        }
    }

    public void Remove()
    {
        foreach (var player in players)
        {
            if (player.HasExited) continue;
            try { player.CloseMainWindow(); }
            catch (Win32Exception) { /* The retained process handle is still available for shutdown. */ }
        }

        var grace = Stopwatch.StartNew();
        foreach (var player in players)
        {
            if (player.WaitForExit(Math.Max(0, 1200 - (int)grace.ElapsedMilliseconds))) continue;
            try { player.Kill(); }
            catch (InvalidOperationException) when (player.HasExited) { }
        }

        grace.Restart();
        foreach (var player in players)
        {
            if (!player.WaitForExit(Math.Max(0, 2000 - (int)grace.ElapsedMilliseconds)))
                throw new UserError("Close the temporary Roblox windows and try closing the manager again.");
        }
        foreach (var player in players) player.Dispose();
        players.Clear();
        installed = null;

        for (int attempt = 0; ownedFolders.Count > 0 && attempt < 5; attempt++)
        {
            ownedFolders.RemoveAll(TryRemove);
            if (ownedFolders.Count > 0) Thread.Sleep(150);
        }
        if (ownedFolders.Count > 0)
            throw new UserError("Windows is still using the temporary Roblox folder. Try closing the manager again in a moment.");
    }

    private static bool TryRemove(DirectoryInfo folder)
    {
        // Only delete a directory created for this session, directly inside the Windows temp folder.
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string? parent = folder.Parent?.FullName;
        if (!temp.Equals(parent, StringComparison.OrdinalIgnoreCase) || !folder.Name.StartsWith(FolderPrefix, StringComparison.Ordinal))
            return false;

        try
        {
            var attributes = File.GetAttributes(folder.FullName);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory) return false;
            folder.Delete(recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException) { return true; }
        catch (FileNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
