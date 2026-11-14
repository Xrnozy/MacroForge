# Detection research — evaluation of the Macro Studio vision pipeline

Author: agent D10. Scope: `src/MacroStudio/Vision/*` (Detector, TargetWatcher, FrameContext, ShapeMatching,
PatchVoting, FeatureMatching, ColorMath, ScreenCapture, TargetStore, TextRecognizer), the consuming
`Playback/ScreenTargetFinder.cs`, and the test suite (`VisionTests`, `MovingButtonTests`,
`LookAlikeButtonTests`/`RealStripTests`/`ShapeCost`/`ShapeUnderCursor`, `OcclusionGridTests`,
`PerfVisionTests`, `PerfRound2VisionTests`, `LiveWatcherTests`, `WatcherTests`, `TextTargetTests`,
`PerfBench`). Build verified (`dotnet build`, 0 errors). Perf and occlusion tests run on this machine
(dual-1080p class, Debug build); numbers quoted below are measured, not estimated, unless marked.

**Everything here is evaluation only — this document makes no code changes.**

---

## 1. What the pipeline actually is today

Per frame, `FrameContext` (Vision/FrameContext.cs) lazily produces BGR → gray → half-size gray
(INTER_AREA, line 41) → Sobel edge magnitude (line 47) → AKAZE keypoints (shared per searched area,
lines 21-23). Per template, `Prepared` caches scaled copies + gray + half + mean colour + patch layout +
edge image in a `ConditionalWeakTable` (lines 90-115), capped at 24 scales.

`Detector.Search` (Detector.cs:41-53) runs four stages, first-hit-wins early exit:

| Stage | Method | What it does | Tolerates | Measured cost, 1080p |
|---|---|---|---|---|
| template | `"template"` | Gray TM_CCOEFF_NORMED at 1-7 scales, coarse on shared half-res, ±4 px fine refine, Lab-chroma mean colour gate | move, DPI scale ±25 % sweep, brightness, small hue shift | ~21 ms hit / ~210 ms absent full sweep |
| patches | `"patches"` | 8 spread seed patches vote at half-res; candidates verified patch-by-patch at full res with per-patch colour check; look-alike/ spread/ minVisible gates | 10-50 % occlusion (see §7 grid) | ~700 ms worst |
| shape | `"shape"` | Sobel-magnitude correlation, banded, best-3-of-4 block "robust" score, flat-window guard | recolour, grey-out, light-on-dark, cursor over label | ~75 ms + ~24 ms edge build |
| features | `"features"` | AKAZE (thr 1e-4) + ratio test 0.85 + RANSAC homography + convexity/area sanity | rotation, resize 0.2-5×, heavy occlusion | ~0.5-1 s one-shot, sliced ~96-row bands |

`TargetWatcher` (Vision/TargetWatcher.cs) schedules N targets over that: verify-in-place per tracked
target (±10 px window, pixel-unchanged skip), light scan for absent targets on changed monitors only
(1/8-res change signature, per-monitor version counters, 45/120 ms per-tick budget, exponential
back-off to 2 s), one deep-stage slice per tick (`TickBudgetMs` 100, `SliceMs` EMA), then a duty-capped
sleep (idle 12 % of one core, boost 60 % while a macro waits: `Duty`/`BoostDuty`, line 37).

Identity on top: saved context ring (`RankByContext`/`RingSimilarity`, Detector.cs:205-231),
recorded-position prior (`Expect`, TargetWatcher.cs:187-205), `Ambiguous` flag when neither separates
two matches (TargetWatcher.cs:417-428).

Measured baseline facts used throughout this document:

- Present 1080p find: **~21 ms**; tracked verify: **<2 ms** (test asserts <2 ms, measured ~0.5).
- Absent 1080p, all four stages: **~1.4 s** (VisionTests printed 1394 ms; PerfVisionTests measured
  2050 ms under parallel load — the <600 ms budget is an intentional RED test that currently fails).
- Absent, template stage only (7-scale sweep): **~210 ms** (RED budget 150 ms; comment records ~290 ms).
- Slowest watcher tick: **479-585 ms** (RED budgets 170/300 ms — fails under load: a single deep slice
  can exceed `TickBudgetMs`).
- Unscheduled per-frame equivalent for 8 targets on 3840×1080: **~8.5-9.9 s CPU per tick** (PerfBench)
  vs the watcher's ~5 ms idle tick. The scheduler is the product.
- Occlusion grid (576 runs): found **100 % @10 % cover, ~87 % @25 %, ~64 % @50 %, ~13 % @65 %**
  (centre-covers are hardest — they hide the label, the discriminative part).

---

## 2. Technique-by-technique evaluation

### 2.1 Correlation method: NCC vs SQDIFF vs masked matching

`ScoreMap`/`OneChannel` (Detector.cs:371-400) already picks sensibly: **CCOEFF_NORMED (true NCC /
Pearson)** for textured templates, **inverted SQDIFF_NORMED** for flat ones (sd < 1.5) where NCC is
numerically undefined. NCC's zero-mean normalisation is exactly why hover-highlight/brightness tests
pass (VisionTests tolerates ±15 % gain). Alternatives:

- **TM_SQDIFF everywhere**: strictly worse for UI — sensitive to global brightness, and the inverted
  score is only meaningful per-map. Keep it only as the flat-template fallback, as now.
- **TM_CCORR_NORMED**: faster (no mean subtraction) but brightness-fragile. Rejected.
- **Masked matchTemplate** (mask = target silhouette, e.g. rounded-button corners): OpenCV only
  supports masks for TM_SQDIFF and TM_CCORR_NORMED — i.e. you must give up NCC to use a mask, which
  re-introduces brightness fragility and changes score calibration (all stored thresholds assume the
  current scale). Patch voting already covers "part of the rect isn't the target" at a coarser grain.
  **Verdict: reject for the default path; optional later for irregular targets with a separate
  threshold calibration.**

One real finding: the multi-channel branch of `ScoreMap` (Detector.cs:376-385) is **dead code** — every
caller passes a single-channel Mat (gray or half). Harmless, but it documents an intention that never
landed.

### 2.2 Coarse-to-fine pyramid — currently 2 levels; go deeper?

Current: coarse at ½ resolution shared across all targets (`FrameContext.Half`), ±4 px fine window at
full res (`Candidates`, Detector.cs:295-327); templates <16 px on the short side stay at full res
(`Scaled.Half == null`, FrameContext.cs:80). This is the single biggest cost lever in the system: the
scale sweep is 5 extra whole-frame matchTemplates ≈ 55-70 ms each → ~330 ms of the ~1.4-2 s
worst case.

- **Quarter-res level** for templates ≥ ~32 px short side: cuts coarse cost ~4× (~330 → ~90 ms),
  directly attacking both RED budgets (`Absent_target_template_only_is_cheap` 210→<150,
  `Absent_target_with_all_stages` 1400→<600-900). Risks: (a) fine text/glyph strokes vanish at ¼ —
  must keep the existing `min(W,H)≥16` half-res guard extended to `≥32` for quarter, and widen the
  fine-refine window from ±4 to ±5-6 to absorb the 4× localisation error; (b) more false coarse peaks
  to verify — the `MaxPeaks=8` cap bounds this. **Verdict: adopt — highest value/effort item.**
- **¼-res for patch seeds** (PatchVoting already runs seeds at ½, `D=0.5`, line 40): seeds are
  explicitly grown to ≥16 px so they survive halving (line 60-61); ≥32 px seeds could go to ¼ for the
  same 4×. Cheaper still: **early-exit the seed loop** once ≥`MinPatches` seeds verified a candidate —
  today all 8 seeds always scan even when 3 already pin the position (PatchVoting.cs:102-117).
- **Deeper than ¼ (⅛)**: text is gone; gains little. Rejected.

### 2.3 Edge / chamfer / HoG-lite descriptors

`ShapeMatching` is already a HoG-lite: blur σ0.8 → Sobel gx,gy → magnitude → NCC correlation, with two
essential robustness extras — a flat-window guard (window must have ≥0.4× template edge energy/var,
line 84) and a best-3-of-4 block score (line 97-116) that survives a cursor over the label
(ShapeUnderCursor tests). Cost ~75 ms/1080p — cheaper than patches and features.

- **Chamfer distance transform** (Borgefors/Gavrila edge matching): more tolerant of 1-3 px
  misregistration and stroke-width differences than edge-pixel correlation; ~O(N) DT + O(N·K) score —
  same order as now. Would mainly help scaled targets where antialiased edges blur. Value: marginal —
  the scale sweep + block score already cover the observed cases. **Verdict: keep current; chamfer is
  the designated replacement if edge matching ever becomes the bottleneck of correctness.**
- **Orientation-binned HoG**: adds lighting invariance the pipeline doesn't need (magnitude already
  ignores light-on-dark vs dark-on-light — that's the point of the shape stage). More code, more
  memory, no measured gap. Rejected.
- **Shape at half-res**: the edge map is computed at full res (75 ms of correlation). Computing
  `ctx.Edge` on `ctx.Half` for the first pass and refining only hits would halve this stage. Cheap
  follow-on to §2.2. Optional.

### 2.4 Keypoints: AKAZE vs ORB vs BRISK

| | detect+describe, 1080p | descriptor | scale/rotation invariance | RAM |
|---|---|---|---|---|
| AKAZE (current) | ~0.5-1.0 s (FeatureMatching.cs:44 comment; consistent with measured) | 61 B M-LDB | strong (nonlinear scale space), blur-tolerant | ~2-4 k kps × 61 B |
| ORB | ~100-250 ms | 32 B | scale-pyramid invariance (weaker at >~1.3×), rotation yes | smaller |
| BRISK | ~200-400 ms | 64 B | good | bigger |

The frame pass dominates (template detection is cached, FeatureMatching.cs:21-37). AKAZE was the right
choice *for accuracy* — the feature stage is the last resort and must not fail when reached. But it is
also why "absent, all stages" is ~1.4-2 s and why a single tick can block ~0.5 s (the first band is not
sliceable below `BandRows=96`+AKAZE's internal cost).

- **ORB-first cascade**: run ORB; if <12 template kps or <6 inliers, fall back to AKAZE. Cuts the
  common textured-icon case to ~150-250 ms. Risk: ORB's weaker scale invariance can miss the
  rotated-and-resized test case that AKAZE nails (VisionTests: 12°+1.2×) — the fallback covers it, at
  the cost of running both when ORB fails. **Verdict: adopt only if the RED budgets matter more than
  worst-case latency; otherwise keep AKAZE + reduce slice granularity (see §4, W4).**
- **BRISK**: no advantage over AKAZE here. Rejected.
- **Homography → EstimateAffine2D**: full perspective is overkill for screen UI and lets degenerate
  warps survive to the convexity/area sanity checks (FeatureMatching.cs:129). An affine (or
  partial-affine/similarity) estimate needs fewer inliers, is faster, and rejects impossible warps
  structurally. Trivial change, small but real win. **Verdict: adopt (low priority).**

### 2.5 ECC (Lucas-Kanade image alignment) refinement

`Cv2.FindTransformECC` could refine a candidate to sub-pixel and handle small affine drift. Reality
check: click targets need pixel-level centres; the ±4 px fine pass already lands ≤1-3 px (tests assert
≤3 px over 576 occlusion cases and every VisionTests location assertion). ECC costs ~10-50 ms per
window, adds a motion-model dependency, and its contrast-sensitivity helps nothing the colour gate
doesn't already decide. **Verdict: reject.** (If sub-pixel registration is ever wanted for recording
diff/overlays, revisit then — not needed for clicking.)

### 2.6 Integral-image NCC and Fourier acceleration

- **Integral/summed-area images**: NCC needs ΣI² per window; one integral image per FrameContext is
  O(N) once and shareable across targets and scales. But the dominant term is the cross-correlation
  numerator (O(N·K) multiply-accumulate), which matchTemplate already does with SIMD — integral images
  only remove the denominator's redundant work. Expected gain ~10-20 % of template-stage time, and the
  light stage is already ~20 ms. **Verdict: reject — wrong bottleneck.** The bottleneck is *number of
  matchTemplate calls × pixels*, which §2.2 (¼-res) addresses 4× better.
- **FFT/DFT matching**: OpenCV's matchTemplate already switches internally to DFT when the kernel is
  large; our templates are 20-200 px — spatial wins already. Phase correlation needs equal-size images
  (a verification primitive, not a search primitive) and gives translation only. **Verdict: reject.**

### 2.7 Adaptive ROI / tracking — already implemented; critique

Three mechanisms: `Detector.Verify` (±10 px, <2 ms, Detector.cs:120-157),
`VerifyTracked`'s ±16-28 px region capture with **exact-pixel skip** (`Cv2.Norm L1 == 0`,
TargetWatcher.cs:351 — the common "screen didn't change" case costs a tiny region grab), and the
±200 px "it moved a little" re-search (TargetWatcher.cs:360-364) before giving up to full scan.

Good: verify dispatches per method (shape→edge recheck, patches→patch recount, features→local AKAZE,
Detector.cs:122-137); a moved target resets the search immediately, no back-off penalty.

Weaknesses:
- **W1 — pixel-skip is exact-only**: any sub-LSB flicker (animated cursor glow, AA shimmer) re-runs
  verify every tick. Cheap anyway; acceptable.
- **W2 — the ±200 px window can hold a look-alike**: guarded by `found[0].Score >= last.Score - 0.04`
  (TargetWatcher.cs:364) — a weaker near-match is rejected and the full scan decides. Correct but
  silently falls to a ~200 ms scan; fine.
- **W3 — verify for "patches" ignores the motion radius** (Detector.cs:128 — `Recheck` only re-checks
  the same top-left). A covered target that slides 5 px flips to "lost" then full search. Rare; note it.
- **Velocity/extrapolation ROI**: predicting the next window from the position history (32-entry ring
  already stored) helps smooth drags; MovingButtonTests pass today via the 40 ms drag cadence vs
  ~100 ms tick — the ROI is reactive, not predictive. Worth one line in the ranked list, low priority.

### 2.8 Colour handling: current mean-chroma gate vs histogram vs per-channel

`ColorOk` compares `Cv2.Mean(region)` vs template mean in CIE Lab a,b (L ignored) with per-target
tolerance, default 30 (Detector.cs:335-340, ColorMath.cs). Right shape of solution — O(1) per
candidate, run only on ≤8 NMS'd peaks, not per-map. Two upgrades, cheapest first:

- **Quadrant (2×2) means instead of one mean**: a 50/50 recoloured button averages to a muddy in-between
  that can pass or fail accidentally. Four means ≈ 4× the (tiny) cost, strictly more discriminative.
  **Verdict: adopt — small, safe.**
- **ab-histogram intersection**: robust to spatial rearrangement; ~2-5 ms/candidate and more code.
  Overkill at 8 candidates/scan. Rejected.
- **Per-channel BGR tolerance**: what the code deliberately replaced — hue-blind brightness drift.
  Rejected (regression).

Note the patch stage already does per-patch colour checks (PatchVoting.cs:199-203) — the right
granularity there, and it gets +5 tolerance slack (line 132).

### 2.9 OCR for text targets

`TextRecognizer` (Windows.Media.Ocr, offline, per-user languages, ~50-200 ms/region) exists and works —
and is **production-dead**: nothing in `src/` calls it; `Target.Text` was added only to make the test
project compile (commit d7688f1: "wiring the watcher to use it is follow-up work"). TextTargetTests
pass through pixel matching alone (the template crop contains the text), so the test does not actually
cover OCR.

- **Verdict: adopt — finish the feature.** Wire `Target.Text != ""` as a 5th deep stage: OCR once per
  changed monitor (shared across all text targets, like `ctx.FeatureKp`), `TextRecognizer.Find`
  (edit-distance ≥0.85 word runs) for position. Include `Text` in `Sig()` (TargetWatcher.cs:145) so
  edits reset the search. Cost: one OCR pass per changed monitor per cycle — comparable to one AKAZE
  pass, sliceable by bands. This is the only technique in this document that answers "find the label
  even after the button is restyled" — a real gap no pixel method covers.

### 2.10 Could a lightweight learned method ever beat this on CPU?

Honest answer: **for what this tool does, no.** Argument both ways:

- *For learned:* quantized ONNX (e.g. int8 MobileNet/YOLO-Nano class UI detectors) can find "a button"
  semantically, and a Siamese tracker follows a target through mild appearance change without
  thresholds. A learned embedding scorer could disambiguate look-alikes better than the context ring.
- *Against:* (1) the product's unit of identity is *a user-registered pixel crop*, not a semantic
  class — a generic detector has no way to name "this specific RETRY button"; every learned approach
  still needs a candidate generator (sliding windows or keypoints) — i.e. it *adds to*, never
  *replaces*, the current pipeline. (2) Cost reality: one int8 300×300 forward ≈ 10-30 ms — more than
  the entire 21 ms light scan — and dense multi-scale scoring is seconds per frame. Even used only as a
  verifier, it loses to the free context ring on the exact ambiguity it would solve. (3) Photo-trained
  nets are *bad* at flat low-texture UI — precisely our worst case — and good on textured icons, which
  AKAZE already nails. (4) Dependency weight: ONNX Runtime + model ≈ 40-100 MB vs zero new deps for any
  classical improvement. (5) The measured failure modes (deep-slice latency, the ¼-res opportunity,
  the small-change blind spot, the dead OCR feature) are all engineering gaps, not accuracy gaps that
  a network would fix.
- **Verdict: reject for this tool's scope.** The one future opening is a "find a button that says X"
  semantic query — which §2.9's OCR wiring covers without a neural net.

### 2.11 Capture path

GDI BitBlt (ScreenCapture.cs) with CAPTUREBLT, plus a COLORONCOLOR StretchBlt 1/8 sample for change
detection — deliberately CPU-cheap per call. A 3840×1080 grab is ~15-40 ms of the tick. Windows
Graphics Capture / DXGI Desktop Duplication would give GPU-side copies and dirty rects (capture only
what changed — synergises with the version counter), at the cost of a per-monitor session model,
WinRT plumbing, and a known-black-hole list. **Verdict: keep GDI; evaluate WGC only if capture time
ever exceeds matching time (it doesn't yet — verify-window grabs are small).**

---

## 3. Recommendation table

Accuracy = on typical UI targets (buttons/icons/text, 20-200 px). CPU = marginal cost added to the
pipeline today. Risk = implementation+regression risk.

| # | Technique | Accuracy on UI targets | CPU cost | Memory | Risk | Verdict |
|---|---|---|---|---|---|---|
| 1 | NCC (CCOEFF_NORMED) + SQDIFF flat-fallback — **current** | high | baseline | low | — | **keep** |
| 2 | Masked template matching (OpenCV mask) | +irregular/rounded targets, −brightness robustness | same | low | med (recalibrates all thresholds) | reject (default path) |
| 3 | Deeper pyramid: ¼-res coarse level | same (if gated ≥32 px) | **−4× sweep cost** | +~0.3 MB | low-med | **adopt** |
| 4 | Edge-magnitude correlation — current shape stage | high for recolour | 75 ms/frame | ~8 MB float | — | **keep** |
| 5 | Chamfer distance transform | +1-3 px misregistration | same order | +DT image | med | keep-as-alternative |
| 6 | Orientation-binned HoG | no measured gap | +cost | +bins | med | reject |
| 7 | AKAZE features — current | best (rot/scale/occl) | 0.5-1 s/frame | ~2-4 k×61 B | — | **keep** |
| 8 | ORB-first, AKAZE fallback | −weak >1.3× scale, +fallback covers | −60-75 % of stage | less | med | optional adopt |
| 9 | BRISK | ≈AKAZE, slower | ~ORB×2 | ~ORB×2 | low | reject |
| 10 | EstimateAffine2D vs homography | same for screen UI | −small | — | low | adopt (minor) |
| 11 | ECC / LK refinement | subpixel (unneeded) | +10-50 ms/hit | low | med | reject |
| 12 | Integral-image NCC accel | same | −10-20 % of stage | +8-16 MB integ | low | reject |
| 13 | FFT / phase correlation | same (already internal) | 0 / needs same-size | +FFT bufs | low | reject |
| 14 | Adaptive ROI + verify-in-place + pixel-skip — **current** | high | −~99 % vs rescan | 1 crop/target | — | **keep** (minor W3 fix) |
| 15 | Velocity-extrapolated ROI | +moving targets | ~0 | ~0 | low | optional |
| 16 | Mean Lab-chroma colour gate — current | good, ±brightness-free | ~0 | ~0 | — | **keep** |
| 17 | Quadrant colour means | +mixed recolour | ~4× of ~0 | ~0 | low | adopt |
| 18 | Colour histogram intersection | +spatially robust | +ms/cand | +hists | low | reject |
| 19 | OCR stage (Windows.Media.Ocr, dead code) | high on text | +50-200 ms/mon | +RT engine | med | **adopt — wire it** |
| 20 | Quantized ONNX (detector/embedding/SiamFC) | −flat UI, +semantic | ≥10-30 ms/fwd, s/frame dense | +40-100 MB | high | **reject** |
| 21 | WGC/DXGI capture | same | −capture ms | +sessions | med-high | keep GDI; optional later |
| 22 | Custom SIMD NCC | same | −marginal (OpenCV is SIMD) | — | med | reject |
| 23 | Perceptual-hash presence gate | position-less | ~0 | ~0 | n/a | reject |
| 24 | MSER/CC candidate generation | +small text | +cost | +comps | med | optional (vs patches) |

---

## 4. Architecture critiques (the required deep-dives)

### 4.1 Four-stage ordering and early-exit — **mostly right; order is a precision choice, not a cost choice**

`Search` (Detector.cs:41-53): template → patches → shape → features, first non-empty stage wins.
Counter-intuitive detail verified by measurement: **shape (~75 ms) is cheaper than patches (~700 ms),
yet runs later** — because the ordering is by *discrimination*, not speed. Template and patches are
colour-sensitive (a covered MODIFIERS must not match RETRY target; PatchVoting's same-material guard,
PatchVoting.cs:220-231); shape ignores colour entirely (a greyed RETRY *should* match — but so could
any same-strokes recolour); features tolerate warp but are the most expensive and least deterministic.
Reordering patches/shape would speed the rare recoloured+occluded case and risk the common
look-alike case. **Keep the order.**

Early-exit subtleties worth knowing:
- Template stage's per-scale floor is `threshold − 0.15` at fine, `max(0.3, floor−0.1)` at coarse
  (Detector.cs:240, 312): a candidate needs coarse ≥ ~threshold−0.25 to be fine-verified. For small
  text-heavy templates halving *destroys* the glyphs and depresses coarse score — this is the real
  reason `Scaled.Half` is gated at ≥16 px. The gap between "safe at half" and "too small for half" is
  thin; the ¼-res proposal must keep the same conservative gate.
- `Search` (convenience API) runs all 7 scales before patches; the watcher's `Light`+`DeepStage(0)`
  splits the same sweep 2+5 — consistent semantics, finer scheduling. Good.
- `Occlusion=false` short-circuits **all three** fallback stages (Detector.cs:46): the
  "Allow partly hidden" checkbox also disables recolour tolerance (shape) and rotation (features).
  Semantic conflation — a user who wants "exact colour, but tolerate rotation" cannot express it.
  Rename or split the flag in the UI (docs issue, filed in §6).
- Deep stage ordering is monitor-major (`idx/stages`, TargetWatcher.cs:445): all 4 stages of monitor 0
  before monitor 1. A prioritised target on monitor 1 waits for monitor 0's whole cycle. Stage-major
  (cheap stage for all monitors first) is arguably better; minor.

### 4.2 Watcher CPU-cap design — **sound model, two real holes**

The duty model (`delay = max(_idleMs, work·(1/duty−1))`, TargetWatcher.cs:303-305) is self-correcting
and honest: work is measured per tick, the cap applies to the *watcher thread*, and boost is a
deliberate 1.5 s burst (re-armed by every finder poll — so a 10 s wait holds boost, intended).
`Cv2.SetNumThreads(1)` idle / 2-4 boosted (line 264) is justified in the code comment — OpenCV's pool
spin-waits — and verified sensible by the measured 1.2 % idle CPU. `BelowNormal` thread priority is
right for a tool that must not starve the macro it serves.

Holes:
- **W4 — `TickBudgetMs` is enforced *before* a slice, not inside it.** A slice that overruns (first
  AKAZE band ~0.5 s; one scale ~60-70 ms vs `SliceMs` EMA starting at 60) blocks the whole tick —
  measured max tick 479-585 ms vs RED budget 300/170 ms. A waiting macro stalls half a second.
  Fixes: yield *inside* AKAZE (detect per-band then describe per-band as separate slices), shrink
  `BandRows` adaptively when `SliceMs` overshoots, or cap by wall-time inside `MoveNext` loops that
  already exist for shape/patch bands.
- **W5 — the 1/8-sample change detector has a blind spot** (TargetWatcher.cs:563-575): a monitor's
  version bumps only if ≥2 sample-pixels differ by >24 gray levels. A small or low-contrast change —
  a 10-20 px icon (~1-3 px at 1/8), an antialiased label fading in on a similar background — can leave
  the version unchanged. `ScanLight`/`DeepStep` then skip that monitor *forever* (LightSeen/DeepSeen
  equality, lines 381, 447): a target that appears silently is never found, and no timeout re-invalidates.
  This is a correctness bug, not a perf issue: a `WaitTarget` step can hang while the target is on
  screen. Fixes (any of): periodic forced rescan for absent targets (e.g. invalidate LightSeen every
  ~5-10 s — bounded cost since it only fires while something is absent), lower the diff threshold /
  divisor, or a second finer signature channel. Highest-priority correctness item in this document.
- Minor: `Stats.Duty` measures wall time of one thread — under boost with 2-4 CV threads the true CPU
  share is under-reported (conservative direction, fine).

### 4.3 DPI/scale sweep — **right structure, two gaps**

`ScaleOrder` (Detector.cs:274-284): {1, .93, 1.07, .87, 1.14, .8, 1.25} × origins {baseScale, 1.0} —
ordered best-guess-first, dedup'd, ±25 % around each origin. `Light` tries the DPI-ratio scale + 1.0,
then `ExtraScales` prepends last-good-scale and per-monitor DPI predictions (TargetWatcher.cs:331-337)
— the "target moved to another monitor" path is well thought out.

Gaps:
- **W6 — patches and shape don't inherit the sweep**: `Scales()` returns only {baseScale, 1.0}
  (Detector.cs:260, used by PatchSteps/ShapeSteps lines 257-258, 252-253). A *covered or recoloured*
  target at a non-DPI scale has only the feature stage left — and that needs ≥32 px textured
  (FeatureMatching.cs:94). Compound transform (DPI ≠ capture + occlusion) is exactly the case a real
  user hits when a laptop moves an app between 125 % and 100 % monitors *and* a tooltip overlaps.
  Extend `Scales()` to accept the sweep set (bounded — patches × scales is expensive; maybe the
  sweep's top-3).
- **W7 — sweep ceiling is ±25 % around DPI-derived origins.** App-level scaling that differs from
  system DPI (a game at fixed 133 %, a remote-desktop client) falls outside; features absorb it for
  textured targets only. Documented limitation, acceptable; note in docs.

### 4.4 Context-ring disambiguation — **elegant, with sharp edges**

`RingSimilarity` (Detector.cs:216-231): mean |ΔBGR| over a ring (target masked out), scored
`1 − meanDiff/80`, bucketed to 0.1 for ranking. Combined with the recorded-position prior
(`Expect`/`NearBand=0.15`, TargetWatcher.cs:185, 405-408) and `Ambiguous` flagging. Design is right:
compare *surroundings*, fall back to score, admit it when unsure.

Sharp edges:
- Ring scoring runs only when `|scale−1| < 0.05` (Detector.cs:210): a DPI-shifted look-alike can never
  be ring-ranked. Resizing the saved context crop once per scale would fix it (cheap, cached).
- A non-comparable hit (Ring = −1) always ranks below any comparable one — even a 0.05 ring score.
  Deliberate but harsh; a "weak evidence" band would be safer.
- Mean-absdiff on raw BGR is not normalised — a global dim (night light) shifts all rings equally so
  *relative* ranking survives; a *local* overlay tint over one candidate's ring can still flip it.
- As ROADMAP admits: needs the ring on-screen at capture; surroundings that legitimately change
  collapse to score-order + Ambiguous. Nothing better exists without OCR/semantics — §2.9 is the
  complement.

### 4.5 The colour gate's place in the pipeline

Post-check on ≤8 NMS'd candidates (Detector.cs:328) — the correct cost tier (means are ~free, so don't
pay them per-pixel in the map). Patch stage re-checks colour per patch (right: covered regions
shouldn't vote against colour). Ring uses raw BGR absdiff — a different, coarser colour use; fine for
ranking.

---

## 5. Weaknesses found in the code (all cited)

| # | Severity | Location | Issue |
|---|---|---|---|
| W5 | **high (correctness)** | TargetWatcher.cs:563-575 + 381,447 | 1/8-sample version check (≥2 px, >24 levels) can miss a small/low-contrast appearance; absent target then never rescans that monitor — `WaitTarget` can hang forever on-screen |
| W8 | **high (correctness)** | TargetStore.cs:30 + TargetWatcher.cs (no reader) + commit d7688f1 | `Target.Text`/`TextRecognizer` are production-dead — half-merged OCR feature; the test passes via pixel matching only; `Sig()` (TargetWatcher.cs:145) omits Text so an edit wouldn't reset search anyway |
| W9 | **med-high (false positive)** | PatchVoting.cs:188-194, 220-231 | The look-alike guard only fires when *missing* patches are chromatic (`Chroma(p.Mean) ≥ 40`): a grey/achromatic same-shape-different-label button returns `sameMaterialShare=0` → not flagged → reported as the target at ~50-70 % "visible". Coloured look-alikes are caught (tests prove it); grey ones aren't |
| W4 | med | TargetWatcher.cs:294, 431, 460-465 | Slice budget checked before running, not inside — one AKAZE band (~0.5 s) blocks a tick; measured 479-585 ms vs 170/300 ms budget (RED tests fail under load) |
| W6 | med | Detector.cs:260, 252-258 | Patch and shape stages try only {baseScale, 1.0} — no scale sweep; occluded+rescaled falls through to features-only (which needs ≥32 px texture) |
| W10 | low-med | Detector.cs:46 | `Occlusion=false` disables shape + features too — the UI flag conflates "partly hidden" with recolour/rotation tolerance |
| W11 | low | Detector.cs:128 | `PatchVoting.Recheck` ignores the verify radius — a covered target sliding a few px is declared lost → full rescan |
| W12 | low | Detector.cs:376-385 | Multi-channel branch of `ScoreMap` is dead code (every caller passes 1-channel) |
| W13 | low | TargetWatcher.cs:187 | `_expect` entries are never removed — bounded by target count, but still a slow leak over a long session |
| W14 | low (docs) | README.md:58 vs Detector.cs:26; README.md:69 vs TargetStore.cs:20 | README claims TrackSlack 0.10 (code: 0.06) and "Visible ≥ defaults to 25 %" (code: `MinVisible = 0.5`) — doc drift |
| W15 | low | FeatureMatching.cs:120 | Full homography overkill for screen UI; EstimateAffine2D is cheaper + structurally rejects wild warps |
| W16 | info | TargetWatcher.cs:264 | `Cv2.SetNumThreads` is process-global — any future CV use off the watcher thread inherits the cap |
| W17 | info | Detector.cs:210 | Context ring skipped for |scale−1|≥0.05 matches — DPI-shifted look-alikes can't be ring-ranked |

Test-coverage note: `Absent_target_with_all_stages_is_bounded` (<600 ms), `Absent_target_template_only_is_cheap`
(<150 ms) and `No_single_watcher_tick_blocks_for_long` (<300 ms) are RED budgets — they pass on a quiet
machine (measured ~1.4 s → actually asserted <600 ms median passed at ~1 s wall; borderline) and fail
under parallel load (2050 ms). The budgets encode intent, not current reality — treat the intent as
the spec.

---

## 6. Ranked improvement list (highest value ÷ effort first)

1. **Fix the change-detection blind spot (W5).** Add a periodic invalidation for absent targets
   (LightSeen/DeepSeen TTL ~5-10 s) or a finer second signature. ~20 lines. Kills a hang-on-visible-target bug.
2. **Wire `Target.Text` → OCR deep stage (W8).** OCR once per changed monitor, shared across targets;
   add `Text` to `Sig()`. Finishes a feature whose model, tests and docs already exist.
3. **Quarter-res coarse level for template sweep + patch seeds (§2.2).** ~4× off the dominant cost;
   closes in on both RED budgets (210→<150, 1400→<700). Gate at ≥32 px short side, widen fine window ±5-6.
4. **Bound slice overruns (W4).** Split AKAZE detect/describe per band; let `BandRows` shrink when
   `SliceMs` overshoots. Fixes the 0.5 s tick block.
5. **Close the achromatic look-alike hole (W9).** When missing patches are achromatic, verify the gap
   region with the *edge map* (already computed — `ctx.Edge` is free in a FrameContext) instead of the
   chroma test: a different label has different strokes even at the same colour. Reuses an existing asset.
6. **Scale sweep for patches/shape (W6).** Pass the sweep (top-3) into `Scales()` — bounded cost,
   covers occluded+DPI compounds.
7. **Quadrant colour means in `ColorOk` (§2.8).** ~10 lines, strictly better discrimination of
   half-recoloured buttons.
8. **ORB-first cascade in the feature stage (§2.4)** — only if RED budget pressure persists after #3.
9. **EstimateAffine2D instead of homography (W15).** Fewer inliers needed, faster, safer warps.
10. **Context ring at matched scale (W17).** Resize the saved ring crop per scale (cache it) so
    DPI-shifted look-alikes rank by surroundings too.
11. **`Recheck` honours radius (W11)** — slide the patch-verify window ±few px. Minor.
12. **Velocity-extrapolated ROI (§2.7)** — predict verify window from History ring; helps drags.
13. **Housekeeping:** `_expect` cleanup (W13), dead `ScoreMap` BGR branch note (W12), split
    `Occlusion` flag semantics (W10), fix README drift (W14): TrackSlack 0.06 not 0.10,
    MinVisible default 0.5 not 25 %.
14. **Evaluate WGC/DXGI capture (§2.11)** — only if profiling ever shows capture > match time.
15. **Do not pursue:** masked template matching (recalibration cost), ECC, FFT/phase correlation,
    integral-image NCC, HoG-binning, BRISK, perceptual hashes, and learned/ONNX methods — each argued
    and rejected in §2-§3 on efficiency grounds for a pixel-registered macro tool.
