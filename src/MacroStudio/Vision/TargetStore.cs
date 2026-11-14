using System.Text.Json;
using MacroStudio.Model;
using OpenCvSharp;

namespace MacroStudio.Vision;

/// <summary>A registered visual target: the pixels live in <c>&lt;id&gt;.png</c>, this is its metadata (<c>&lt;id&gt;.json</c>).</summary>
public sealed class Target
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Target";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    /// <summary>System DPI when captured; replay scales the template by currentDpi / captureDpi first.</summary>
    public int CaptureDpi { get; set; } = 96;
    public ScreenRect CaptureDesktop { get; set; }
    public double Threshold { get; set; } = 0.85;
    /// <summary>Also match when part of the target is covered (part voting, then feature matching).</summary>
    public bool Occlusion { get; set; } = true;
    /// <summary>Share of the target (0.2-0.9) that must be visible for the part-voting stage.</summary>
    public double MinVisible { get; set; } = 0.5;
    /// <summary>Text on the target to read with OCR instead of matching its pixels ("" = match pixels).</summary>
    public string Text { get; set; } = "";
    /// <summary>Optional label that organises related targets ("Main menu", "Shop"...).</summary>
    public string Group { get; set; } = "";
    /// <summary>How far the target's average colour may drift (hover, tint, state change) and still match. 0 = identical colour, 30 = slight variation (default), 250+ = ignore colour.</summary>
    public double ColorTolerance { get; set; } = ColorMath.DefaultTolerance;
    /// <summary>Where the target was on screen when it was registered. A recorded click inside this rectangle becomes a relative click.</summary>
    public ScreenRect CaptureRect { get; set; }
    /// <summary>Pixels of surroundings saved around the target (0 = none). Used to tell visually identical targets apart by what is next to them.</summary>
    public int ContextPad { get; set; }
    /// <summary>The recording (its Id) this target belongs to; <see cref="Everyone"/> = shared on purpose with every recording; null = made before targets belonged to recordings.</summary>
    public string? Owner { get; set; }
    public const string Everyone = "*";

    internal Target Copy() => (Target)MemberwiseClone();

    /// <summary>
    /// Does the panel of the recording with this id list this target? Its own, the ones shared on purpose, and any older or foreign target that the
    /// recording's steps use, and (<paramref name="inheritedFrom"/>) the targets of every macro whose folder this recording is in, at any depth. (Everything else is still in the library: "show all" lists it.)
    /// </summary>
    public bool VisibleTo(string? recordingId, ISet<string> usedByRecording, ISet<string>? inheritedFrom = null) =>
        Owner == Everyone || (Owner != null && Owner == recordingId) || usedByRecording.Contains(Id) || (Owner != null && inheritedFrom?.Contains(Owner) == true);
}

public sealed class TargetStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly Dictionary<string, Mat> _images = new();
    readonly Dictionary<string, Mat> _ctx = new();
    public string Dir { get; }

    public TargetStore(string? dir = null)
    {
        Dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroStudio", "targets");
        Directory.CreateDirectory(Dir);
    }

    string Meta(string id) => Path.Combine(Dir, id + ".json");
    string Png(string id) => Path.Combine(Dir, id + ".png");
    string CtxPng(string id) => Path.Combine(Dir, id + ".ctx.png");

    // ---------- metadata cache ----------
    // Parsed targets are kept in memory. Our own writes update them at once; files changed by another instance are noticed by a directory
    // listing (names, sizes, write times: no file is opened) at most every RescanMs, and only the changed files are parsed again.

    const int RescanMs = 400;
    readonly Dictionary<string, (Target T, DateTime Stamp, long Len)> _meta = new();
    readonly object _metaLock = new();
    long _lastScan = long.MinValue;

    void Refresh()
    {
        lock (_metaLock)
        {
            long now = Environment.TickCount64;
            if (_lastScan != long.MinValue && now - _lastScan < RescanMs) return;
            _lastScan = now;
            var seen = new HashSet<string>();
            var pngs = new HashSet<string>(new DirectoryInfo(Dir).EnumerateFiles("*.png").Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var f in new DirectoryInfo(Dir).EnumerateFiles("*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(f.Name);
                seen.Add(id);
                if (_meta.TryGetValue(id, out var m) && m.Stamp == f.LastWriteTimeUtc && m.Len == f.Length) continue;
                try
                {
                    if (JsonSerializer.Deserialize<Target>(File.ReadAllText(f.FullName), Json) is { Id.Length: > 0 } t) _meta[id] = (t, f.LastWriteTimeUtc, f.Length);
                    else _meta.Remove(id);
                }
                catch { _meta.Remove(id); /* unreadable (or half written by another instance): skipped until it changes */ }
            }
            foreach (var id in _meta.Keys.Where(k => !seen.Contains(k) || !pngs.Contains(k + ".png")).ToList()) Forget(id);
        }
    }

    void Forget(string id)
    {
        _meta.Remove(id);
        lock (_images) { _images.Remove(id); _ctx.Remove(id); }   // not disposed: a watcher may still be matching with it
    }

    /// <summary>Every target, by name. The objects are copies: changing one does nothing until <see cref="Save"/>.</summary>
    public List<Target> List()
    {
        Refresh();
        lock (_metaLock) return _meta.Values.Select(v => v.T.Copy()).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Target? Get(string id)
    {
        Refresh();
        lock (_metaLock) return _meta.TryGetValue(id, out var m) ? m.T.Copy() : null;
    }

    public Target Add(string name, Mat image, int dpi, ScreenRect desktop, double threshold, ScreenRect? rect = null, Mat? context = null, int contextPad = 0, string group = "")
    {
        var t = new Target { Id = Guid.NewGuid().ToString("N")[..8], Name = name, CaptureDpi = dpi, CaptureDesktop = desktop, Threshold = threshold, CaptureRect = rect ?? default, Group = group, ContextPad = context == null ? 0 : contextPad };
        Cv2.ImEncode(".png", image, out var png); // not ImWrite: it breaks on non-ASCII profile paths
        File.WriteAllBytes(Png(t.Id), png);
        if (context != null) { Cv2.ImEncode(".png", context, out var cp); File.WriteAllBytes(CtxPng(t.Id), cp); }
        Save(t);
        return t;
    }

    /// <summary>A copy of a target (pixels, surroundings and settings) under a new id, owned by <paramref name="newOwner"/>.</summary>
    public Target Clone(string id, string? newOwner)
    {
        var src = Get(id) ?? throw new FileNotFoundException("target " + id);
        var t = JsonSerializer.Deserialize<Target>(JsonSerializer.Serialize(src, Json), Json)!;
        t.Id = Guid.NewGuid().ToString("N")[..8]; t.Owner = newOwner; t.Created = DateTimeOffset.Now;
        File.Copy(Png(id), Png(t.Id));
        if (File.Exists(CtxPng(id))) File.Copy(CtxPng(id), CtxPng(t.Id));
        Save(t);
        return t;
    }

    public void Save(Target t)
    {
        File.WriteAllText(Meta(t.Id), JsonSerializer.Serialize(t, Json));
        var f = new FileInfo(Meta(t.Id));
        lock (_metaLock) _meta[t.Id] = (t.Copy(), f.LastWriteTimeUtc, f.Length);
    }

    /// <summary>The template pixels (cached, shared: do not dispose).</summary>
    public Mat Image(string id)
    {
        lock (_images)
        {
            if (!_images.TryGetValue(id, out var m))
                _images[id] = m = Cv2.ImDecode(File.ReadAllBytes(Png(id)), ImreadModes.Color);
            return m;
        }
    }

    public void Delete(string id)
    {
        lock (_images) if (_images.Remove(id, out var m)) m.Dispose();
        lock (_images) if (_ctx.Remove(id, out var c)) c.Dispose();
        File.Delete(Meta(id)); File.Delete(Png(id)); if (File.Exists(CtxPng(id))) File.Delete(CtxPng(id));
        lock (_metaLock) _meta.Remove(id);
    }

    /// <summary>The saved surroundings of a target (target plus <see cref="Target.ContextPad"/> px on every side), or null if none were saved.</summary>
    public Mat? Context(string id)
    {
        lock (_images)
        {
            if (_ctx.TryGetValue(id, out var m)) return m;
            if (!File.Exists(CtxPng(id))) return null;
            return _ctx[id] = Cv2.ImDecode(File.ReadAllBytes(CtxPng(id)), ImreadModes.Color);
        }
    }
}
