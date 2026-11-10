# OpenKuro

OpenKuro is the source repository for Kuro, a native Windows video and motion editor built with C#/.NET 8 and WPF. See the [roadmap](docs/ROADMAP.md) for supported features and planned work.

## Run

After packaging locally, open `dist/Kuro-0.1.6/Kuro.exe`. The welcome screen lists recent projects and opens a blank timeline with New Project.

From source:

```powershell
dotnet build Kuro.sln -c Release
dotnet run --project src/Kuro -c Release
```

Requirements: Windows 10/11 x64, .NET 8 Desktop Runtime (SDK 8 to build), `ffmpeg.exe` and `ffprobe.exe` from a full FFmpeg build. FFmpeg 8.1.1 was used for verification. Put the tools on PATH, set `KURO_FFMPEG_DIR` to their bin directory, or place them in `tools/` beside the application executable. Local stabilization requires FFmpeg's `deshake` filter. No Adobe installation is required to run Kuro.

## First edit

1. Import video, audio, or images with **Ctrl+I**, the Project dropzone, or Explorer drag and drop. Importing leaves the timeline unchanged.
2. Drag a Project asset onto a timeline row to choose its start frame and stacking position. Double-clicking an asset explicitly places it at the playhead.
3. Select a layer and use Properties to edit transforms, text, shapes, timing, and available effects. Drag a numeric label to scrub its value; type a precise number in its field. Drag a selected visual layer in the viewer to move it. Double-click a selected text layer in the viewer to edit its text.
4. Drag a clip to move it; drag its edges to trim. Ctrl-click selects multiple layers. **Ctrl+Shift+D** splits selected unlocked layers at the playhead. The upper layer renders in front.
5. Select a layer and press S for Scale, P for Position, R for Rotation, T for Opacity, or U to toggle animated properties. Drag the number in either direction to adjust it and click its diamond to add or remove a keyframe. Right-click selected keys for graph and easing actions. Backspace deletes selected keyframes, or selected layers when no keys are selected. Position and Scale have independent X/Y link toggles.
6. Save as `.kuro` with **Ctrl+S**. Export a real H.264/AAC MP4 with **Ctrl+M**. Choose composition, 4K, HD, or custom even dimensions and full composition or work area.

Press **Ctrl+K** for the active composition’s resolution, aspect ratio, frame rate, duration, name, and background. FPS changes preserve clip and keyframe frame numbers. The **AI Editing** toolbar button toggles a resizable sidebar beside the viewer and timeline.

The editable `examples/First light.kuro` project includes original procedural landscape artwork, an eight-second video, stacked titles, and transform/opacity curves. It is optional and never loaded into new projects automatically.

## Implemented

- Native resizable Windows workspace with a cool charcoal and vermilion default, optional paper theme, custom dropdown and context menus, collapsible inspector sections, and saved splitters.
- Real asynchronous media probing, thumbnails, PCM waveforms, searchable bins, asset rename/relink, and non-destructive media references.
- Layer stacking, move/trim/split/duplicate/delete/ripple delete, selection, visibility/lock/audio/solo, snapping, markers, work area, timeline zoom, and precompositions.
- Transform numerical controls, keyframes, linear/hold/cubic Bézier evaluation, editable value graph and diagnostic speed graph, freeze/reverse/constant speed.
- Editable Unicode text with installed font selection/weight, drag-to-draw shapes, fill, and text/shape blur/glow/shadow. Double-click text in the preview to edit it in place. Use the preview zoom menu or wheel to zoom, and middle-drag to pan.
- Shared preview/export compositor on a dedicated worker, Full/Half/Quarter preview, guides, frame stepping/time entry, audio preview prepared locally and played through Windows media, and export audio mixing.
- Atomic versioned projects with stable IDs, relative media references, undo/redo, 30-second recovery autosave, recent projects, and configurable/exportable shortcuts.
- Right-click a video layer for Remove Background or Stabilize. Each processes the selected source trim and swaps the result into that layer as one undoable edit. Remove Background runs U²-Netp locally after a one-time model download. Stabilize uses FFmpeg block matching.
- Configurable API-backed editing through the same command layer, real model discovery when available, a tool-call capability test, cancellation, revision checks, and Windows Credential Manager storage.

## Important current limits

CPU raster compositing is shared between preview and export; the WPF workspace itself uses available Windows graphics acceleration. GPU composition, sustained realtime 4K, HDR/wide-gamut color management, long-form performance, proxies, bounded disk cache eviction, hardware encoding, render queue, pen/masks, blend modes beyond Normal, parenting, AV linking, optical-flow/frame blending/motion blur, object tracking and temporally consistent mattes, and exact AE preset parity are unfinished. Audio preview requires preparing a local PCM mix; time-remapped audio and non-unit-speed nested audio are not supported. The current source decoder clamps source-time requests to valid video bounds. See the roadmap for all retained requirements.

## AI connection

**Media processing is always local.** There is no cloud segmentation, tracking, rendering, retrieval dependency, or telemetry pipeline in the media engine. Core editing and export do not require a chat key.

Settings → AI connection accepts a compatible Chat Completions API base URL, its model ID, and **that provider's own key**. Discover models if supported, or enter a model manually. **Test tool use & save** performs a function-call test before enabling autonomous edits. The adapter uses bounded, non-streaming requests.

Credentials are masked and stored with Windows Credential Manager, scoped to the exact configured endpoint. Chat context contains IDs, layer data, timing and curves, excluding original media paths, media bytes, and credentials. Chat does not upload images. Project text is treated as data.

## Presets

True Motion reads selector values and keyframes from supported FFX presets in an optional local `True Motion/` folder. Obtain these preset files separately from their publisher. Applying a preset exposes its keys on the text layer. Move keys or edit their values in the timeline or Value Graph. See [True Motion compatibility](docs/PRESETS.md).

## Verify and package

```powershell
.\tools\verify.ps1
.\tools\build.ps1
```

The self-test generates real synthetic media, verifies timing/curves/transactions/persistence, and exports MP4s including a six-frame 4K composition. Results are in `artifacts/verification-report.json`. The short test is not a sustained performance benchmark. The full machine, checks, and visual review notes are in [docs/VERIFICATION.md](docs/VERIFICATION.md).

`build.ps1` produces a framework-dependent Windows x64 build under `dist/Kuro-0.1.6` and a ZIP. It includes the sample project and copies optional preset files when available locally. FFmpeg is a separate runtime dependency.

## License

OpenKuro's source and original assets are available under the [MIT License](LICENSE). Third-party dependencies and external preset files carry their own licenses.
