# Expanded race and selection assets

`build.py` derives results controls and text from a clean, extracted copy of the
user's disc. It ships no game artwork or message data. The experimental LAN
Docker workflow applies it to the release's `DATA` directory after validating
the layout, leaving the input image unchanged.

```sh
python3 mods/expanded-ui/build.py --data /path/to/extracted/DATA \
  --players 24 --report /path/to/expanded-ui.json
```

Individual VS, GP and time-trial controls gain named variants for every rank.
The retail twelve-player assets remain unchanged.
At 24, two columns retain the twelve authored vertical positions with half-size
rows. Team controls gain twelve rows per team in their existing vertical window.
Above 24, individual results use compact two-line cards: rank/name above the
portrait and points or time. Team results use compact single-line name, portrait
and score cards in two team regions. At 128, both presentations have **16 rows
and 8 total columns**, with all participants visible together. Smaller expanded
capacities choose the row/column combination that maximizes uniform card scale
inside the authored window; ties prefer fewer empty slots, then fewer columns.
The planner includes the team selection flash's 1.03 scale in its bounds.
Team parents use the same two half-screen regions in both aspect ratios.
Each card retains the original control flags, retail fonts/materials and
independent aspect-ratio transforms. All named panes remain present for guest
code; compact cards suppress only the steering-wheel decoration and redundant
team `pts` label. Native rank lookup uses reserved BMG IDs for ranks above twelve,
preserving the original score/control messages immediately after rank twelve.
English, French, Spanish, German, Italian and Japanese generated labels follow
the archive's language; unknown language suffixes receive decimal rank text.

The tool validates the original variant names and record structure, rebuilds
BMG indices and offsets, and verifies lossless U8/Yaz0 reconstruction before
replacing files. It refuses already-expanded or unrecognized templates. Use a
fresh disc extraction for each build. The report identifies changed files and
hashes. Capacities through 128 can be generated. Geometry checks cover every
card, team-parent region and selection-flash bounds at 25, 26, 48, 52, 64, 100,
116, 118, 127 and 128. In-game rendering/readability and complete results flows
above 24 still require qualification; generated geometry is not that proof.

Python checks cover compression boundaries, ordinal rules and distinct row
variants. `ExpandedResultsAssetTests` executes the original USA variant loader,
control-transform loader and BMG index/text readers against freshly generated
assets. It checks every individual/team row, both aspect ratios and all 24/128 rank
labels in the English/French/Spanish archives. The 128 case also checks compact pane fonts, preserved retail font/material
sections and original textures, three-digit artwork, and team-parent transforms.
Rendering and complete results flows still need gameplay validation. HUD indicator animations also gain one texture/frame per rank. New digit and
ordinal glyphs come from the original position artwork, with the original
non-podium palette used for borrowed podium digits; no replacement font is used.
The original first twelve textures remain byte-for-byte unchanged. IA4, IA8 and
RGB5A3 inputs are decoded and new glyph composites use RGB5A3. Pillow is required
(`python3-pil` in the Docker builders); no external font package is needed.
The original game step-curve reader checks every generated texture index. HUD
rendering still needs gameplay verification.

The vote and rating screens in `MenuMulti.szs` and `MenuSingle.szs` gain named
`Vote00` / `Member00` card variants through capacity. At 24 they use three columns
of eight cards within the original vertical window. Card artwork, Mii images and
text resources come from the retail controls. Clean input must include both menu
archives; all changes are validated before any archive is replaced.
