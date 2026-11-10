# Architecture

## Native application

WPF/.NET 8 was selected because this machine has its Windows desktop SDK/runtime and FFmpeg installed. It supplies native filesystem pickers, Explorer drag and drop, accessibility peers, installed font enumeration, Windows scaling, native window management and a DirectX-backed workspace. No browser or web server hosts the application.

WPF `RenderTargetBitmap` rasterization is CPU-based; this build does not describe it as a GPU compositor. A future Direct3D compositor can replace `Engine/Renderer` behind the same project evaluator. The current choice establishes an executable, deterministic editing foundation without shipping an untested media framework or pretending that hardware acceleration exists.

## Boundaries

| Module | Responsibility |
| --- | --- |
| `Core/Model.cs` | Versioned durable project data, stable IDs, rational frame rate and numerical curves |
| `Core/Commands.cs` | Validated editor mutations, undo/redo, snapshots, revision checks and lifecycle events |
| `Core/Store.cs` | Atomic project/preferences writes, relative media paths and disposable cache locations |
| `Engine/Media.cs` | Local FFprobe metadata, FFmpeg thumbnail/PCM extraction and bounded sequential decoders |
| `Engine/Renderer.cs` | Shared source-time and animation evaluator, alpha layer composition and dedicated preview STA |
| `Engine/Jobs.cs` | Separate export STA, H.264 encoding, source-audio mix, genuine frame progress and stabilization |
| `AI/Connection.cs` | Provider boundary, model discovery, capability test, protected credentials and bounded command loop |
| `UI/Timeline.cs` | Visible-row timeline rendering, direct editing gestures, snapping and drop placement |
| `UI/GraphEditor.cs` | Curve evaluation visualization and native editing handles |
| `UI/Actions.cs` | Discoverable action registry and shortcut assignments |
| `MainWindow*`, `Styles.xaml`, `Theme.cs` | Native views, semantic tokens, focus/hover states, dialogs and layout preferences |

## Timing and coordinates

Clip boundaries and keyframes use integer composition frames. FPS is a numerator/denominator pair, including 30000/1001. Timecode is non-drop-frame frame-count notation; drop-frame display is not implemented. Source media offsets use seconds because decoders accept time bases; conversion from composition frames is performed from the original frame count, not by repeatedly adding floating point deltas.

Layer position is in composition pixels, relative to the layer's center. Anchor offsets are local source pixels. Scale is a percentage, rotation is degrees, opacity is clamped to 0–100. Positive Y is down. Transforms apply anchor offset, scale, rotation and position. Higher layers composite above lower ones. Parenting is not present.

Curves use absolute composition frame indices. Split clones the entire curve and preserves a preset's original animation origin. Moving a layer moves its keys and preset origin by the same integer delta. Linear, Hold and cubic Bézier evaluation are shared between graph, viewer and export. Bézier X is solved by bisection, with monotonic time handles; Y may overshoot. A speed graph numerically differentiates the same evaluator. It is an inspection graph; speed handles are not directly editable yet.

Nested composition visual time is converted through source seconds into the child frame rate. Circular references are rejected. Nesting is capped at 12. Precomposition moves the selected layer properties inside a full-length transparent child and leaves a unity-transform outer layer. Alternative property transfer modes are not implemented.

## Responsiveness

The UI passes snapshots to the preview worker. The pending queue is bounded to one request; obsolete renders are canceled. Results carry a request serial and exact frame index. UI presentation rejects late results. Playback drops video requests when rendering is busy and explicitly reports displayed versus requested frames. Audio is mixed locally to a revision-specific PCM cache before playback; Windows media position is the playback clock when audio is active. This is not yet a measured low-latency production playback engine.

Decoding, media probing, waveform analysis, PCM mixing, stabilization and encoding run outside the UI thread. The preview requests FFmpeg automatic hardware decoding when enabled and retries on CPU when needed. Scrubbing keeps one pending request, caches nearby decoded frames, and renders the final playhead position at the selected quality. Source decoders and frame caches are bounded. The compositor itself still rasterizes on the CPU.

The timeline uses custom retained drawing and culls offscreen rows. Its playhead is an independent visual. Asset lists use WPF recycling virtualization. Graph edits render drafts locally and commit one undo transaction on release. Viewer drag drafts change only the selected layer in an ephemeral project snapshot, then commit one transaction.

## Export and color

Export takes an immutable project snapshot. Preview resolution never changes export dimensions. The shared renderer supplies premultiplied BGRA frames flattened to black for ordinary MP4. FFmpeg encodes H.264/yuv420p with Rec.709 SDR metadata, square pixels and CRF quality. Original audio sources are trimmed, retimed with `atempo`, delayed and mixed at 48 kHz stereo into AAC. Time-remapped audio and non-unit-rate nested audio are excluded rather than falsely claimed as synchronized.

FFmpeg encodes and mixes directly into a unique `.partial.mp4` file. The completed file is then moved to the destination. Cancellation stops child processes and removes the partial file. Existing destinations are handled explicitly in the UI. Export validates dimensions, destination and free space.

## Local and remote boundary

Only the chat provider adapter contains runtime HTTP. Media engine code launches local executables and never makes HTTP calls. The current build has no learned media models and no remote fallback. Conventional stabilization is described accurately. Missing local inference features are identified in Settings and not represented by fake progress.

Provider credentials reside in Windows Credential Manager. The Exa reference credential has its own target and is never read as a generic chat-provider key. Arbitrary inference endpoints require their own protected credential and an actual tool test. Providers cannot invoke shell commands or access unrelated OS resources through the tool schema. Chat operations validate command names, IDs, parameter values, locks and revisions. There are no automatic retrieval tools.

## Reliability

Transactions snapshot durable data, validate and roll back on failure. Undo and redo retain 80 snapshots. Saves use a flushed temporary file and atomic replacement with a backup. Recovery autosaves every 30 seconds when dirty. Analysis caches and credentials are outside projects. Project open checks format version and composition basics; hostile-project hardening and migration tooling remain future work.
