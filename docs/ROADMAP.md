# Macro Studio: adaptive automation, status and plan

Built in stages. **Stage A (done)** is the foundation for the Critical items: adaptive clicks, waiting, confidence, retries, fallback, recovery, plus the debugging and safety pieces that depend on them. **Stage B** (conditionals, loops, variables, application states, checkpoints) is not started.

## Architecture (the five systems)

| System | Where | Notes |
|---|---|---|
| Input Recorder | `Native/InputHooks.cs`, `Recording/Recorder.cs` | Low-level hooks on their own thread, queue to a consumer, timestamps from QPC. |
| Visual Recognition Engine | `Vision/*` | `Detector` (staged: verify / light / deep), `TargetWatcher` (shared scheduler, history, identity ranking), `FrameContext` (per-frame cache), `TargetStore`. Knows nothing about macros. |
| Macro Execution Engine | `Playback/*` | `Player` (timeline, pause, step, breakpoints), `StepExecutor` (adaptive steps), `AdaptingSink` (other PC), `ScreenTargetFinder` (reads the watcher), guards. Talks to vision only through `ITargetFinder`. |
| Debugging and Monitoring | `ExecutionLog`, `Monitoring/PerfMonitor.cs`, `MainWindow.Diagnostics.cs`, `Views/*` | Run log, detections, step debugger, performance line, overlays. |
| Macro Storage | `Model/*`, `Storage/*`, `Vision/TargetStore.cs` | JSON macros (versioned), targets as PNG + JSON in `%AppData%\MacroStudio\targets`. |

## Status against the requirements

Legend: **done**, *partial*, not yet.

### 1. Intelligent visual targeting
| Requirement | Status |
|---|---|
| Multiple targets | **done**: one shared scheduler, cost does not multiply per target |
| Occlusion tolerance | **done**: part voting + AKAZE features, tracked cheaply while covered |
| Dynamic repositioning | **done**: full search across monitors, tracking window, scale sweep |
| Confidence | **done**: shown in Live detect, Detections tab, debugger, run log; per-step minimum confidence |
| Target identity | *partial*: surroundings (24 px ring) saved at capture; identical-looking candidates are ranked by it. Needs the whole ring on screen at capture time; does not help when the surroundings also change |
| Target history | **done**: last 32 moves/losses per target (bounded) |
| Disappearance | **done**: debounced (not seen for 200 ms in a row), `LostUtc`, history entry |
| Groups | *partial*: groups organise the list, menus and Detections tab. No group-level operations yet (watch / wait-for-any) |
| Relative click position | **done**: anchor as a fraction of the target size from its centre (so it scales with the target), plus pixel offset. Anchor picker in the inspector; "Convert recorded click to adaptive target click" derives it from the recorded click |

### 2. Intelligent playback and recovery
| Requirement | Status |
|---|---|
| Pre-click verification | **done**: stable sighting (two separate detections agree within 3 px), last-moment re-check, minimum confidence |
| Wait until visible / gone | **done** (`WaitTarget`, `WaitGone`); gone is debounced |
| Retry, timeouts | **done**: per step, configurable |
| Fallback coordinates | **done**: only when the screen layout is unchanged since recording, otherwise refused with a clear error |
| Recovery actions | *partial*: press a key and/or click another target, then retry. No arbitrary recovery sequences yet |
| Post-click verification | **done**: target gone, or another target appears |
| Conditional actions | not yet (Stage B) |

### 3. Visual debugging and monitoring
| Requirement | Status |
|---|---|
| Detection bounding boxes | **done**: click-through overlay in Live detect |
| Detection history | **done**: Detections tab |
| Playback debugger | **done**: Step button, breakpoints (Ctrl+B), panel showing the next action, target searched, detection, confidence, click point, fallback safety, last result |
| Execution logs | **done**: ring of 2000 entries; ok / warn / retry / skipped / fallback / failed |
| Performance overlay | **done**: CPU, RAM, detection time and watcher share, playback lateness, with trend warnings |
| Live mouse trajectory | *partial*: the playhead follows the recorded path during playback; the pointer's actual path is not drawn |
| Detection events on the same timeline | not yet: detections appear in the log and Detections tab, not as timeline lanes |
| Frame inspection | not yet: no screenshots are stored (keeps memory low); a bounded failure-snapshot ring is the planned way |

### 4. Long-session performance
Done: event-driven scheduler that idles when nothing changes, adaptive rate (10 Hz normal, fast only while a macro waits), region-of-interest tracking, bounded log / history / performance rings, no screenshots stored, per-frame and per-template caches, detection on its own thread, CPU cap, trend warnings.
**Not proven:** nothing has been run for days. Short soak tests and measurements only. The recorded event list itself grows with recording length (a 1000 Hz mouse for hours is a very large list); a bounded recording mode is not built.

### 5. Advanced macro editor
Existing: undo/redo, duplicate, copy/paste, insert, reorder, bookmarks of a kind (breakpoints). Not yet (Stage B): grouping into reusable sequences, loops, conditional branches, variables, reusable action library, dependencies, timeline bookmarks, node-based workflow editor.

### 6. Application state recognition
Not yet (Stage B). Planned design: a state = a set of targets that must be visible (and optionally some that must not), matched with the existing watcher; `WaitState` / `IfState` steps and transitions on top of the same step executor.

### 7. Reliability and safety
| Requirement | Status |
|---|---|
| Emergency stop | **done**: F8 |
| User input detection | **done**, opt-in checkbox ("Pause if I use the mouse or keyboard"); first 1.5 s ignored, app hotkeys ignored, small nudges ignored |
| Display change detection | **done**: pauses on resolution, monitor, layout or scaling change |
| Multi-monitor / DPI | **done** (earlier work) |
| Detection failure recovery | **done**: retries, fallback, skip, recovery |
| Repeated click prevention | **done**: minimum interval per target spot |
| Confidence warnings | **done**: low margin and partial-match warnings in the log |
| Application monitoring (pause when the target app closes) | not yet |
| Progress saving, checkpoints, resume | not yet |
| Infinite loop detection | not yet (no loops exist yet) |

## Next stage (B), in order
1. Application state recognition (`WaitState`, `IfState`).
2. Conditional steps and loops (`If target visible / else / end`, `Repeat n`, `Repeat while visible`) with infinite-loop detection and a small variable store. This needs the player to move from a fixed timeline to a program counter with jumps; the existing timing tests must keep passing.
3. Checkpoints and resume; application monitoring.
4. Reusable sequences and the action library.
5. Node-based workflow editor on top of the same model.
