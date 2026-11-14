using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MacroStudio.Model;

namespace MacroStudio.Playback;

/// <summary>Posts one text message to a Discord webhook. Only genuine Discord webhook addresses are accepted, and the address never appears in an error message.</summary>
public static partial class DiscordWebhook
{
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(10) };

    [GeneratedRegex(@"^https://(?:ptb\.|canary\.)?discord(?:app)?\.com/api/webhooks/\d+/[A-Za-z0-9_-]+/?$")]
    private static partial Regex Valid();

    /// <summary>JPEG of the whole desktop right now (null if the screen cannot be captured). JPEG keeps a two-monitor shot well under Discord's upload limit.</summary>
    public static byte[]? Screenshot()
    {
        try
        {
            using var frame = MacroStudio.Vision.ScreenCapture.Capture();
            OpenCvSharp.Cv2.ImEncode(".jpg", frame, out var bytes, new OpenCvSharp.ImageEncodingParam(OpenCvSharp.ImwriteFlags.JpegQuality, 85));
            return bytes;
        }
        catch { return null; }
    }

    [GeneratedRegex(@"https?://(?:ptb\.|canary\.)?discord(?:app)?\.com/api/webhooks/\S+", RegexOptions.IgnoreCase)]
    private static partial Regex AnyHook();

    /// <summary>Removes anything that looks like a webhook address from a piece of text.</summary>
    public static string Scrub(string text) => AnyHook().Replace(text, "[webhook removed]");

    /// <summary>A copy of the recording that is safe to hand to someone else: no recording Id, and no webhook address even if one was typed into a message or the name.</summary>
    public static Macro ForExport(Macro m) => new()
    {
        Version = m.Version, Id = null, Name = Scrub(m.Name), Created = m.Created, Desktop = m.Desktop, Env = m.Env,
        Events = m.Events.Select(e => e.Kind == EventKind.Discord && e.Target != null ? e with { Target = Scrub(e.Target) }
            : e.Step?.DiscordText is { } t ? e with { Step = e.Step with { DiscordText = Scrub(t) } } : e).ToList(),
    };

    /// <summary>What may be shown on screen for a link: the host and the (public) id, never the secret token.</summary>
    public static string Mask(string? url) =>
        IsValid(url) ? System.Text.RegularExpressions.Regex.Replace(url!, @"^https://([^/]+)/api/webhooks/(\d+)/.*$", "$1/api/webhooks/$2/••••••••") : "";

    public static bool IsValid(string? url) => url != null && Valid().IsMatch(url);

    /// <summary>Null when sent, otherwise a short reason. Mentions are switched off, so a message can never ping @everyone.</summary>
    public static async Task<string?> Post(string url, string text, HttpClient? http = null, byte[]? screenshot = null)
    {
        if (!IsValid(url)) return "not a Discord webhook address";
        if (text.Length > 1900) text = text[..1900] + "…";
        var body = JsonSerializer.Serialize(new { content = text, allowed_mentions = new { parse = Array.Empty<string>() } });
        try
        {
            HttpContent content = new StringContent(body, Encoding.UTF8, "application/json");
            if (screenshot != null)
            {
                var form = new MultipartFormDataContent { { new StringContent(body, Encoding.UTF8, "application/json"), "payload_json" } };
                var file = new ByteArrayContent(screenshot); file.Headers.ContentType = new("image/jpeg");
                form.Add(file, "files[0]", "screenshot.jpg");
                content = form;
            }
            using (content)
            {
                using var r = await (http ?? Shared).PostAsync(url, content);
                return r.IsSuccessStatusCode ? null : $"Discord answered {(int)r.StatusCode} {r.ReasonPhrase}";
            }
        }
        catch (Exception ex) { return ex is TaskCanceledException ? "Discord did not answer in 10 s" : "could not reach Discord (" + ex.GetType().Name + ")"; }
    }
}

/// <summary>
/// Webhook links and the "message me when a macro ends" choices. Kept in this user's private AppData folder, never inside a recording, so
/// exporting or sharing a recording can never carry a webhook. Each recording (by its Id) can have its own link; a recording without one
/// uses the default link.
/// </summary>
public sealed class DiscordSettings
{
    /// <summary>The default link, used by every recording that has none of its own.</summary>
    public string? Url { get; set; }
    /// <summary>Recording Id -> that recording's own link.</summary>
    public Dictionary<string, string> Links { get; set; } = new();
    public bool OnFinish { get; set; }
    /// <summary>Attach a screenshot to the end-of-macro message.</summary>
    public bool ShotOnFinish { get; set; } = true;

    static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroStudio");
    static string FilePath => Path.Combine(AppDir, "private", "discord.json");
    static string OldPath => Path.Combine(AppDir, "discord.json");   // earlier versions kept it beside the recordings

    public static DiscordSettings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : OldPath;
            var s = JsonSerializer.Deserialize<DiscordSettings>(File.ReadAllText(path)) ?? new();
            if (!DiscordWebhook.IsValid(s.Url)) s.Url = null;
            s.Links = (s.Links ?? new()).Where(kv => DiscordWebhook.IsValid(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
            return s;
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            if (File.Exists(OldPath)) File.Delete(OldPath);
        }
        catch { }
    }

    /// <summary>The link this recording sends to: its own, else the default, else null.</summary>
    public string? UrlFor(string? recordingId) => recordingId != null && Links.TryGetValue(recordingId, out var u) ? u : Url;
    public bool HasOwn(string? recordingId) => recordingId != null && Links.ContainsKey(recordingId);
    public void SetLink(string recordingId, string? url) { if (url == null) Links.Remove(recordingId); else Links[recordingId] = url; }
    public void CopyLink(string from, string to) { if (Links.TryGetValue(from, out var u)) Links[to] = u; }
}

/// <summary>Bottom of the playback chain: a Discord step sends its message (without holding the macro up); every other event goes on to the real input.</summary>
public sealed class DiscordSink(IInputSink inner, Func<string?> url, ExecutionLog? log, Func<int> index, Func<byte[]?>? screenshot = null) : IInputSink
{
    public void Send(in MacroEvent e)
    {
        if (e.Kind != EventKind.Discord) { inner.Send(in e); return; }
        int i = index();
        if (url() is not { } hook) { log?.Add(i, "discord", LogResult.Skipped, "no webhook set: click \"Discord…\" in the toolbar"); return; }
        string text = e.Target ?? "";
        var grab = e.Extended ? (screenshot ?? DiscordWebhook.Screenshot) : (Func<byte[]?>?)null;   // Extended = "attach a screenshot"
        _ = Task.Run(async () =>
        {
            var shot = grab?.Invoke();      // the desktop capture + JPEG encode run off the playback thread: they must not make the next event late
            var err = await DiscordWebhook.Post(hook, text, null, shot);
            log?.Add(i, "discord", err == null ? LogResult.Ok : LogResult.Warn, err ?? (shot != null ? "message and screenshot sent" : "message sent"));
        });
    }
}
