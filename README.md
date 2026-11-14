# Macro Studio

Record, inspect, edit and replay mouse and keyboard input on Windows. C# / WPF on .NET 10.

```
dotnet run --project src/MacroStudio
dotnet test
```

Hotkeys (work while any app has focus): **F9** record / stop, **F11** pause / resume, **F10** play / stop, **F8** stop everything.
Recording minimizes the window so its own clicks are not captured. Recordings are saved automatically to `%AppData%\MacroStudio\`.

## Views (all share one selection and playhead)
- **Path**: cursor path coloured early→late (blue→orange) with direction arrows, START/END, click markers (● press, ○ release), scroll arrows, playhead. Click to select, drag a selected point to change its coordinates, wheel to zoom, drag to pan, double-click to fit.
- **Timeline**: lanes for mouse speed, buttons, keys (overlapping keys get their own row) and scroll, with a time ruler. Click a ruler to scrub, drag a bar or tick to retime, rubber-band to multi-select, wheel to zoom.
- **Table**: every event with time, delay since previous, coordinates, key or button, scroll direction and raw delta, hold time and problems. Optionally hide mouse moves.
- **Inspector**: edit the selected event: type, time, delay (optionally rippling later events), X/Y, key (click and press a key), button, scroll direction / notches / raw units, hold duration.

Editing: undo/redo (Ctrl+Z / Ctrl+Y), delete, duplicate (Ctrl+D), copy/paste at the playhead, insert key/click/move/scroll at the playhead, reorder, shift selected events in time or space.

## File format
`*.macro.json`, version 1: an indented header plus one compact event per line. Event times are absolute milliseconds from the start. Wheel `delta` is raw wheel units (120 = one notch; positive = up/right). Import and Export use the same files.

## Limits
- Input into elevated (admin) windows is blocked by Windows unless Macro Studio is also run as administrator.
- Coordinates are physical pixels on the recorded desktop layout. Replay on a different monitor layout or scale will land elsewhere (the Issue column flags positions outside the recorded desktop).

## Visual targets (no training)
Register a button once, find it again anywhere on screen.
1. **Targets panel → ＋ Capture**: the app minimizes, freezes each monitor, and you drag a box around the button or icon. Name it. A match threshold is chosen automatically from how look-alike the rest of the screen is.
2. **Live detect** draws a box over every match on the real screen and lists its `x, y`, score and search time. Move the window or button and the box follows.
3. **Edit → Insert at playhead → Click target** adds a step that waits (default 10 s) for the target, then clicks its current position (optional offset, button, "skip if not found"). Replay no longer depends on where the button was when you recorded.

How it matches, cheapest first: (1) whole-image match for a fully visible target, with a DPI-aware scale sweep and a tracking window around the last hit; (2) **part voting** when something covers part of it: the target is cut into patches, a few spread-out seed patches vote for its position and every candidate is checked patch by patch, so covered patches are simply ignored; (3) **feature matching** (AKAZE keypoints + RANSAC) for textured targets that are also rotated or resized. No model, no dataset. Per target you can turn "Allow partly hidden" off or set the minimum visible share (default 40%).

Limits: exclusive-fullscreen games can capture black (use windowed/borderless); a target needs roughly 40% of its detailed area visible; very small or plain targets have too few keypoints for the feature stage (part voting still works for same-size targets); a fully hidden target is, of course, not found; semi-transparent or animated buttons and strong 3D perspective are not handled; macro files reference targets by id and do not bundle the images, so copy `%AppData%MacroStudio	argets` to move them to another PC.

### Many targets, low CPU
All targets are watched by one shared scheduler (`Vision/TargetWatcher.cs`) instead of one search per target per frame:
- **Shared work:** each captured frame is converted to gray and half size once, and its AKAZE keypoints are computed once, for every target.
- **Found targets are nearly free:** they are re-confirmed in a window of a few pixels around their last position (and skipped entirely if those pixels did not change), using a capture of just that window.
- **Absent targets are searched sparingly:** only on monitors whose pixels changed (checked with a cheap 1/8 sample), most-wanted first, with a growing pause between failed searches. The expensive stages (scale sweep, part voting, features) run one per tick, after the cheap search failed.
- **A CPU cap:** the watcher sleeps so it uses about 12% of one core at most while idle-watching (about 0.5% on a static screen, about 4% tracking one target), and runs fast (up to 60%) only while a macro is waiting for a target.
- OpenCV runs on one thread normally (its worker pool spin-waits, costing more CPU than it saves) and on several only while a macro waits.

Measured on an 8-target, 3840x1080 test desktop: finding all visible targets took ~260 ms of work in total (the previous per-frame approach took ~3 s *per tick*), and a static screen costs ~5 ms per tick at 1 tick/s.

## Adaptive steps (Stage A)
See [docs/ROADMAP.md](docs/ROADMAP.md) for the full status. In short:
- **Edit > Convert recorded click to adaptive target click...** turns a recorded click into one that finds a registered target and clicks the *same spot of it* (the anchor), wherever it is now.
- **Edit > Insert at playhead** adds *Click target*, *Wait until target is visible* and *Wait until target has disappeared* steps.
- In the inspector a click step has: the anchor picker, minimum confidence, retries, stable-detection check, minimum time between clicks, what to do if it still fails (stop / skip / click the recorded position, the last only while the screen layout is unchanged), a check after the click (target gone / another target appears) and recovery (press a key and/or click another target, then retry).
- Targets can have a **group**, and keep their surroundings so two identical-looking buttons can be told apart by what is next to them.
- **Run log** and **Detections** tabs show what happened and what is detected; **Ctrl+B** sets a breakpoint, **Step** runs one event at a time, and the panel above the log explains the next action. The status bar shows CPU, RAM and detection cost.
- Safety: *Pause if I use the mouse or keyboard* (opt-in), automatic pause on a display change, repeated-click prevention.

## Reliable detection and mouse path (update)
- **Slight colour changes no longer lose a target.** Matching now compares *structure* (brightness pattern), which a hover highlight, tint or small hue shift barely changes, and checks colour separately: the **Colour tolerance** box in the Targets panel (default 30 = slight change; 0 = identical colour; 250+ = ignore colour). A clearly different colour (red vs orange) is still a different target. A target that is already being tracked is kept even if its score dips by up to 0.10 (hover, antialiasing).
- **Moving targets are followed.** A target that jumps away is searched for at once (not after a back-off), the sizes tried first include where it was last matched and what each monitor's own scaling would give, a target dragged past a look-alike is no longer swapped for it, and editing a target's settings makes the search start over immediately.
- **Identical-looking buttons are reported as such.** When two matches cannot be told apart (and the saved surroundings do not settle it) the detection is marked **ambiguous** in the Detections tab and the run log warns before clicking.
- **Mouse path to an adaptive click** (per step, in the inspector: *How the mouse reaches the target*; plus the toolbar switch *Mouse path follows moved targets*):
  - *Exact recorded path, then jump*: the old behaviour.
  - *Bend the recorded path to the target's current position* (default for new steps): the recorded approach is replayed with the same start, shape and timing, bent so it ends on the target; unchanged if the target has not moved. A target that moved very far cannot be reached in the recorded time at a natural speed, so the bend is limited step by step and the cursor then glides the rest of the way.
  - *Glide straight to the target*: a smooth minimum-jerk movement from wherever the cursor is (duration from distance, or set it yourself).
  - The toolbar switch off = every step uses the exact recorded path. **The recording is never modified**: the bend and the glide exist only in the events sent to the mouse during playback.

## Partly hidden targets, optimized mouse path, visible playback

- **Partly hidden targets**: the target is cut into small, evenly spread patches; those still visible vote on where the whole target is. Found with 10-25% covered by anything, and half hidden from one side (tracked targets re-check immediately when something slides over them). The visible patches must span at least 30% of the target in both directions, and "Visible ≥" now defaults to 25%.
- **Optimized movement** (step option, default for new adaptive steps): the recorded approach is not replayed; the cursor goes straight from where it is to the target's current position (smooth start/stop, re-aimed continuously without jumps), then on to the next target the same way. The recording is never changed.
- **Keep this window visible while playing** (toolbar checkbox): the window is not minimised, never takes focus, is excluded from screen capture, lets replayed clicks pass through it, and using it does not trigger "pause if I use the mouse". Status bar and timeline show progress live.

## Paths on screen, landing accuracy, continuation, window placement

- **Show paths while playing** (on by default): the recorded path is drawn blue and dashed, the path the pointer really takes orange and semi-transparent (click-through, hidden from detection). The recording itself is never changed.
- **Landing accuracy**: after moving to a target the player reads where the pointer really is. If it is off (scaling, monitor layout, another monitor's resolution) it shifts the injected position by the error, up to 3 times, and logs how far it was off.
- **Continuation**: after a click on a relocated target, the recorded movements that follow are shifted by the difference between the real and the recorded click point, so they continue from where the cursor is, and the shift fades out along the path.
- **Keep this window visible**: the window moves to the monitor (or corner) with no recorded path or target, shrinking if needed (not below 480×340), re-checks every ~0.8 s against the real pointer path and live targets, and returns to its old place when playback ends.
