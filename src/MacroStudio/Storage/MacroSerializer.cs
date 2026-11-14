using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacroStudio.Model;

namespace MacroStudio.Storage;

public static class MacroSerializer
{
    public const int CurrentVersion = 1;

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // Enum names exactly as JsonStringEnumConverter writes them (no naming policy = member names verbatim).
    static readonly string[] KindNames = Enum.GetNames<EventKind>();
    static readonly string[] BtnNames = Enum.GetNames<MouseBtn>();

    // A recorded event line runs ~45-60 bytes; presizing avoids the ArrayBufferWriter doubling chain on big macros.
    static int GuessSize(int events) => events * 56 + 256;

    /// <summary>Indented header, one compact event per line so files stay readable and diffable.</summary>
    public static string Serialize(Macro m)
    {
        var buf = new ArrayBufferWriter<byte>(GuessSize(m.Events.Count));
        Write(buf, m);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>The same document as UTF-8 bytes (Save goes straight to disk: no multi-MB UTF-16 string).</summary>
    public static byte[] SerializeUtf8(Macro m)
    {
        var buf = new ArrayBufferWriter<byte>(GuessSize(m.Events.Count));
        Write(buf, m);
        return buf.WrittenSpan.ToArray();
    }

    static void Write(IBufferWriter<byte> buf, Macro m)
    {
        // Per-event JSON is composed once into a small reusable buffer, then embedded raw into the indented document:
        // the old path materialised a string per event and made WriteRawValue re-parse it for validation.
        var evBuf = new ArrayBufferWriter<byte>(128);
        var ew = new Utf8JsonWriter(evBuf);
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", m.Version);
            if (m.Id != null) w.WriteString("id", m.Id);
            w.WriteString("name", m.Name);
            w.WriteString("created", m.Created);
            if (m.Desktop != null)
            {
                w.WritePropertyName("desktop");
                w.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(m.Desktop, Opts), skipInputValidation: true);
            }
            if (m.Env != null)
            {
                w.WritePropertyName("env");
                w.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(m.Env, Opts), skipInputValidation: true);
            }
            if (m.Subs.Count > 0)
            {
                w.WriteStartArray("subs");
                foreach (var id in m.Subs) w.WriteStringValue(id);
                w.WriteEndArray();
            }
            w.WriteStartArray("events");
            foreach (var e in m.Events)
            {
                evBuf.Clear();
                ew.Reset();
                WriteEvent(ew, in e);
                ew.Flush();
                w.WriteRawValue(evBuf.WrittenSpan, skipInputValidation: true); // we produced the JSON: no re-parse
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        ew.Dispose();
    }

    /// <summary>
    /// One event object, identical bytes to what JsonSerializer.Serialize(e, Opts) produced: same property order
    /// (record positional order), camelCase names, enum names as strings, defaults and nulls omitted, Id never written.
    /// </summary>
    static void WriteEvent(Utf8JsonWriter w, in MacroEvent e)
    {
        w.WriteStartObject();
        w.WriteNumber("t", e.T);
        if ((uint)e.Kind < (uint)KindNames.Length) w.WriteString("kind", KindNames[(int)e.Kind]);
        else w.WriteNumber("kind", (int)e.Kind);               // an unknown enum value serializes as its number, like the converter
        if (e.Vk != 0) w.WriteNumber("vk", e.Vk);
        if (e.Scan != 0) w.WriteNumber("scan", e.Scan);
        if (e.Extended) w.WriteBoolean("extended", true);
        if (e.X != 0) w.WriteNumber("x", e.X);
        if (e.Y != 0) w.WriteNumber("y", e.Y);
        if (e.Button != MouseBtn.Left)
        {
            if ((uint)e.Button < (uint)BtnNames.Length) w.WriteString("button", BtnNames[(int)e.Button]);
            else w.WriteNumber("button", (int)e.Button);
        }
        if (e.Delta != 0) w.WriteNumber("delta", e.Delta);
        if (e.Target != null) w.WriteString("target", e.Target);
        if (e.Step != null) { w.WritePropertyName("step"); JsonSerializer.Serialize(w, e.Step, Opts); }
        if (e.Flow != null) { w.WritePropertyName("flow"); JsonSerializer.Serialize(w, e.Flow, Opts); }
        w.WriteEndObject();
    }

    /// <summary>Compact array of events (the clipboard format). Same bytes as JsonSerializer.Serialize(events, Opts).</summary>
    public static string SerializeEvents(IEnumerable<MacroEvent> events)
    {
        var buf = new ArrayBufferWriter<byte>(events.TryGetNonEnumeratedCount(out int n) ? GuessSize(n) : 4096);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartArray();
            foreach (var e in events) WriteEvent(w, in e);
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    public static List<MacroEvent> DeserializeEvents(string json) =>
        JsonSerializer.Deserialize<List<MacroEvent>>(json, Opts) ?? throw new InvalidDataException("No events");

    public static Macro Deserialize(string json) => Post(JsonSerializer.Deserialize<Macro>(json, Opts));

    /// <summary>UTF-8 input: the file's bytes are parsed directly (no multi-MB UTF-16 string first).</summary>
    public static Macro Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.StartsWith("\uFEFF"u8)) utf8Json = utf8Json[3..];   // ReadAllText used to strip a BOM; keep tolerating it
        return Post(JsonSerializer.Deserialize<Macro>(utf8Json, Opts));
    }

    static Macro Post(Macro? m)
    {
        if (m == null) throw new InvalidDataException("Empty macro file");
        if (m.Version > CurrentVersion) throw new NotSupportedException($"Macro format version {m.Version} is newer than this app supports ({CurrentVersion}).");
        var ev = m.Events;
        for (int i = 0; i < ev.Count; i++) ev[i] = ev[i] with { Id = i + 1 };   // renumber in place (one list, no LINQ copy)
        return m;
    }

    /// <summary>
    /// Only the header's "subs" array, without materialising the events: the reader skips every other value wholesale,
    /// so a 100k-event folder macro costs a byte scan, not a full deserialize. Tolerates subs appearing anywhere.
    /// </summary>
    public static List<string> ReadSubsIds(ReadOnlySpan<byte> utf8Json)
    {
        var ids = new List<string>();
        if (utf8Json.StartsWith("\uFEFF"u8)) utf8Json = utf8Json[3..];
        var r = new Utf8JsonReader(utf8Json);
        int depth = 0;
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.PropertyName && depth == 1 && r.ValueTextEquals("subs"))
            {
                if (r.Read() && r.TokenType == JsonTokenType.StartArray)
                    while (r.Read())
                    {
                        if (r.TokenType == JsonTokenType.String) { if (r.GetString() is { } s) ids.Add(s); }
                        else return ids;                          // EndArray (or a malformed file): stop
                    }
                return ids;
            }
            if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                if (depth == 0) depth++;                          // the root object
                else r.Skip();                                    // a nested value (desktop/env/events): skip its bytes
            }
        }
        return ids;
    }

    public static void Save(Macro m, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, SerializeUtf8(m));
        File.Move(tmp, path, overwrite: true); // atomic replace: a crash never leaves a half-written macro
    }

    public static Macro Load(string path) => Deserialize(File.ReadAllBytes(path));
}
