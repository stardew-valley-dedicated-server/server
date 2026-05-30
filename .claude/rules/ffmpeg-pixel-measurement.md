---
paths:
  - "tests/**/Helpers/ContainerRecorder.cs"
  - "TestOverlay.cs"
  - "RenderingTests.cs"
  - "tools/.playground/recording-validator/**"
---

# Measure ffmpeg-rendered pixels with per-column `crop`, not a full-frame `format=gray` raw scan

When verifying glyph extent or pixel color in an ffmpeg-produced frame (overlay geometry, burn-in width, text baseline), sample each column with `-vf "crop=1:H:x:0" -f rawvideo -pix_fmt rgb24` and read the bytes, NOT a single full-frame `-vf format=gray -f rawvideo` scan indexed by an assumed width.

**Why:** Verifying the overlay burn-in this session, repeated full-width `format=gray` rawvideo scans reported phantom black pixels at the canvas edge (e.g. "rightmost black col x=599" on a 600px frame) that flatly contradicted direct `crop=1:1` RGB reads showing that same pixel was pure white `255 255 255`. The full-frame raw byte stream has row-stride/alignment the naive `v[r*w+c]` indexing doesn't honor, so the row pointer drifts and "black" lands at bogus columns — and a lavfi `color=white` source doesn't always read as luma 255 under the chosen threshold, compounding it. Cost ~5 misfired measurements (colon-escape test and TIME-string-width test both) before switching methods; the per-column `crop=1:H` + rgb24 reads were immediately reliable and repeatable.

**How to apply:** To find a text/box edge, loop candidate columns and crop a 1×H strip at each (`crop=1:40:$x:0`), decode as `rgb24`, count rows where `R<60`. The edge is where the count drops to 0. Trust `crop`-isolated RGB reads over whole-frame gray-rawvideo arithmetic; if the two disagree, the raw scan is wrong. For "is this single pixel the expected color" (e.g. the RenderingTests `(0,0)` white check), `crop=1:1:x:y` + rgb24 is the ground truth.
