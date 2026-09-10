# PoChopAudio — architecture

Recordings in, one WAV per take out. Point it at files holding five repetitions of a sound
separated by pauses and it returns five clips each, trimmed to the start and end of every take.

Governance lived in `NET_RULES.md`, now deleted (`git show f185bd3:NET_RULES.md` for the text);
this file records what was actually built.

> **PoChopAudio is a WinUI 3 desktop app.** The ASP.NET Minimal API (`PoChopAudio.API`) and the
> Blazor WASM client (`PoChopAudio.Client`) were deleted: everything runs in-process on the user's
> machine and nothing is uploaded. Where an old HTTP route is mentioned it is named as history, to
> explain a shape the code still has — the DSP behind it moved into `PoChopAudio.Services`
> unchanged. Nothing below describes a call the app makes today.

## Shape

```
src/PoChopAudio.Services/     Host-agnostic engine room. No UI, no hosting framework.
  Outcome.cs                  Value-or-domain-reason return type used by both services
  ExportedFile.cs             Bytes + filename + content type, ready to save
  Chop/ChopService            The whole chop feature: upload, analyze, clip, zip, delete
  Chop/AudioDecoder           Decode to canonical WAV; MediaFoundation codecs are Windows-only
  Chop/SegmentDetector        The split algorithm (gate sweep, widest agreeing band)
  Chop/SilenceTrimmer         Leading/trailing silence trim, in frames
  Chop/ClipExporter           WAV slicing, UniqueStems, two-pass normalized export, ZIP
  Chop/LoudnessMeter          ITU-R BS.1770-4 K-weighting + gated integrated loudness
  Dsp/Fft                     Radix-2 FFT and a Hann window; no numerics dependency
  Dsp/Spectrogram             Log-spaced, fixed-window time/frequency grid for drawing
  Dsp/CueSynth                Count-in, chimes and a -18 dBFS reference tone, as samples
  Dsp/ParticleField           Fixed-capacity particle simulation, pure and frame-rate independent
  Chop/ClipProcessor          Export maths: gain decision and fade curve, no I/O
  Chop/ChopJobStore           Temp-dir job scratch space, lock-file liveness (no TTL)
  Cutout/CutoutService        CutOutAsync: decode, mask, clean, head-crop, encode — one call
  Cutout/ImageDecoder         JPEG/PNG/WebP decode, EXIF auto-rotate, MP.jpg trailer strip
  Cutout/EdgeProcessor        Mask threshold, morphology, feather, alpha multiplier
  Cutout/HeadFinder           Crops to the head alone: peak, neck, shoulder flare
  Cutout/Engines/OnnxU2NetRemover  u2netp ONNX model, in-process
  Cutout/CutoutModelOptions   Injected path to u2netp.onnx — why the engine needs no host
  Chop/ChopContracts          JobId, ChopLimits, ChopOptions, ChopSegment, Upload/AnalysisResult
  Chop/ExportContracts        NormalizeMode, ExportLimits, ExportOptions
  Cutout/CutoutContracts      CutoutOptions
  Cutout/CutoutLimits         CutoutLimits, CutoutEngine
  Cutout/IBackgroundRemover   The one remover interface, so EnginePicker can report it missing
  Cutout/IFaceLocator         Optional platform face detection; no implementation lives here
src/PoChopAudio.WinUI/        The app. Unpackaged, self-contained Windows App SDK.
  App.xaml.cs                 DI registration — the same singletons the API used to register
  MainWindow.xaml             NavigationView shell: Chop Audio, Cutout Studio, Settings
  Views/CutoutPage            Camera viewfinder, TAKE PHOTO, and the cut-out results below it
  Views/SettingsPage          Appearance, saving, storage, capability report, about
  Services/AppSettingsService JSON settings under %LOCALAPPDATA%; the only state that outlives a run
  Services/DiagnosticsReport  Probes every optional capability and renders it as name/value lines
  Services/CameraService      MediaFrameReader viewfinder + still capture
  Services/AudioRecorderService, AudioPlayerService, ExportService
  Services/ServiceOutcomeExtensions  .OrThrow(), the Outcome-to-exception seam
  Controls/WrapPanel          WinUI 3 ships none; the knob row needed one
  Controls/WaveformView       Win2D: segment bands, amplitude trace or spectrogram, compositor playhead
  Controls/InputScopeView     Win2D rolling scope with peak-hold ballistics
  Controls/CutoutPreview      Win2D: the cut-out on a checkerboard, with an edge outline
  Controls/ShaderBackdrop     Runs AuroraShader behind a page
  Controls/ParticleBurst      Draws ParticleField; idle unless something is alive
  Shaders/AuroraShader        HLSL mesh gradient, compiled by ComputeSharp at build time
  Services/AudioCueService    Plays the synthesised cues on their own output device
  Common/Motion               Composition animation helpers; honours reduced-motion
  ViewModels/RecordingViewModel  Capturing one take: name, count-in, input meter, TakeRecorded
  ViewModels/ChopViewModel    The rest of the Chop page: files, knobs, playback, export
  Services/WindowsFaceLocator The IFaceLocator implementation — a Windows API, so it lives here
  Content/Models/u2netp.onnx  Fetched by SCRIPTS/download-models.ps1, not committed
tests/PoChopAudio.Unit/       Pure logic: detector, exporters, decoder, edge processor (100 tests)
tests/PoChopAudio.Integration/  Multi-component behaviour against the real services (18 tests)
```

There is no `PoChopAudio.Shared`. It existed so an API and a browser client could share a wire
contract; with one consumer left, a separate project for five files was a layer to step through
rather than a boundary to enforce, and the contracts moved into the slice that owns them.

## How a file becomes clips

1. **Upload** — `ChopService.UploadAsync`. The file is written to a temp job directory and decoded
   once into `canonical.wav` (32-bit float, source rate and channel count). Nothing is decoded twice.
   It returns a `JobId`, and every later call takes that struct — not a string, so an id cannot be
   confused with a filename at a call site.
2. **Envelope** — the same decode pass builds a 10 ms-per-frame RMS loudness trace plus a 900-point
   peak trace for the waveform view. Both live in memory on the job; the audio lives on disk.
3. **Detect** — `ChopService.Analyze`. See below.
4. **Export** — `ChopService.GetClip` or `GetBatchZip` slices `canonical.wav` and writes 16-bit PCM
   WAV.

Everything after step 1 is synchronous, so the caller chooses the thread. From a view model that
means `Task.Run`, or the window freezes.

Jobs are scratch space: they live under `%TEMP%/PoChopAudio` for as long as the app is open, and the
directory is wiped on startup and shutdown.

## Batches

A job is still exactly one recording. A batch is nothing more than the set of jobs the client is
currently holding, which is why there is no batch entity, no second lifetime to manage, and no way
for a batch to outlive the audio it points at.

- **Decoding** is sequential, one file at a time. Decoding several 250 MB recordings at once would
  only trade throughput for memory.
- **Settings** are shared until a file's own knobs are touched. Touching them sets `UsesOwnSettings`,
  which excludes that file from *Re-split all* — one badly-recorded take never forces the rest to be
  re-tuned, and re-tuning the rest never silently discards the fix.
- **Failure is per file.** A file that will not decode, or that finds the wrong number of takes, is
  flagged and left in place; the others finish and the flagged file is still exported.
- **Export** is `ChopService.GetBatchZip`, capped at `ChopLimits.MaxBatchFiles`. Unknown ids are
  skipped rather than fatal, so one lapsed job cannot block the save; only an entirely unusable set
  comes back `NotFound`.
- **Names** stay flat — `Nick_Happy_1.wav … Nick_Sad_5.wav` — because the source name is already the
  prefix. `ClipExporter.UniqueStems` guarantees the flatness is safe: two uploads called
  `Nick_Happy` become `Nick_Happy` and `Nick_Happy(2)`, compared case-insensitively so a Windows
  extraction cannot overwrite a clip.

## Detection

`SegmentDetector` never guesses a fixed threshold. It sweeps every gate from `noiseFloor + 3 dB` to
`peak - 3 dB` in 0.5 dB steps, counts how many takes each gate produces, and keeps the gate at the
centre of the **widest band of gates that all agree on the expected count**. A wide band means the
answer does not depend on the exact number, which is what a clean split looks like.

Then:

- Quiet stretches shorter than `MinGapMs` do not split a take in two.
- Runs shorter than `MinSegmentMs` are dropped — this is what removes marker pulses between takes.
- More runs than expected: the longest N win. Fewer: the caller gets a warning, never a silent lie.
- Each take's boundaries are followed outward while the signal stays within 9 dB of the gate, so the
  attack and decay are inside the clip, then padded by `PadMs` and clamped to the midpoint of the
  gap so neighbouring clips can never overlap.
- Peak-to-floor contrast under 6 dB means silence or an unbroken wall of sound: zero clips, plus a
  warning saying so.

`ChopOptions` is the only tuning surface for detection, and every knob is exposed in the UI.

## Recording

The Chop page can capture from the microphone instead of taking a file. A recording joins the same
batch as an upload and is split by the same detector — there is no recording entity, no second
lifetime, and no separate page, because after the first few milliseconds there is nothing to tell a
recording apart from an uploaded `.wav`.

`AudioRecorderService` captures through NAudio's `WaveInEvent` and writes 16-bit PCM WAV bytes, so
what comes off the microphone is byte-identical in shape to a picked `.wav`: same decode, same
detection, same clip naming, no codec that exists for this one feature.

`RecordingViewModel` owns the whole of it — the pending name, the count-in, the level meter — and
hands the finished bytes to `ChopViewModel` through a single `TakeRecorded` event. It knows nothing
about files, jobs or clips, which is the point: it was carved out of a `ChopViewModel` that had
grown to hold recording, playback, analysis and export at once.

- **Capture is deliberately unprocessed.** No AGC and no noise suppression. AGC in particular would
  ride the level between takes and destroy exactly the consistency the detector measures; noise
  suppression would eat the quiet gaps it splits on.
- **Naming is the whole point.** A recording has no filename to take a stem from, so the panel has a
  name field, and Record stays disabled until it is filled in — `CanStartRecording`. `Nick_Happy`
  becomes `Nick_Happy_1.wav … Nick_Happy_5.wav` through `ClipExporter.ClipFileName`.
- **Nothing the app can make a sound with may sound while the microphone is open.** `StartRecording`
  sets `AudioCueService.IsSuppressed` and only Stop or `Abandon` lifts it. A cue that leaks into a
  take does not annoy the user, it corrupts their recording.
- **The count-in is audible and visible.** One pre-rendered buffer carries the beats so they cannot
  drift, and `Countdown` steps 3-2-1 alongside it. Both matter: cue sounds default to off, so a
  silent count-in with nothing on screen is three seconds of no feedback at all.

## Head shots

The Cutout page is where head shots are taken. There was once a second, browser-only `/headshots`
page that did the same job locally, because photographs of someone's face are the input where "we
only upload it in order to process it" is the wrong trade. That argument won: the app has no server
at all now, so the one remaining page already keeps every pixel on the machine and the duplicate
was deleted.

The preview is mirrored so lining a shot up feels like a mirror; the captured frame is deliberately
*not* flipped, because saving the mirror would hand back a reversed photograph.

`CutoutPreview` draws each result on a **checkerboard**, which is the whole reason it is a Win2D
surface rather than an `Image`. A flat backdrop makes a translucent edge look identical to an opaque
one, so feather 0 and feather 2 were indistinguishable and the fine-tune sliders were being adjusted
blind. The **Edge** toggle runs a Sobel pass over the cut-out's alpha and tints it, which says
exactly where the mask boundary landed.

It was `BeforeAfterView`, and it also carried a draggable wipe between the original photo and the
cut-out. That went: the checkerboard and the edge outline answer "is this mask right", which is the
question being asked, and the wipe answered "what did the photo look like before" — which the user
had just seen in the viewfinder. It cost a second full-size Win2D bitmap per card in a virtualizing
list to do it. The original bytes are still held on the item, because re-applying the knobs re-cuts
from the frame as captured rather than compounding the previous result.

## Export polish

A clip is a plain slice of `canonical.wav` unless the download URL asks for more. `ExportOptions`
carries four knobs — normalize mode, target, ceiling, and the two fades — and every one of them
defaults to doing nothing, so a bare download URL still returns exactly the samples that were cut.

**They live on the URL, not in `ChopOptions`.** Nothing about a fade changes where the takes are, so
folding them into detection would re-run the whole analysis for an answer that cannot have changed.
Riding on the query string also means the URL changes when the settings do, which is what stops the
browser replaying a clip cached under the previous settings.

| Mode | Measures | Use it when |
| --- | --- | --- |
| `None` | nothing | The default. Bit-for-bit the raw cut. |
| `Peak` | loudest sample | Predictable, but one stray click keeps a quiet take quiet. |
| `Rms` | mean square | Steadier across takes, and reliable below 400 ms where gating cannot run. |
| `Lufs` | ITU-R BS.1770-4 integrated | Closest to perceived loudness. |

Three guards decide the gain, in order, and each is reported on `ClipGain` so the reason is never
invisible:

1. **Silence** — below `ExportLimits.SilenceFloorDb` (-70) the clip is left alone. Normalizing
   digital silence is a divide by zero wearing a hat.
2. **Ceiling** — if the target would push the loudest sample past `CeilingDb`, the ceiling wins and
   the clip lands quieter than asked. A loudness target is never met by clipping.
3. **Max gain** — `ExportLimits.MaxGainDb` (+24) caps the rest, so targeting -16 LUFS on a take that
   is mostly room tone cannot raise the noise by 40 dB and call it a performance.

Fades are raised-cosine rather than linear, so a 5 ms edge treatment removes a click without leaving
a corner of its own, and two fades longer than the clip are scaled to fit instead of multiplying
into silence in the middle.

### Two things worth knowing about the meter

`LoudnessMeter` builds the K-weighting biquads by bilinear-transforming the analog prototype, which
reproduces the BS.1770-4 coefficient table exactly at 48 kHz **and** stays correct at every other
rate — clips keep their source rate, so a 48 kHz-only table would quietly mis-measure 44.1 kHz work.
The RBJ cookbook shelf is a different filter and reads about 0.25 LU low; the tests pin this by
asserting a dual-mono 1 kHz sine reads its own dBFS value, which is the property the -0.691 offset
exists to arrange.

Mono is measured as one channel at weight 1.0, per the standard. That makes a mono clip read about
3 LU below the same audio duplicated to stereo, so **a mono take normalized to -16 LUFS receives
3 dB more gain than a stereo one**. This matches ffmpeg and pyloudnorm. The UI says so next to the
mode picker.

Normalization needs the whole clip measured before the first sample can be written, so an export
that asks for it reads the slice twice rather than buffering it — memory stays flat whether the take
is 200 ms or the entire recording. A pass-through export skips the measuring pass entirely.

## Decoding

| Format | Reader | Platform |
| --- | --- | --- |
| WAV | `WaveFileReader` | any |
| MP3 | `Mp3FileReaderBase` + NLayer | any (managed decoder) |
| AIFF | `AiffFileReader` | any |
| M4A, AAC, WMA, MP4 | `MediaFoundationReader` | Windows only |

`AudioDecoder.IsSupportedExtension` and `/diag` both report the list for the running platform, so the
UI never offers a format the server cannot read.

## Cutout

`CutoutService` takes an image and returns a PNG with the background removed. There is one engine,
`OnnxU2Net` — Microsoft.ML.OnnxRuntime running u2netp.onnx in-process, free, good for portraits.

`CutoutEngine` once had a second member, `BrowserOnnx`, for onnxruntime-web running in the Blazor
client. That client is gone and the member was deleted with it: an enum value no code can produce
is a branch every reader has to rule out by hand.

`IBackgroundRemover` and `EnginePicker` survive the collapse to one engine because they are what
makes the model *optional*. The picker reports an empty engine list when `u2netp.onnx` is absent,
which is how `CutoutService.IsAvailable` goes false and the page shows a banner instead of
throwing at startup.

`OnnxU2NetRemover` takes its model path from an injected `CutoutModelOptions` rather than reading
it from a hosting abstraction. That is what lets the engine live in `PoChopAudio.Services` with no
host dependency: the app resolves the path next to the executable. A missing file still leaves the
engine simply unavailable.

`CutoutService.CutOutAsync` does the whole thing in one call:

1. **Decode** — raw RGBA via `ImageSharp`, EXIF auto-rotated, Pixel Motion Photo `.MP.jpg` trailer
   stripped.
2. **Mask** — the picker selects an engine; the engine returns the alpha mask as RGBA bytes.
3. **Clean** — `EdgeProcessor` applies threshold, morphology, feather and the alpha multiplier, in
   that order. Defaults are tuned for head shots: threshold 160 kills the soft halo u2netp leaves,
   a 1 px erode pulls the edge inside the fringe, a 1 px feather stops it looking jagged, and the
   1.6x multiplier saturates what survives so the head is solid rather than translucent.
4. **Crop** — `HeadFinder` cuts at the neck, so the result is the head and not the body.
5. **Encode** — `ImageDecoder.EncodePng` writes a transparent PNG.

This used to be four HTTP calls with a job store holding decoded pixels between them. That shape
existed because HTTP is stateless and a browser needed a handle to refer back to; in-process the
caller already holds the bytes, so `CutoutJobStore`, its 2 h expiry and its temp directory were
removed along with `CutoutExporter` (ZIP and filename templates) and `TrimHelper` (the
whole-subject crop, superseded by the head crop). `ProgressChannel` went too — it published
progress that only the deleted SSE endpoint ever subscribed to.

`CutoutJobId`, `CutoutUploadResult` and `CutoutResult` outlived that cull by being records nobody
deleted; with no job to identify and no separate download call to describe, they were dead
declarations and are now gone too. So is `CutoutCapabilities`: a five-field record whose only
caller was a test.

The model file (`u2netp.onnx`, ~4.4 MB) belongs in `src/PoChopAudio.WinUI/Content/Models/` and is
**not** committed — `.gitignore` excludes it and `SCRIPTS/download-models.ps1` fetches it. The
csproj's `Content ... Condition="Exists(...)"` clause means a fresh clone builds without it,
`EnginePicker` drops the engine, the Cutout page shows a banner, and the cutout tests skip
themselves.

For a while the file was committed anyway, which quietly made all of that dead code: not one of
those paths could be reached, and every clone carried 4.4 MB of model to guarantee it. Either the
file ships or the degradation path is real; it cannot be both.

## Settings and diagnostics

Three things persist across a restart, and only three: the theme, a default save folder, and
whether batch saves may skip the folder picker. They live in
`%LOCALAPPDATA%/PoChopAudio/settings.json`.

**The chop and cutout knobs are deliberately not among them.** Those belong to a take, not to the
app, and carrying the last session's tuning silently into the next one hands someone clips cut by
settings they cannot remember choosing. `ResetSettings` on the Chop page already exists for the
case where the knobs need to go back; a persisted knob would fight it.

`AppSettingsService` follows the same probe-report-degrade rule as the ONNX model: a missing file
is the ordinary first-run case, and a corrupt one falls back to defaults rather than throwing.
Settings are a convenience, so failing to read them must never be what stops the app starting.

**Theme is applied to the frame, not the application.** `Application.RequestedTheme` can only be
set before the first window exists, so a live toggle has to assign `RequestedTheme` on the window's
root element — every page lives inside that one frame, so a single assignment re-themes the lot.

### The capability report

Six things in this app degrade quietly when something is missing — the ONNX model, the Windows face
detector, the Media Foundation codecs, a camera, a microphone, and Mica — and before this only one
of them (the missing model) was reported anywhere a user could see. The rest failed correctly and
silently, which is worse than failing loudly: the app does less and never says which part.

`DiagnosticsReport` probes all of them into one list the Settings page renders and a Copy button
turns into pasteable text. Absent capabilities are drawn amber rather than red, because every one
of them degrades to something that still works. It is rebuilt on every visit to the page rather
than cached — a camera can be plugged in, or the model downloaded, while the app is running, and a
stale report is worse than none when it is the one someone pastes into a bug.

### Cleaning up scratch

The Settings page can delete scratch left by earlier runs, which changed how `ChopJobStore` decides
what is abandoned. It used to use age: anything older than the two-hour job lifetime was assumed
dead. That is safe for a constructor sweeping yesterday's leftovers and unsafe the moment a user
can press "clean up now", because a second copy of the app five minutes into a session looks
exactly like a crashed one.

So liveness is now explicit. Each store holds `Root/.inuse` open with `FileShare.None` for as long
as it lives, and a sweep skips any sibling whose lock it cannot take. `FileOptions.DeleteOnClose`
means Windows drops the file even when the process is killed outright — which is precisely the case
the age check existed to cover, now answered exactly instead of guessed. A directory with no lock
file is free by definition.

## Accessibility

Every icon-only button, slider, toggle and custom control carries an `AutomationProperties.Name`;
there were none at all before. Three of those needed more than a literal string:

- **Sliders** have a label `TextBlock` beside them that is never programmatically associated, so a
  screen reader read them as bare tracks. Each one names its own unit — "Shortest gap, in
  milliseconds".
- **Rows in a list** all carried the same "Play" and "Save", which is indistinguishable to anyone
  not looking at the screen. `LabelWithValueConverter` folds the row's own value into the label, so
  they announce as "Play sound 1", "Play sound 2".
- **The waveform and the level meter** are drawn, not composed, so nothing about them is visible to
  assistive tech no matter how they are decorated. Both set their name from code as their contents
  change: the waveform announces the file, its length and how many sounds were found; the meter
  announces the level it is showing.

Status and error lines are `LiveSetting="Polite"` and `"Assertive"` respectively — a red line that
appears silently reaches nobody who is not already watching it.

## Graphics and sound

The pictures are drawn on the GPU through Win2D, and the sounds are generated rather than shipped.

### Why the waveform stopped being XAML elements

`WaveformView` used to build one `Line` element per bar — about 270 at card width — plus a `Border`
and `TextBlock` per segment, cleared and rebuilt on every resize and every re-split. Ten files on
screen put several thousand live elements in the visual tree to show a picture that is not
interactive at the element level and never needed to be. It is now a single `CanvasControl`.

That change is what made the rest possible: a spectrogram and an edge-detection outline are both
trivial once there is a drawing surface, and both impossible on a pile of `Line`s.

### The spectrogram

`Spectrogram.Build` produces a magnitude grid the control uploads as one bitmap and lets the GPU
filter. Two choices worth keeping:

- **Bins are log-spaced.** An ear hears octaves, and a linear axis spends four fifths of its height
  on 5–20 kHz where a spoken take carries almost nothing.
- **Magnitudes are normalised against a fixed dB window**, not against the loudest bin present. Two
  recordings of the same voice then look the same, instead of each being auto-levelled into looking
  equally busy.

Peak rather than mean within each band, because a narrow tone inside a wide high-frequency band
would otherwise be averaged into invisibility by its silent neighbours.

### Sound the app makes itself

`CueSynth` renders every cue as decaying sines. In an app where the user is judging recorded audio a
cue must be instantly distinguishable from the material, and a pure tone is about as far from a
voice or a footstep as a sound gets; a sampled "ding" would be one more thing to mistake for
content.

**The suppression rule is the whole design.** `AudioCueService.IsSuppressed` is set while the
microphone is open and every entry point checks it, because a tick that leaks into a take does not
annoy the user — it corrupts their recording. Clip-audition blips bracket the clip (before it
starts, after it stops) rather than playing over it. The count-in deliberately ignores suppression:
it runs before the microphone opens, which is the one moment making a sound is the entire point.

The count-in is rendered as one buffer rather than sequenced with a timer, because a count-in whose
beats drift is worse than none, and sample offsets cannot drift.

### Two things that will crash the app

Both were hit building this, and both are silent until runtime:

1. **`CanvasAnimatedControl` as an overlay.** It is a `SwapChainPanel` underneath, composites in its
   own layer, and paints over the sibling XAML it is supposed to sit behind — the page rendered as
   nothing but the gradient. `CanvasControl` composites in the visual tree; animate it with
   `CompositionTarget.Rendering`.
2. **XAML properties from a Win2D render thread.** `CanvasAnimatedControl.Draw` is not on the UI
   thread, and reading `ActualTheme`, a dependency property or `UISettings` there throws
   `RPC_E_WRONG_THREAD` — the app died at startup with `0xC000027B` before a single log line. What
   the drawing code needs is snapshotted on the UI thread into plain fields.

### Layout, theming and the card lists

Reworked in one pass; the parts that are not obvious from the XAML:

- **`NavigationView.PaneDisplayMode` is `Auto`, not `Left`.** `Left` pins the pane open at every
  size, so a narrow window spent most of its width on navigation. `Auto` is the control's own
  adaptive behaviour — full pane, then icon rail, then hamburger.
- **One `ShaderBackdrop`, in `MainWindow`, behind the `Frame`.** It used to be one per page, so
  navigating rebuilt a `PixelShaderEffect` and restarted the animation clock each time. Settings
  moved to the same glass cards as the other pages as a consequence: an opaque card over the
  gradient reads as a hole punched in the page.
- **Every page is `NavigationCacheMode="Required"`.** Without it, navigation destroyed and rebuilt
  the page — which on Cutout meant tearing down `CameraService`'s `MediaFrameReader` and starting a
  fresh one, a visible second of black viewfinder for nothing.
- **The card lists are `ItemsRepeater`.** `ItemsControl` has no virtualizing panel, so every card was
  realized at once and each one owns a live Direct2D surface. This has a hard prerequisite:
  **`WaveformView` and `CutoutPreview` create their `CanvasControl` in `Loaded`, not in XAML.**
  Releasing a surface calls `RemoveFromVisualTree`, which is permanent, and a recycled element is
  unloaded and then shown again against different data — so re-entering the tree has to build a new
  one. See `EnsureSurface` in both.
- **Colours are theme brushes, not literals.** `DangerFill`, `OnDangerFill`, `DangerText`,
  `ErrorText`, `ErrorBanner` and `WarnBadge` live in `App.xaml`'s theme dictionaries, high contrast
  included, where they resolve to system colours. Light theme darkens the error red to `#B91C1C`:
  `#EF4444` on the light card measures about 3.6:1, under the 4.5:1 WCAG AA asks for small text.
- **The record button's red is the exception, and is inline.** Setting `Background` on
  `AccentButtonStyle` paints only the rest state — the template's PointerOver and Pressed states
  assign the accent resources back over it, so the button turned blue under the cursor. It overrides
  `AccentButtonBackground*` in its own theme dictionaries, which a `Style` cannot carry because
  `Resources` is not a dependency property, and which cannot alias the shared brushes because a
  `StaticResource` alias resolves once and freezes the theme.
- **Uniform `WrapPanel` cells replaced the fixed knob grids.** A three- or four-column `Grid` left
  roughly 190px for a slider plus its label and value; the labels clipped.
- **Minimum window size is clamped in `WindowHelper.SetMinimumSize`**, by watching
  `AppWindow.Changed` and resizing back. `OverlappedPresenter.PreferredMinimumWidth` says this
  directly but only exists from Windows App SDK 1.7, and moving that pin drags WinUI and Win2D with
  it. The Cutout viewfinder additionally shrinks to 240px below a 760px window height, so the
  shutter button — the only control that page exists for — cannot be pushed below the fold.

### Motion is a system choice

Everything animated routes through `Common/Motion.cs`, which reads the Windows "Show animations"
setting fresh each time. Windows exposes that setting because motion makes some people ill; an app
that animates anyway has taken the choice away from them. The particle field additionally refuses to
run while recording — CPU contention during capture shows up as dropped frames in someone's take.

## Deliberately absent

- **No auth.** Nothing is protected, so there is no BFF cookie flow, no Entra ID, no
  `FakeAuthHandler`. Add `Features/Auth` and a `FallbackPolicy` if this is ever hosted for more than
  one person — the NET_RULES rules for that path still apply.
- **No server and no web client.** The API and the Blazor client were deleted once the app became
  desktop-only. Do not reintroduce them: the logic they wrapped lives in `PoChopAudio.Services`,
  which both of them referenced anyway, and the app calls it in-process. The `Outcome<T>` return
  type is the seam that used to be HTTP status codes.
- **No primary database, and no Azurite.** A job is still a temp directory that resets on restart.
  The best-effort batch recency index went with the API; a desktop app has no Azurite to reach and
  the index was never load-bearing.
- **No paid AI services.** The one engine is free and on-device. remove.bg was considered and
  dropped per the no-paid-services decision.
- **No per-file progress bar.** Processing is sequential and the status line names the file it is
  on, which is enough at this scale.
- **No persisted chop or cutout knobs, and no presets.** See "Settings and diagnostics" above: a
  knob is part of a take, not part of the app.
- **No keyboard accelerators, and no device pickers.** The Settings page reports which microphone
  and camera the app resolved to, which is what catches the wrong one being used; choosing a
  different one is a separate feature and is not built.
- **No face-detection *model*.** `HeadFinder` crops to the head by reading the shape of the
  saliency mask — peak, neck, shoulder flare — rather than adding a second ONNX model for faces.
  What it does use, when the OS has it, is `WindowsFaceLocator` over the `FaceDetector` that ships
  with Windows: a measured chin replaces the inference of where the neck is. That adds no download
  and no package, which is what the rule was actually about, and the mask-shape logic runs unchanged
  when the component is absent — which is why no test needs it.
- **No CI beyond build-and-test.** `.github/workflows/ci.yml` restores, builds Release and runs both
  suites on `windows-latest`. `TreatWarningsAsErrors` is on solution-wide and there is no analyzer
  config, so that build step *is* the lint gate. There is no packaging, signing or release job: the
  app is unpackaged and run from `bin`.
- **No UI tests.** Playwright cannot drive a WinUI 3 window and no WinAppDriver harness is set up.
  The camera, the microphone, the face locator and the XAML itself are covered by nothing automated;
  the log file is the only way to see them work.
