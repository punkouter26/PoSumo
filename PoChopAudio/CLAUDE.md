# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**PoChopAudio is a WinUI 3 desktop app.** It splits recordings into one clip per take, and takes
head-shot photos and strips their backgrounds. Everything runs on the machine: there is no server,
no HTTP, and nothing is uploaded.

It did not start that way. The repo used to host an ASP.NET Minimal API plus a Blazor WASM client,
and some of the architecture is still shaped by that history — the vertical slices, the chop job
store, the `Outcome<T>` return type. Those all survive because they earned their place, not because a
server is coming back. **`PoChopAudio.API` and `PoChopAudio.Client` were deleted deliberately; do
not reintroduce them.**

## Governance documents

- [AGENT.md](AGENT.md) — the architecture doc. Records what was built and why, including a
  "Deliberately absent" section. The Detection, Export polish, Decoding and meter sections are the
  best explanation of the DSP anywhere in the repo. Its plumbing sections used to describe a world
  that no longer existed — HTTP endpoints, a browser `/headshots` page, an `AudioWorklet` recorder,
  verification scripts that were never in the tree — and have been rewritten against the code.
  Update it when architecture changes.
- [README.md](README.md) — user-facing behaviour of the chop and export knobs. Current.
- `NET_RULES.md` — the author's standing rules (naming, layout, testing). **Deleted from the repo**;
  read it with `git show f185bd3:NET_RULES.md`, the last commit that carried it. The part that still
  binds here is §5 (test layout). Its §2 says anything two slices need goes in a `Shared` project —
  that project is gone (see below) and the rule now reads as "slices do not reference each other",
  which they don't. Its §4 Blazor UI section and the web half of §6 no longer apply, and §1 says
  trunk is `master` while this repo's trunk is `main`.

## Commands

CI runs the first two of these on every push and PR
([.github/workflows/ci.yml](.github/workflows/ci.yml)): restore, build Release, test. That is the
whole gate — see `TreatWarningsAsErrors` below.

```powershell
# Restore + build (Release) + run all tests. Add -Run to also build and launch the desktop app.
# -Platform is detected from RuntimeInformation.OSArchitecture, so it is right under emulation.
./SCRIPTS/setup.ps1
./SCRIPTS/setup.ps1 -Run

# Fetch the ~4.4 MB u2netp.onnx cutout model into src/PoChopAudio.WinUI/Content/Models (idempotent)
./SCRIPTS/download-models.ps1
```

Plain dotnet, against the `.slnx` solution:

```powershell
dotnet build PoChopAudio.slnx -c Release
dotnet test PoChopAudio.slnx                              # both test projects
dotnet test tests/PoChopAudio.Unit --filter "FullyQualifiedName~SegmentDetectorTests"
dotnet test tests/PoChopAudio.Unit --filter "FullyQualifiedName~HeadFinderTests.KeepsFullHeightForHeadOnlyPhoto"
```

`TreatWarningsAsErrors` is on solution-wide — a warning fails the build. There is no separate
linter, formatter or analyzer config: the compiler is the whole gate. The one blanket suppression is
`MVVMTK0045` in the WinUI csproj.

### Building and running the desktop app

The solution-wide build does **not** produce a runnable app: the WinUI project needs an explicit
platform and RID, because it is unpackaged and self-contained.

```powershell
dotnet build src/PoChopAudio.WinUI/PoChopAudio.WinUI.csproj -c Release -p:Platform=ARM64 -r win-arm64
```

Then run the exe from **`bin`, never `publish`**:

```
src\PoChopAudio.WinUI\bin\ARM64\Release\net10.0-windows10.0.22621.0\win-arm64\PoChopAudio.WinUI.exe
```

**Check the architecture before you build.** `Platforms` is `x86;x64;ARM64` and the output path
contains whichever you picked, so a wrong guess quietly builds an app you then cannot find. Note
that `$env:PROCESSOR_ARCHITECTURE` **reads `AMD64` inside an emulated x64 shell on an ARM64
machine**: it reports the process, not the OS. Get the truth from
`[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture`, which is what `setup.ps1`
now does for its own default.

`dotnet publish` silently drops `PoChopAudio.WinUI.pri`, the app's own resource index, and without
it the app dies at startup with a stowed exception (`0xC000027B`) inside `Microsoft.UI.Xaml.dll`.
The build output has the `.pri` plus every self-contained Windows App SDK file.

**The app is self-contained** (`WindowsAppSDKSelfContained` + `SelfContained`). The pinned Windows
App SDK 1.6 runtime is not installed on most machines, and an unpackaged app without it fails to
launch with `0x80670016`. Shipping the runtime in the output folder avoids a machine-wide install.

### When the running app misbehaves

There is no console. Logs go to `%LOCALAPPDATA%\PoChopAudio\logs\pochopaudio.log`
([FileLoggerProvider.cs](src/PoChopAudio.WinUI/Services/FileLoggerProvider.cs)), which is the only
window into the camera, the face locator and the cutout pipeline — none of which any test covers.
The file is **deleted and restarted** past 2 MB rather than rotated, so copy it before reproducing
something long.

## Architecture in one pass

.NET 10 / C# 15. Three source projects, two test projects, vertical slices.

```
src/PoChopAudio.Services/   Host-agnostic engine room. No UI, no ASP.NET. All the real logic.
src/PoChopAudio.WinUI/      The app: XAML views, view models, camera, audio devices, file pickers.
```

The dependency direction is one-way: `WinUI → Services`. `Services` must never reference `WinUI` or
take a dependency on any UI or hosting framework — that constraint is what keeps the logic testable
without a window, and it is why the two test projects can cover it directly.

**There is no `PoChopAudio.Shared`.** It held the wire contracts an API and a browser client both
needed. With one consumer left it was a project to step through rather than a boundary to enforce,
so its five files moved into the slice that owns them: `Chop/ChopContracts.cs`,
`Chop/ExportContracts.cs`, `Cutout/CutoutContracts.cs`, `Cutout/CutoutLimits.cs`,
`Cutout/IBackgroundRemover.cs`. Do not reintroduce it.

### Where the real logic is

The interesting, test-covered parts are pure and I/O-free — put new logic there, not in view models:

- [SegmentDetector.cs](src/PoChopAudio.Services/Chop/SegmentDetector.cs) — the split algorithm.
  It does not pick a fixed threshold: it sweeps every gate from `noiseFloor + 3 dB` to `peak - 3 dB`
  in 0.5 dB steps and keeps the centre of the *widest band of gates that agree on the expected
  count*. Read the Detection section of AGENT.md before touching it.
- [ClipExporter.cs](src/PoChopAudio.Services/Chop/ClipExporter.cs) — WAV slicing and `UniqueStems`,
  the case-insensitive de-duplication that makes the flat batch ZIP safe on Windows. Also the
  two-pass export: normalization needs the whole clip measured before the first sample can be
  written, so it reads the slice twice rather than buffering it.
- [ClipProcessor.cs](src/PoChopAudio.Services/Chop/ClipProcessor.cs) — export gain and fade maths,
  no I/O. The three guards (silence, ceiling, max gain) live in `DecideGain`.
- [LoudnessMeter.cs](src/PoChopAudio.Services/Chop/LoudnessMeter.cs) — ITU-R BS.1770-4. The
  K-weighting biquads come from the analog prototype, **not** the RBJ cookbook shelf, which is a
  different filter and reads ~0.25 LU low.
- [EdgeProcessor.cs](src/PoChopAudio.Services/Cutout/EdgeProcessor.cs) — mask threshold, morphology,
  feather, alpha multiplier, in that order.
- [HeadFinder.cs](src/PoChopAudio.Services/Cutout/HeadFinder.cs) — crops to the head alone. u2netp
  is a saliency model, not a face model, so its alpha bounding box always includes the shoulders.
  This reads per-row mask widths: the head widens to a peak, narrows to the neck, then the
  shoulders flare back out. Cut at the neck. A head-only photo never flares and keeps full height.
  Each tuning constant carries the reasoning for its current value in a doc comment — read those
  before changing a number.
- [ImageDecoder.cs](src/PoChopAudio.Services/Cutout/ImageDecoder.cs) — decode, EXIF auto-rotate,
  Pixel Motion Photo (`.MP.jpg`) trailer strip.

### The two services

`ChopService` and `CutoutService` are the whole feature surface. The view models call them and
nothing else.

They are deliberately different shapes. `ChopService` keeps the **job store**: upload decodes to a
canonical WAV, analyze re-runs only the cheap tuning step against it, and export renders on demand,
so turning a knob does not re-decode the file. Jobs live until the user clears them or the app
closes — the 2 h TTL that used to be documented here was never enforced (nothing called
`RemoveExpired`) and has been removed rather than wired up: expiring a job the user still has on
screen would be a bug, not housekeeping.

`CutoutService` has no job store at all — `CutOutAsync` decodes, masks, cleans, crops and encodes in
one call. The multi-step version existed to give a browser a handle to refer back to across
stateless requests; in-process the caller already holds the bytes.

Per NET_RULES §2, the slices never reference each other. `Chop` and `Cutout` share only
`Outcome<T>` and `ExportedFile`, which sit at the `Services` root for exactly that reason.

### Outcome, not exceptions

Service calls return `Outcome<T>` ([Outcome.cs](src/PoChopAudio.Services/Outcome.cs)): a value, or a
domain reason there isn't one (`NotFound`, `Invalid`, `Empty`, `TooLarge`, `UnsupportedMedia`,
`Undecodable`, `EngineUnavailable`). Expected failures travel on the normal return path — an expired
job is not exceptional.

The view models funnel everything into an `ErrorMessage`, so they unwrap with `.OrThrow()`
([ServiceOutcomeExtensions.cs](src/PoChopAudio.WinUI/Services/ServiceOutcomeExtensions.cs)), which
converts an outcome to an exception **at the consumer boundary only**. Do not push that back into
the services.

### Long work must not run on the UI thread

`Analyze`, `GetClip` and the ZIP builders do real DSP and image work. Call them through `Task.Run`
from a view model or the window freezes. This is why the service methods are mostly synchronous —
the caller decides where they run.

## Conventions worth knowing

- **IDs are `readonly record struct`** — `JobId`, and no bare `Guid` or `string` id crossing a
  boundary. Every `ChopService` method takes `JobId`, not `string?`; they took strings while the
  caller was a router binding route parameters, and the parse-per-call that came with it made
  "malformed id" and "no such job" the same `NotFound`. There is no `JobId.TryParse` any more: ids
  are minted by the store and handed straight back, so nothing reconstructs one from text.
  `CutoutJobId` is gone entirely — `CutoutService` has no job store to identify anything in.
  Closed sets are enums (`CutoutEngine`).
- **Logging** goes through `[LoggerMessage]` source generators (`ChopLog`, `CutoutLog`). No string
  interpolation in log calls. `Services` uses `Microsoft.Extensions.Logging.Abstractions`; the app
  registers a factory in `App.ConfigureServices` with `services.AddLogging()`.
- **Batch failure is per file.** A file that will not decode is flagged and still included in the
  ZIP; the batch never aborts.
- **Packages are pinned centrally** in [Directory.Packages.props](Directory.Packages.props); project
  files carry bare `<PackageReference Include="..." />` with no version. Adding a package means
  editing both files.
- **Scoped XAML, no inline styles.** WinUI 3 ships no `WrapPanel`; there is a small one in
  [Controls/WrapPanel.cs](src/PoChopAudio.WinUI/Controls/WrapPanel.cs) rather than a toolkit
  dependency.
- **Buttons bind commands; they do not use `async void` Click handlers.** A handler that returns
  void swallows anything thrown past its first `await` and bypasses the command's `CanExecute`, so
  a page that mixes both guards the same button on one path and not the other. Two mechanics make
  the binding possible:
  - The pickers need a `Window`. The view models expose `Host`, set from the page's `Loaded`
    handler — not its constructor, because `App.MainWindow` does not exist yet while the frame is
    navigating to the first page from the window's own constructor.
  - **A `DataTemplate` has its own namescope**, so `{Binding ElementName=Page}` inside one resolves
    to nothing at all, silently. Card templates reach the view model through an `Owner` property on
    the item (`{x:Bind Owner.SomeCommand}`), which is a compiled binding the XAML compiler checks.
    The two buttons on a take row are the exception: their item is a `ChopSegment` record from
    `Services` with no route back, so they keep a *non-async* Click handler that executes the same
    command.
  The remaining `async void` are three page `Loaded`/`Unloaded`/`Drop` handlers, which have nowhere
  to return a Task to. Each wraps its whole body in try/catch, because an exception escaping one
  reaches the runtime's unhandled hook and takes the process down.

### Graphics and sound

Two packages carry this, pinned centrally like everything else:

- **Win2D** (`Microsoft.Graphics.Win2D`) — Direct2D for WinUI 3. The waveform, spectrogram, live
  input scope, cut-out preview and confetti all draw through a `CanvasControl`.
- **ComputeSharp.D2D1.WinUI** — HLSL pixel shaders written as C#. `Shaders/AuroraShader.cs` is
  real HLSL; the source generator compiles it at build time, which is why `AllowUnsafeBlocks` is on
  (the generator emits unsafe marshalling code; nothing hand-written here uses it).

**Win2D is pinned to 1.3.2, not the latest.** 1.4.0 pulls `Microsoft.WindowsAppSDK.WinUI` 1.8
transitively, which collides with the pinned WindowsAppSDK 1.6 and breaks the build outright with a
double-import and an MSIX targets failure. Do not bump it without moving the whole SDK.

**`TargetPlatformVersion` is 22621**, raised from 19041 so ComputeSharp's assets resolve at all —
below that NuGet silently contributes nothing and the types simply are not there.
`TargetPlatformMinVersion` stays 17763, so the app still runs on older Windows. Note the output
path changed with it.

Two rules that are not optional:

1. **`CanvasAnimatedControl` cannot be used as a page overlay.** It is backed by a `SwapChainPanel`,
   which composites in its own layer and paints straight over sibling XAML — the entire page went
   blank. Every surface here is a `CanvasControl`, animated where needed by
   `CompositionTarget.Rendering`.
2. **Never touch a XAML property from a Win2D render thread.** `CanvasAnimatedControl.Draw` runs off
   the UI thread, and reading `ActualTheme`, a dependency property or `UISettings` there throws
   `RPC_E_WRONG_THREAD` and takes the process down at startup with `0xC000027B`. Snapshot what the
   drawing code needs on the UI thread first.

All motion routes through `Common/Motion.cs`, which honours the Windows "Show animations" setting.
Audio cues are synthesised by `Services/Dsp/CueSynth.cs` and played by `AudioCueService`, which is
**suppressed while the microphone is open** — a cue that leaks into a take corrupts the recording.

### Settings, and what persists

Exactly three things survive a restart — theme, default save folder, and whether a batch save may
skip the folder picker — in `%LOCALAPPDATA%/PoChopAudio/settings.json` via `AppSettingsService`.
The chop and cutout knobs are deliberately **not** persisted: a knob belongs to a take, not to the
app. Do not add them without reading the reasoning in AGENT.md.

The Settings page also carries the capability report (`DiagnosticsReport`) — the one place that
says out loud which optional capabilities actually started on this machine, and the only user-facing
report for the five that used to degrade silently.

### Optional-capability pattern

The rule for anything optional: **probe, report, degrade — never throw at startup.** Three live
instances, and new optional dependencies should take the same shape:

- **The cutout model.** `u2netp.onnx` is absent from a fresh clone — `.gitignore` excludes it and
  `./SCRIPTS/download-models.ps1` fetches it. The WinUI csproj includes it only
  `Condition="Exists(...)"`; when missing, `EnginePicker` drops `OnnxU2Net`,
  `CutoutService.IsAvailable` goes false, the Cutout page shows a banner, and the service returns
  `EngineUnavailable` naming the download script. Cutout tests skip themselves.
  **Do not commit the model.** It was committed for a while, which made every one of those
  branches unreachable while the repo still carried 4.4 MB of it in each clone — the degradation
  path and the shipped file are alternatives, not a pair.
- **Face detection.** [IFaceLocator.cs](src/PoChopAudio.Services/Cutout/IFaceLocator.cs) is the
  interesting variant: the interface lives in `Services` with **no implementation there**, because
  the only implementation is a Windows API and `Services` may not reference an OS framework. The app
  registers [WindowsFaceLocator.cs](src/PoChopAudio.WinUI/Services/WindowsFaceLocator.cs), and
  `CutoutService` takes it as an optional constructor parameter defaulting to null. A measured chin
  replaces `HeadFinder`'s inference of where the neck is when it is available; the mask-shape logic
  runs unchanged when it is not, which is why the tests never need it.
- **Platform codecs.** M4A/AAC/WMA go through `MediaFoundationReader`, Windows-only.
  `AudioDecoder.IsSupportedExtension` reports the list for the running platform, so the UI never
  offers a format that cannot be read.

### Camera

`CutoutPage` → `CameraService` → `MediaFrameReader`. WinUI 3 has no `CaptureElement` (that was UWP),
so the viewfinder is built from frames pushed into a `SoftwareBitmapSource`. Stills come from the
most recent preview frame rather than `CapturePhotoToStreamAsync`, which fights the frame reader for
the device on many webcams.

The page drops frames while one is still being handed to the UI thread — without that guard the
queue grows without bound and the preview drifts seconds behind the room.

## Test layout

| Project | Scope | State |
| --- | --- | --- |
| `tests/PoChopAudio.Unit` | Pure logic — detector, exporters, decoder, edge processor, head finder, job store, DSP | 100 tests, all passing. Add here first |
| `tests/PoChopAudio.Integration` | Multi-component behaviour against the real services | 18 tests, all passing |

The suites are capped at **100 unit and 50 integration**. Unit is at its cap, so adding a test there
means retiring one. What was cut to get under it, and the standard to apply next time: tests of a
deleted HTTP surface (a `CutoutCapabilities` JSON deserialization test quoting a live
`/api/cutout/capabilities` response), tests asserting a constant equals its own literal, a test that
`typeof(SegmentDetector)` is not null, tests of a library's own output rather than this code, and
`[InlineData]` rows that re-ran one branch with a different number. Nothing that guards a DSP
decision or a documented failure mode was touched.

Both reference `Services` directly. `Integration` used to drive the HTTP pipeline
through `WebApplicationFactory`; when the API was deleted its export-maths and cutout-pipeline tests
were ported to call the services instead, which is closer to what the app actually does. It links in
`u2netp.onnx` from the WinUI project `Condition="Exists(...)"`, so its cutout tests skip on a fresh
clone rather than fail.

The camera, the microphone, the face locator and the WinUI UI itself need real hardware and a real
window, and are **not covered by any automated test** — the log file is the only way to see them
work.
