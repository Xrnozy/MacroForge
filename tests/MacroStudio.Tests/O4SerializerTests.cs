using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacroStudio.Model;
using MacroStudio.Storage;

namespace MacroStudio.Tests;

/// <summary>
/// O4: the serializer keeps the *.macro.json byte format (indented header, one compact event per line) while the
/// write path got faster. These tests pin byte-identity against a replica of the original implementation and
/// equivalence of the new read paths, plus a 200k-event timing log.
/// </summary>
public class O4SerializerTests
{
    static readonly JsonSerializerOptions RefOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The original Serialize(), kept here verbatim as the byte-format reference.</summary>
    static string ReferenceSerialize(Macro m)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", m.Version);
            if (m.Id != null) w.WriteString("id", m.Id);
            w.WriteString("name", m.Name);
            w.WriteString("created", m.Created);
            if (m.Desktop != null)
            {
                w.WritePropertyName("desktop");
                w.WriteRawValue(JsonSerializer.Serialize(m.Desktop, RefOpts));
            }
            if (m.Env != null)
            {
                w.WritePropertyName("env");
                w.WriteRawValue(JsonSerializer.Serialize(m.Env, RefOpts));
            }
            if (m.Subs.Count > 0)
            {
                w.WriteStartArray("subs");
                foreach (var id in m.Subs) w.WriteStringValue(id);
                w.WriteEndArray();
            }
            w.WriteStartArray("events");
            foreach (var e in m.Events) w.WriteRawValue(JsonSerializer.Serialize(e, RefOpts));
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static Macro KitchenSink()
    {
        var ev = new List<MacroEvent>();
        double t = 0;
        foreach (var k in Enum.GetValues<EventKind>())
            ev.Add(new MacroEvent(t += 1.25, k, Vk: 0x41, Scan: 0x1E, Extended: true, X: -7, Y: 9999,
                Button: MouseBtn.X2, Delta: -120, Id: 42,
                Target: "tgt-\"quoted\" \\ back\tslash ☃ emoji🎯 and ] brackets [",
                Step: new StepOptions { RelX = 0.25, RelY = -0.5, MinConfidence = 0.9, Retries = 4, RetryDelayMs = 12,
                    RequireStable = false, MinRepeatMs = 5, Move = MoveMode.Glide, GlideMs = 321, OnFail = FailAction.UseRecordedPosition,
                    FallbackX = -3, FallbackY = 7, DiscordText = "hi \"there\"", DiscordShot = true, Verify = VerifyMode.OtherTargetAppears,
                    VerifyTarget = "v\"t", VerifyTimeoutMs = 9, RecoveryKey = 0x1B, RecoveryTarget = "r\\t" },
                Flow: new FlowOptions { Condition = WhenCondition.NotFound, Always = true, WaitMs = 42, KeepWaiting = true,
                    ClickFirst = true, Do = FlowDo.RunMacro, Macro = "m-id", After = FlowAfter.Restart, MaxRuns = 3 }));
        ev.Add(new MacroEvent(0, EventKind.MouseMove));                                  // all-default event: shortest line
        ev.Add(new MacroEvent(0.30000000000000004, EventKind.Wheel, Delta: 1));          // awkward double
        ev.Add(new MacroEvent(999999999999.9, EventKind.RelMove, X: int.MinValue, Y: int.MaxValue));
        ev.Add(new MacroEvent(5, EventKind.TargetClick, Target: null!));                 // explicit nulls stay absent
        var sparse = new MacroEvent(6, EventKind.KeyDown, Vk: 1) { };                    // default Button omitted
        ev.Add(sparse with { Target = "only-target" });
        return new Macro
        {
            Version = 1, Id = "m-1", Name = "kitchen \"sink\"", Created = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Desktop = new DesktopInfo(new ScreenRect(-1920, -30, 5760, 1110), [new ScreenRect(-1920, 0, 1920, 1080), new ScreenRect(0, -30, 1920, 1110), new ScreenRect(3840, 0, 1920, 1080)]),
            Env = new EnvInfo(144, 3, 10),
            Subs = ["a", "b"],
            Events = ev,
        };
    }

    static Macro Synthetic(int n)
    {
        var ev = new List<MacroEvent>(n);
        double t = 0;
        for (int i = 0; i < n; i++) { t += 4; ev.Add(new MacroEvent(t, EventKind.MouseMove, X: 100 + (i * 7) % 1700, Y: 100 + (i * 13) % 900)); }
        return new Macro { Name = "synthetic", Events = ev };
    }

    // ---------- byte identity ----------

    [Fact]
    public void Serialize_is_byte_identical_to_the_reference_writer()
    {
        var m = KitchenSink();
        Assert.Equal(ReferenceSerialize(m), MacroSerializer.Serialize(m));
        var s = Synthetic(5000);
        Assert.Equal(ReferenceSerialize(s), MacroSerializer.Serialize(s));
        var empty = new Macro { Name = "empty" };
        Assert.Equal(ReferenceSerialize(empty), MacroSerializer.Serialize(empty));
    }

    [Fact]
    public void SerializeUtf8_is_the_same_bytes_as_Serialize()
    {
        var m = KitchenSink();
        Assert.Equal(MacroSerializer.Serialize(m), Encoding.UTF8.GetString(MacroSerializer.SerializeUtf8(m)));
    }

    [Fact]
    public void SerializeEvents_is_byte_identical_to_STJ()
    {
        var events = KitchenSink().Events;
        Assert.Equal(JsonSerializer.Serialize(events, RefOpts), MacroSerializer.SerializeEvents(events));
        Assert.Equal(JsonSerializer.Serialize(events.Take(1).ToList(), RefOpts), MacroSerializer.SerializeEvents(events.Take(1)));
        Assert.Equal("[]", MacroSerializer.SerializeEvents([]));
    }

    [Fact]
    public void Saved_file_is_byte_identical_to_Serialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "o4ser-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var m = KitchenSink();
            var p = Path.Combine(dir, "k" + MacroLibrary.Ext);
            MacroSerializer.Save(m, p);
            Assert.Equal(MacroSerializer.Serialize(m), File.ReadAllText(p));
            var back = MacroSerializer.Load(p);
            Assert.Equal(m.Events.Count, back.Events.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- read paths ----------

    [Fact]
    public void Deserialize_span_overload_matches_string_overload()
    {
        var json = MacroSerializer.Serialize(KitchenSink());
        var a = MacroSerializer.Deserialize(json);
        var b = MacroSerializer.Deserialize(Encoding.UTF8.GetBytes(json));
        Assert.Equal(a.Events, b.Events);
        Assert.Equal(a.Desktop!.Virtual, b.Desktop!.Virtual);
        Assert.Equal(a.Desktop.Monitors, b.Desktop.Monitors);   // record equality on the List is by reference: compare the sequence
        Assert.Equal(a.Subs, b.Subs);
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(JsonSerializer.Deserialize<Macro>(json, RefOpts)!.Events.Select((e, i) => e with { Id = i + 1 }).ToList(), b.Events);
    }

    [Fact]
    public void Deserialize_tolerates_a_bom_and_renumbers_in_place()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"version\":1,\"name\":\"x\",\"events\":[{\"t\":0,\"kind\":\"MouseMove\"},{\"t\":1,\"kind\":\"KeyUp\",\"vk\":65}]}")).ToArray();
        var m = MacroSerializer.Deserialize(bytes);
        Assert.Equal(2, m.Events.Count);
        Assert.Equal(MouseBtn.Left, m.Events[1].Button);            // absent button = Left, not default-serialized
        Assert.Equal([1, 2], m.Events.Select(e => e.Id).ToArray());
        Assert.Equal(MouseBtn.X1, MacroSerializer.Deserialize("{\"version\":1,\"events\":[{\"t\":0,\"kind\":\"ButtonUp\",\"button\":\"X1\"}]}").Events[0].Button);
    }

    [Fact]
    public void ReadSubsIds_reads_only_the_header_and_tolerates_placement()
    {
        // normal layout: subs before events -> found
        var m = new Macro { Subs = ["kid-1", "kid-2"], Events = [new MacroEvent(0, EventKind.MouseMove, Target: "\"subs\"")] };
        Assert.Equal(["kid-1", "kid-2"], MacroSerializer.ReadSubsIds(Encoding.UTF8.GetBytes(MacroSerializer.Serialize(m))));
        // hand-edited layout: subs after the events array -> still found (events are skipped, not parsed)
        var json = "{\"version\":1,\"name\":\"x\",\"events\":[{\"t\":0,\"kind\":\"MouseMove\"},{\"t\":1,\"kind\":\"TargetClick\",\"target\":\"subs\"}],\"subs\":[\"late\"]}";
        Assert.Equal(["late"], MacroSerializer.ReadSubsIds(Encoding.UTF8.GetBytes(json)));
        // no subs / non-array / malformed
        Assert.Empty(MacroSerializer.ReadSubsIds(Encoding.UTF8.GetBytes("{\"version\":1,\"events\":[]}")));
        Assert.Empty(MacroSerializer.ReadSubsIds(Encoding.UTF8.GetBytes("{\"version\":1,\"subs\":\"nope\",\"events\":[]}")));
        Assert.ThrowsAny<JsonException>(() => MacroSerializer.ReadSubsIds("not json"u8));
        // a "subs" name nested inside an event object is ignored
        var nested = "{\"version\":1,\"events\":[{\"t\":0,\"kind\":\"When\",\"flow\":{\"subs\":[\"fake\"]}}]}";
        Assert.Empty(MacroSerializer.ReadSubsIds(Encoding.UTF8.GetBytes(nested)));
    }

    [Fact]
    public void Library_SubIds_does_not_parse_events()
    {
        var dir = Path.Combine(Path.GetTempPath(), "o4subs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new MacroLibrary(dir);
            var big = Synthetic(50_000); big.Subs = ["k1", "k2", "k1"];
            var p = lib.UniquePath("big");
            MacroSerializer.Save(big, p);
            var sw = Stopwatch.StartNew();
            Assert.Equal(["k1", "k2"], lib.SubIds(p));
            sw.Stop();
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "o4bench.log"), $"SubIds 50k-event folder: {sw.Elapsed.TotalMilliseconds:0.0} ms{Environment.NewLine}");
            Assert.True(sw.Elapsed.TotalMilliseconds < 1000, $"SubIds on a 50k-event folder macro took {sw.Elapsed.TotalMilliseconds:0} ms (should skim the header)");
            // a hand-edited file with "subs" AFTER the giant events array still resolves (the head-scan falls back)
            var p2 = Path.Combine(dir, "late" + MacroLibrary.Ext);
            var evJson = string.Join(",", Enumerable.Range(0, 3000).Select(i => $"{{\"t\":{i},\"kind\":\"MouseMove\",\"x\":{i}}}"));
            File.WriteAllText(p2, $"{{\"version\":1,\"name\":\"late\",\"events\":[{evJson}],\"subs\":[\"late-kid\"]}}");
            Assert.True(new FileInfo(p2).Length > 64 * 1024);
            Assert.Equal(["late-kid"], lib.SubIds(p2));
            // and a big file with no subs at all answers empty
            var p3 = Path.Combine(dir, "none" + MacroLibrary.Ext);
            File.WriteAllText(p3, $"{{\"version\":1,\"name\":\"none\",\"events\":[{evJson}]}}");
            Assert.Empty(lib.SubIds(p3));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- timing log (numbers for the perf report; bounds are wide so sibling load cannot flake them) ----------

    static double Median(Action a, int runs = 5)
    {
        a();
        var t = new double[runs];
        for (int i = 0; i < runs; i++) { var sw = Stopwatch.StartNew(); a(); t[i] = sw.Elapsed.TotalMilliseconds; }
        Array.Sort(t);
        return t[runs / 2];
    }

    [Fact]
    public void Benchmark_serialize_and_deserialize_200k_events()
    {
        var m = Synthetic(200_000);
        string json = "";
        double ser = Median(() => json = MacroSerializer.Serialize(m), 5);
        double serOld = Median(() => json = ReferenceSerialize(m), 3);
        double de = Median(() => MacroSerializer.Deserialize(json), 5);
        double deBytes = Median(() => MacroSerializer.Deserialize(Encoding.UTF8.GetBytes(json)), 5);
        double serEvents = Median(() => MacroSerializer.SerializeEvents(m.Events), 3);
        double serEventsOld = Median(() => JsonSerializer.Serialize(m.Events, RefOpts), 3);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        json = MacroSerializer.Serialize(m);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "o4bench.log"),
            $"O4 200k: serialize {ser:0} ms (old {serOld:0}), SerializeEvents {serEvents:0} ms (old {serEventsOld:0}), deserialize(string) {de:0} ms, deserialize(bytes) {deBytes:0} ms, serialize alloc {allocated / 1e6:0.0} MB, file {json.Length / 1e6:0.0} MB{Environment.NewLine}");
        Assert.True(ser < de * 4 + 400, $"serialize 200k: {ser:0} ms (deserialize {de:0} ms)");   // the write path must not be worse than the read path
    }
}
