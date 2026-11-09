# True Motion compatibility

Place your local FFX files under `True Motion/` in the source checkout or next to Kuro.exe. The public repository includes the preset reader. Preset files come from their publisher under separate terms.

Kuro reads authored position, scale, opacity, blur, tracking, range-selector values and temporal keys directly from supported FFX files. Applying a preset creates editable timeline keys. The In + Out preset keeps all four authored keys. Existing preset layers can expose keys through the True Motion section in Properties.

The compositor implements character and word selectors, per-unit blur and native wiggly noise. These behaviors follow the structure described in [Adobe's text animation reference](https://helpx.adobe.com/after-effects/desktop/animating-text/text-animation/animating-text.html). Text layout, selector math and noise are native implementations. Frame-by-frame equivalence remains unverified. Import was developed against 16 True Motion text presets.

## AE-assisted inspection

`tools/inspect-presets.jsx` refuses to run against a nonempty project. In an empty After Effects instance, run it through File → Scripts → Run Script File. It creates one matching 1920×1080/30 fps, four-second composition per source preset, applies the real file, extracts recursive match names/values, keyframes, interpolation/ease data, expressions and samples every three frames, and writes JSON to `artifacts/preset-inspection/`. It does not save or overwrite an AE project.

For a reference comparison, capture frame sequences and compare them against Kuro using matching text, fonts, dimensions, frame rates and preset start times.
