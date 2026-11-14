using MacroStudio.Model;

namespace MacroStudio.Storage;

/// <param name="Depth">0 at the top of the list; 1+ for a macro filed inside a folder macro (listed only there).</param>
/// <param name="Kids">How many macros are filed inside this one (it is then a folder).</param>
public sealed record LibraryItem(string Path, string Name, DateTime Modified, int Depth = 0, bool HasKids = false, bool Open = false, string? Parent = null, int Kids = 0)
{
    public double IndentPx => Depth * 18;
    public bool Nested => Depth > 0;
    public bool IsMacro => !HasKids;
    /// <summary>Segoe MDL2 glyphs: chevron down / right for a folder, nothing for a plain macro.</summary>
    public string Chevron => HasKids ? (Open ? "\uE70D" : "\uE76C") : "";
    /// <summary>An open or closed folder, or a page for a plain macro.</summary>
    public string Glyph => HasKids ? (Open ? "\uE838" : "\uE8B7") : "\uE8A5";
}

public sealed class MacroLibrary
{
    public const string Ext = ".macro.json";
    public string Dir { get; }

    public MacroLibrary(string? dir = null)
    {
        Dir = dir ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroStudio");
        Directory.CreateDirectory(Dir);
    }

    public static string NameOf(string path)
    {
        var f = System.IO.Path.GetFileName(path);
        return f.EndsWith(Ext, StringComparison.OrdinalIgnoreCase) ? f[..^Ext.Length] : System.IO.Path.GetFileNameWithoutExtension(f);
    }

    public List<LibraryItem> List() =>
        Directory.EnumerateFiles(Dir, "*" + Ext)
            .Select(p => new LibraryItem(p, NameOf(p), File.GetLastWriteTime(p)))
            .OrderByDescending(i => i.Modified).ToList();

    // ---- folders: a macro filed under another one lives only there ----

    readonly Dictionary<string, (DateTime Modified, string[] Ids)> _kids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ids of the macros filed under this file's macro. Cached until the file changes; a file without any is never parsed.</summary>
    public string[] SubIds(string path)
    {
        var m = File.GetLastWriteTimeUtc(path);
        lock (_kids) if (_kids.TryGetValue(path, out var c) && c.Modified == m) return c.Ids;
        string[] ids = [];
        try
        {
            // Header-only scan: "subs" sits before "events" in files we write, so the head of the file is enough;
            // a hand-edited file with subs later (or none) falls back to one full read. Events are never parsed.
            using var fs = File.OpenRead(path);
            var head = new byte[64 * 1024];
            int n = fs.Read(head, 0, head.Length);
            List<string> found;
            if (n == fs.Length) found = MacroSerializer.ReadSubsIds(head.AsSpan(0, n));
            else if (head.AsSpan(0, n).IndexOf("\"subs\""u8) >= 0)
            {
                try { found = MacroSerializer.ReadSubsIds(head.AsSpan(0, n)); }   // truncated JSON throws -> full read below
                catch (System.Text.Json.JsonException) { found = MacroSerializer.ReadSubsIds(ReadRest(fs, head, n)); }
            }
            else found = MacroSerializer.ReadSubsIds(ReadRest(fs, head, n));
            ids = found.Distinct().ToArray();
        }
        catch { }
        lock (_kids) _kids[path] = (m, ids);
        return ids;
    }

    static byte[] ReadRest(FileStream fs, byte[] head, int n)
    {
        var all = new byte[fs.Length];
        head.AsSpan(0, n).CopyTo(all);
        for (int off = n; off < all.Length;)
        {
            int r = fs.Read(all, off, all.Length - off);
            if (r == 0) return all[..off];
            off += r;
        }
        return all;
    }

    /// <summary>True when <paramref name="folderPath"/> already contains <paramref name="subPath"/>, directly or through other folders (filing it the other way round would loop).</summary>
    public bool Contains(string folderPath, string subPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = IdMap();
        bool Walk(string p) => seen.Add(p) && SubIds(p).Select(id => map.GetValueOrDefault(id)).Any(k => k != null && (string.Equals(k, subPath, StringComparison.OrdinalIgnoreCase) || Walk(k)));
        return Walk(folderPath);
    }

    /// <summary>
    /// Moves <paramref name="subId"/> into the folder macro in <paramref name="folderPath"/>: it is taken out of any other folder it was in, so it lives in one place.
    /// Returns an error text, or null when done.
    /// </summary>
    public string? AddSub(string folderPath, string subId)
    {
        var subPath = PathOfId(subId);
        if (subPath == null) return "That macro is not in the library.";
        if (string.Equals(subPath, folderPath, StringComparison.OrdinalIgnoreCase)) return "A macro cannot be moved into itself.";
        if (Contains(subPath, folderPath)) return "That would put a macro inside itself: the target is already inside the one you are moving.";
        foreach (var p in Directory.EnumerateFiles(Dir, "*" + Ext))
            if (!string.Equals(p, folderPath, StringComparison.OrdinalIgnoreCase) && SubIds(p).Contains(subId)) RemoveSub(p, subId);
        var m = MacroSerializer.Load(folderPath);
        if (!m.Subs.Contains(subId)) { m.Subs.Add(subId); MacroSerializer.Save(m, folderPath); }
        return null;
    }

    /// <summary>Takes <paramref name="subId"/> out of the folder macro (it goes back to the top of the list).</summary>
    public bool RemoveSub(string folderPath, string subId)
    {
        var m = MacroSerializer.Load(folderPath);
        if (!m.Subs.Remove(subId)) return false;
        MacroSerializer.Save(m, folderPath);
        return true;
    }

    /// <summary>The folder macro this one is filed in (its path), or null when it is at the top of the list.</summary>
    public string? FolderOf(string path)
    {
        var id = IdOf(path);
        return id == null ? null : Directory.EnumerateFiles(Dir, "*" + Ext).FirstOrDefault(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase) && SubIds(p).Contains(id));
    }

    /// <summary>The ids of every folder macro above this one (its folder, that folder's folder, and so on).</summary>
    public HashSet<string> AncestorIds(string path)
    {
        var ids = new HashSet<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
        for (var p = FolderOf(path); p != null && seen.Add(p); p = FolderOf(p)) if (IdOf(p) is { } id) ids.Add(id);
        return ids;
    }

    /// <summary>The ids filed under the macro in this file, read from disk.</summary>
    public List<string> ReadSubs(string path) { try { return MacroSerializer.Load(path).Subs; } catch { return []; } }

    /// <summary>
    /// The library list: macros that are filed in a folder appear only inside it (and only when it is open); everything else is at the top.
    /// </summary>
    public List<LibraryItem> ListTree(ISet<string> open)
    {
        var all = List();
        Prune(all);
        var map = IdMap();
        var kidsOf = new Dictionary<string, List<LibraryItem>>(StringComparer.OrdinalIgnoreCase);
        var filed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in all)
        {
            var kids = SubIds(it.Path).Select(id => map.GetValueOrDefault(id)).Where(p => p != null && !string.Equals(p, it.Path, StringComparison.OrdinalIgnoreCase))
                .Select(p => new LibraryItem(p!, NameOf(p!), File.GetLastWriteTime(p!), Parent: it.Path)).ToList();
            kidsOf[it.Path] = kids;
            foreach (var k in kids) filed.Add(k.Path);
        }
        var rows = new List<LibraryItem>();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(LibraryItem it, int depth, HashSet<string> trail)
        {
            var kids = kidsOf.GetValueOrDefault(it.Path)?.Where(k => !trail.Contains(k.Path)).ToList() ?? [];
            bool isOpen = kids.Count > 0 && open.Contains(it.Path);
            rows.Add(it with { Depth = depth, HasKids = kids.Count > 0, Open = isOpen, Kids = kids.Count });
            shown.Add(it.Path);
            if (isOpen && depth < 4) foreach (var k in kids) Add(k, depth + 1, [.. trail, it.Path]);
        }
        foreach (var t in all.Where(t => !filed.Contains(t.Path))) Add(t, 0, []);
        foreach (var t in all.Where(t => !shown.Contains(t.Path) && !filed.Contains(t.Path))) Add(t, 0, []);
        return rows;
    }

    /// <summary>Forgets cached ids/subs of files that no longer exist, so the caches cannot grow over a long session.</summary>
    void Prune(List<LibraryItem> all)
    {
        var live = new HashSet<string>(all.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        lock (_kids) foreach (var k in _kids.Keys.Where(k => !live.Contains(k)).ToList()) _kids.Remove(k);
        lock (_ids) foreach (var k in _ids.Keys.Where(k => !live.Contains(k)).ToList()) _ids.Remove(k);
    }

    /// <summary>A macro that is filed but whose folder is a loop (A in B, B in A) would never be listed: show it at the top so it is never lost.</summary>
    public static bool IsListed(IEnumerable<LibraryItem> rows, string path) => rows.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    public static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim();
        return name.Length == 0 ? "Untitled" : name;
    }

    /// <summary>First free path for the name: "Name", "Name (2)", ...</summary>
    public string UniquePath(string name)
    {
        name = Sanitize(name);
        var p = System.IO.Path.Combine(Dir, name + Ext);
        for (int n = 2; File.Exists(p); n++) p = System.IO.Path.Combine(Dir, $"{name} ({n}){Ext}");
        return p;
    }

    public string Duplicate(string path)
    {
        var dst = UniquePath(NameOf(path) + " copy");
        File.Copy(path, dst);
        return dst;
    }

    public string Rename(string path, string newName)
    {
        var dst = System.IO.Path.Combine(Dir, Sanitize(newName) + Ext);
        if (string.Equals(dst, path, StringComparison.OrdinalIgnoreCase)) return path;
        if (File.Exists(dst)) throw new IOException($"A macro named '{NameOf(dst)}' already exists.");
        File.Move(path, dst);
        return dst;
    }

    public void Delete(string path) => File.Delete(path);

    // ---- by id: how a When block refers to its connected macro ----

    readonly Dictionary<string, (DateTime Modified, string? Id)> _ids = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Text.RegularExpressions.Regex IdHead = new(@"""id""\s*:\s*""([^""]+)""");

    /// <summary>The id stored in the file's header (cheap: only the first bytes are read, and the result is cached until the file changes).</summary>
    /// <summary>The id stored in this macro file, or null when it has none yet (nothing is written).</summary>
    public string? IdFor(string path) => IdOf(path);

    string? IdOf(string path)
    {
        var m = File.GetLastWriteTimeUtc(path);
        lock (_ids) if (_ids.TryGetValue(path, out var c) && c.Modified == m) return c.Id;
        string? id = null;
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[512]; int n = f.Read(buf, 0, buf.Length);
            var mt = IdHead.Match(System.Text.Encoding.UTF8.GetString(buf, 0, n));
            if (mt.Success) id = mt.Groups[1].Value;
        }
        catch { }
        lock (_ids) _ids[path] = (m, id);
        return id;
    }

    /// <summary>id -> path for every file, built with one enumeration (the first file wins, like <see cref="PathOfId"/>).</summary>
    public Dictionary<string, string> IdMap()
    {
        var map = new Dictionary<string, string>();
        foreach (var p in Directory.EnumerateFiles(Dir, "*" + Ext)) if (IdOf(p) is { } id) map.TryAdd(id, p);
        return map;
    }

    volatile Dictionary<string, string>? _idx;   // last full id -> path scan; never mutated, only replaced

    /// <summary>The path of the macro with this id. A remembered answer is re-checked with one cached stat (so a rename, delete or edit is never missed); only a miss rescans the folder.</summary>
    public string? PathOfId(string id)
    {
        if (_idx is { } m && m.TryGetValue(id, out var p) && File.Exists(p) && IdOf(p) == id) return p;
        var fresh = IdMap(); _idx = fresh;
        return fresh.GetValueOrDefault(id);
    }
    public string? NameOfId(string id) => PathOfId(id) is { } p ? NameOf(p) : null;
    public Macro? LoadById(string id) { try { return PathOfId(id) is { } p ? MacroSerializer.Load(p) : null; } catch { return null; } }

    /// <summary>The id of the macro in this file; a macro that has none yet gets one (and is saved), so it can be linked to.</summary>
    public string EnsureId(string path)
    {
        if (IdOf(path) is { } id) return id;
        var m = MacroSerializer.Load(path);
        m.Id = Guid.NewGuid().ToString("N");
        MacroSerializer.Save(m, path);
        lock (_ids) _ids.Remove(path);
        return m.Id;
    }
}
