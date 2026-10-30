namespace LocalAccountManager;

public sealed record BulkChanges(string? Group = null, string? AliasTemplate = null, string? Notes = null, bool AppendNotes = false, bool? Favorite = null);
public sealed record BulkPreview(Account Account, string Group, string Alias, string Notes, bool Favorite)
{
    public string Username => "@" + Account.Username;
}

public static class BulkEditing
{
    public static List<BulkPreview> Preview(IReadOnlyList<Account> accounts, BulkChanges changes)
    {
        if (accounts.Count == 0) throw new UserError("Select accounts to edit.");
        if (changes.Group is null && changes.AliasTemplate is null && changes.Notes is null && changes.Favorite is null)
            throw new UserError("Choose at least one field to apply.");
        var result = new List<BulkPreview>();
        for (int i = 0; i < accounts.Count; i++)
        {
            var a = accounts[i];
            string group = changes.Group is null ? a.Group : string.IsNullOrWhiteSpace(changes.Group) ? "Default" : changes.Group.Trim();
            string alias = changes.AliasTemplate is null ? a.Alias : changes.AliasTemplate
                .Replace("{username}", a.Username, StringComparison.Ordinal)
                .Replace("{displayName}", a.DisplayName, StringComparison.Ordinal)
                .Replace("{index}", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            string notes = changes.Notes is null ? a.Notes : changes.AppendNotes && a.Notes.Length > 0 && changes.Notes.Length > 0 ? a.Notes + "\n" + changes.Notes : changes.AppendNotes && changes.Notes.Length == 0 ? a.Notes : changes.Notes;
            if (group.Length > 80 || group.Any(char.IsControl) || alias.Length > 80 || alias.Contains('\0') || notes.Length > 4000 || notes.Contains('\0'))
                throw new UserError("An edited value exceeds its limit (group/alias: 80, notes: 4,000 characters). Check the preview and alias expansion.");
            result.Add(new(a, group, alias, notes, changes.Favorite ?? a.Favorite));
        }
        return result;
    }

    public static void Apply(IReadOnlyList<Account> accounts, BulkChanges changes, Action save)
    {
        var next = Preview(accounts, changes);
        var previous = accounts.Select(a => new BulkPreview(a, a.Group, a.Alias, a.Notes, a.Favorite)).ToList();
        void Set(IEnumerable<BulkPreview> values)
        { foreach (var value in values) { var a = value.Account; a.Group = value.Group; a.Alias = value.Alias; a.Notes = value.Notes; a.Favorite = value.Favorite; } }
        Set(next);
        try { save(); } catch { Set(previous); throw; }
    }
}
