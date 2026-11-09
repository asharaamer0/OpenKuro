# Verification

Run `tools/verify.ps1` from the workspace. The Windows test executable writes `artifacts/verification-report.json`, including test results and timings. Tests cover frame timing, interpolation, selected keyframe commands, undo, project persistence, FFmpeg media analysis, local audio mixing, Rec.709 MP4 export, cancellation, and a short 4K render.

The True Motion integration checks run when the local preset pack is available. The rest of the suite runs from a clean checkout.

The UI checks render the dark and light workspaces at 1366 and 1920 pixels wide, check the full-height AI sidebar at 1366 and 1050 pixels, and exercise the Composition Settings popup by choosing a resolution, switching aspect ratio, and applying a fractional frame rate. The tests render a separate keyframe context menu for inspection. Preview images are under `artifacts/`.

The workspace was tested on Windows 11, a Ryzen 5 2600, 16 GB RAM, a Radeon RX 580, .NET 8 and FFmpeg 8.1.1. The report records synthetic test timings for this machine. Sustained playback, long-form A/V synchronization, a high-DPI device matrix, and a live compatible AI model connection remain unverified.
