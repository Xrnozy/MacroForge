# Concurrency audit

Read-only audit of the shared-state paths in Macro Studio, ordered by severity. Each finding names the file and
line, the race or deadlock scenario, a severity, a suggested fix, and which file the fix belongs in. The only fixes
applied by this audit are inside `src/MacroStudio/Editing/EditSession.cs`; everything else is documented for the
agent that owns that file.

Threading map: the UI thread owns the views, the timers and `EditSession` selection/playhead state; a Windows hook
thread (`InputHooks`) raises raw input; a fallback poller thread checks real key/button state; a channel consumer
task stores `Recorder` events; a `TargetWatcher` worker scans the screen; a `Player` thread runs playback.

## High

### 1. `InputHooks._seen` / `_hookAt` are shared between the hook thread and the poller without synchronization
- `src/MacroStudio/Native/InputHooks.cs:19,32` — declarations; writes at `:185, :229` (hook thread); read-and-write
  sequence at `:134–140` (poller thread); `_seenX/_seenY` written at `:103, :118, :120` (poller) and `:225` (hook).
- Scenario: the poller reads `_seen[vk]` twice (`KeyPollPlan.Due`, then `down == _seen[vk]`) before writing it. If the
  hook thread updates `_seen[vk]` and `_hookAt[vk]` between those reads, the poller either reports a transition the
  hook already delivered (duplicate event enters the recording) or suppresses a real one (lost event). `_hookAt` is
  written with a plain store but read with `Volatile.Read` at `:138`, so the freshness the grace window depends on is
  only guaranteed on one side. `_seenX/_seenY` is a non-atomic pair: a torn read can make the poller report a phantom
  cursor move.
- Severity: high for correctness (the arrays exist precisely to deduplicate hook vs. reality; the dedup invariant can
  break under load), medium for frequency (the two-poll `bad[vk]` confirmation and the `_hookAt` grace window absorb
  most races).
- Fix: make all `_seen`/`_hookAt` access go through `Volatile.Read`/`Volatile.Write` (cheap, keeps the lock-free
  design), or funnel poller discoveries through the channel consumer so exactly one thread owns `_seen`. Keep
  `_seenX/_seenY` updates consistent by packing them into one `long` or guarding the pair with a tiny lock.
- Owner: sibling agent — `Native/InputHooks.cs`.

## Medium

### 2. `TargetWatcher.History(id)` locks the wrong object
- `src/MacroStudio/Vision/TargetWatcher.cs:218–221` — `History` takes `lock (_lock)` and calls
  `r.History.ToArray()`; `AddHistory` at `:494` mutates the same `Queue<HistoryItem>` under `lock (r.History)`.
- Scenario: the watcher thread enqueues/dequeues on a UI tick while the UI thread calls `History`
  (`MainWindow.Diagnostics.cs:92` during playback diagnostics). Two different locks guard one queue, so `ToArray`
  can enumerate mid-mutation: torn entries, or an `ArgumentException`/`IndexOutOfRangeException` when `_head`/`_size`
  move under the copy.
- Severity: medium — bounded (32 entries) and only hit while the diagnostics overlay queries history, but it is a real
  data race on every such tick.
- Fix: take `lock (r.History)` inside `History`, matching `AddHistory`. `_lock` is not needed for the queue itself.
- Owner: sibling agent — `Vision/TargetWatcher.cs`.

### 3. `TargetWatcher.Dispose` gives up joining a stuck worker, then disposes the Mats it may still be using
- `src/MacroStudio/Vision/TargetWatcher.cs:577–585` — `_stop = true; _wake.Set(); _thread?.Join(2000);` then under
  `_lock` every `r.Crop`/`r.Job`/`r.JobCtx` is disposed, `_monSig` mats disposed, `_wake` disposed.
- Scenario: the worker can legitimately block > 2 s inside a native `Cv2` capture/match call (screen grab on a busy
  box, huge template). The join times out, `Dispose` frees the very `Mat`s the worker is about to pass to OpenCV →
  use-after-free in native code (AV, heap corruption) rather than a managed exception. The follow-on `_wake.WaitOne`
  inside the worker is safely covered by the `catch (ObjectDisposedException)` at `:246`, and `_state` reads after
  dispose only return stale `WatchState`s — but the Mat lifetime race is not covered.
- Severity: medium-high — rare (only a genuinely stuck worker on shutdown), but the failure mode is a native crash.
- Fix: before touching mats, set a `volatile bool _disposed` the worker checks between steps (it already re-reads
  `_stop`); or have `Dispose` park the worker with the `_lock` held for the whole teardown *and* a short “abort
  in-flight work” flag so a post-join worker exits before `_rt` is cleared. At minimum, null-swap `r.Crop` under the
  lock and let the worker own disposal of anything it still references.
- Owner: sibling agent — `Vision/TargetWatcher.cs`.

### 4. `InputHooks.Dispose` can post `WM_QUIT` to a thread that has not published its id yet
- `src/MacroStudio/Native/InputHooks.cs:17` (`uint _threadId`), `:64` (assigned inside the thread proc),
  `:89–90` (`PostThreadMessage(_threadId, WM_QUIT)` then `_thread.Join(2000)`).
- Scenario: `Start()` returns as soon as `_thread.Start()` is issued; if `Dispose` runs before the hook thread
  reaches `GetCurrentThreadId()` (or the store is simply not yet visible — the field is not volatile), the posted
  `WM_QUIT` goes to thread 0, the join times out, and a background thread stays alive with low-level hooks installed
  for the rest of the process. The same window exists on restart: hooks would be installed twice.
- Severity: medium — shutdown/start quick-succession or thread starvation triggers it; the symptom is silent input
  capture continuing after “stop”.
- Fix: publish `_threadId` through a `volatile` field plus a `ManualResetEventSlim` set after it is assigned (and
  after `SetWindowsHookEx`), and wait on that event in `Dispose` before posting `WM_QUIT`.
- Owner: sibling agent — `Native/InputHooks.cs`.

### 5. `Recorder._events` is swapped at `Start` outside `_gate`
- `src/MacroStudio/Recording/Recorder.cs:25` (declaration), `:103–107` (`_events = new List<MacroEvent>(); _events.Add(...)`
  in `Start`), readers `Snapshot` `:65`, `SnapshotFrom` `:76–80` under `_gate`.
- Scenario: the field is a plain reference. A UI-tick `SnapshotFrom` racing `Start` can read the *previous*
  recording's list (stale reference visibility) — the live view briefly shows the old macro. Mutations of whichever
  list a reader does get are properly locked, so this is visibility, not tearing.
- Severity: medium-low — brief stale snapshot after pressing record, self-correcting on the next tick.
- Fix: assign `_events` inside `lock (_gate)` or mark it `volatile`.
- Owner: sibling agent — `Recording/Recorder.cs`.

### 6. `Recorder.Stop` publishes its private list into the returned `Macro`
- `src/MacroStudio/Recording/Recorder.cs:264–269` — `Events = stuck ? Snapshot() : _events`.
- Scenario: on the normal path the `Macro` shares the recorder's internal list object. It is safe today: the only
  writer (channel consumer) was joined, `Start` replaces the reference, and a late `TryWrite` to a completed channel
  returns false. It stays safe only while those three facts hold; any new writer, or a future code path that keeps
  mutating `_events` after `Stop`, corrupts the saved macro in place.
- Severity: medium-low — correct but fragile; a `Snapshot()` (or `List` copy) unconditionally is cheap insurance.
- Fix: always return `Snapshot()`; remove the `stuck` conditional cost optimization.
- Owner: sibling agent — `Recording/Recorder.cs`.

## Low

### 7. `Player` relies on volatile-pairing and dispatcher barriers for some cross-thread reads
- `src/MacroStudio/Playback/Player.cs:61` (`double _speed`, plain), `:95–96` (UI setter also sets volatile `_resync`),
  `:166–167` (read on the playback thread), `:70` (`Exception? Error`, plain, written `:203`, read by UI after finish).
- Scenario: `_speed` is published only because the setter happens to write volatile `_resync` after it — correct but
  fragile (reordering the two assignments breaks it silently). `Error`'s visibility depends on the dispatcher
  marshaling in `Finished` subscribers (`MainWindow.xaml.cs:461`). `Finished?.Invoke()` itself is raised on the
  playback thread (`:208`); current subscribers marshal correctly, but the type gives no hint they must.
- Severity: low — works today by accident of ordering, not by construction.
- Fix: mark `_speed` and `Error` volatile, and document on the event that handlers run on the playback thread.
- Owner: sibling agent — `Playback/Player.cs`.

### 8. `Recorder.Ignore` runs a UI-state-reading delegate on the hook thread
- `src/MacroStudio/Recording/Recorder.cs:118` (`Ignore?.Invoke(r)` inside the hook callback),
  `src/MacroStudio/MainWindow.xaml.cs:325` (`_recorder.Ignore = OverOwnWindow`),
  `src/MacroStudio/MainWindow.Diagnostics.cs:165–170` (reads `_visiblePlay`/`_recVisible`, plain bools, calls
  `WindowPlacement.Bounds(_hwnd)`).
- Scenario: hook-thread reads of non-volatile UI-owned bools can lag a toggle by an unbounded time; a click over the
  app's own window right after hide/show can be filtered the wrong way (one stray event recorded or dropped).
  The native `GetWindowRect` call itself is thread-safe.
- Severity: low — one stray event at a window-state edge.
- Fix: make the two flags `volatile bool`, or evaluate the filter on the consumer thread (it has the coordinates).
- Owner: sibling agent — `MainWindow.Diagnostics.cs` / `Recorder.cs`.

### 9. `MacroLibrary` file I/O is not serialized against playback-thread loads
- `src/MacroStudio/Storage/MacroLibrary.cs:81–91` (`MacroSerializer.Load`/`Save` under `_kids`-locked callers),
  `:175–188` (`File.Copy`/`Move`/`Delete`), `:229–234` (`PathOfId`/`LoadById`, callable from the playback thread via
  `FlowRunner`), caches `_kids`/`_ids`/`_idx` correctly locked/volatile.
- Scenario: the UI thread renames/deletes/saves a macro while a running `When` block loads it by id →
  `FileNotFound`/`IOException`/a torn read mid-write. Most playback-side paths already swallow exceptions
  (`LoadById` catches and returns null), so the blast radius is a skipped connected macro, not a crash.
- Severity: low — transient, mostly handled.
- Fix: write via temp-file + atomic `File.Move` replace in `MacroSerializer.Save`, and retry once in `LoadById`.
- Owner: sibling agent — `Storage/MacroSerializer.cs`, `Storage/MacroLibrary.cs`.

## Audited and found correct

- **`Recorder._events` mutation discipline** (`Recording/Recorder.cs:26,65,76–85,90–93`): `Store`/`TakeBack`/`Snapshot`/
  `SnapshotFrom` all hold `_gate`; `_epoch` bumps under the same lock, so the UI's incremental-view epoch check at
  `MainWindow.xaml.cs:108–115` can never observe “new events but old epoch”.
- **Hook → channel → consumer** (`Recorder.cs:109–116`): `TryWrite` is non-blocking on an unbounded channel declared
  `SingleWriter: false`, so hook, poller and any other producer can call it concurrently; `SingleReader` matches the
  one `await foreach` consumer. `Writer.Complete()` in `Stop` happens after the hooks are disposed, and a post-complete
  `TryWrite` fails cleanly.
- **`TargetWatcher._state` publication** (`Vision/TargetWatcher.cs:84,215,500`): `ConcurrentDictionary` of immutable
  `WatchState` records replaced wholesale per tick — `Get` readers on any thread see a whole state, never half-built.
  `Updated` (`:308`) is raised on the watcher thread outside `_lock`, and `TargetsPanel.OnWatcherUpdated`
  (`Views/TargetsPanel.cs:412–420`) coalesces with `Interlocked.Exchange` and marshals with `BeginInvoke` — no lock
  ordering issue, no unbounded queue.
- **`RawMouse`** (`Native/RawMouse.cs:49–50,84–87,117–122`): `_dx/_dy` are only ever touched under `_l`;
  `_lastAbsX/_lastAbsY` are confined to the raw-input thread.
- **`ExecutionLog`** (`Playback/ExecutionLog.cs:16–34`): one lock around a fixed ring; safe for the playback thread to
  write while the UI reads `Total`.
- **`MacroLibrary` caches** (`Storage/MacroLibrary.cs:48–56,201–212,224–231`): `_kids`/`_ids` under dedicated locks,
  `_idx` is a volatile never-mutated dictionary — readers get a consistent map.
- **`Player` held-keys state**: `Track`/`ReleaseAll` dictionaries live entirely on the playback thread.

## Fixed in `src/MacroStudio/Editing/EditSession.cs` (this audit's owned file)

- All event-array mutation (`Load`, `Append`, edits, `Undo`/`Redo`) now serializes on a private `_gate`; readers
  (`Events`, `Analysis`, `PosIndex`, `IndexOfId`, `LowerBound`, `Duration`, `PositionAt`, `PrimaryEvent`, `ToMacro`,
  `CanUndo`/`CanRedo`) take the same lock, so a racing view can no longer read an array while `Load` rebuilds it.
- The three lazy caches (`_analysis`, `_posIndex`, `_idMap`) were separate fields that could go stale independently:
  a reader could pair a new array with an old map. They are now published as single immutable cache records keyed on
  the exact array reference — a reader gets a consistent pair or rebuilds.
- `Append` queues its tail (`_tail`) and the merge into a published array happens once, under `_gate`, on the next
  read — concurrent `Append`/`Load` can interleave arbitrarily and every published array stays sorted with unique ids.
- Remaining documented surface: `Selected`, `Expanded`, `Primary`, `PlayheadMs` and the `SelectionChanged`/
  `PlayheadChanged` events stay UI-thread-owned (as the views always assumed); `IsBreakpoint` has its own lock for
  the playback thread.
