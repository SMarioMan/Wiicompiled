# Expanded results assets

`build.py` derives results controls and text from a clean, extracted copy of the
user's disc. It ships no game artwork or message data. The experimental LAN
Docker workflow applies it to the release's `DATA` directory after validating
the layout, leaving the input image unchanged.

```sh
python3 mods/expanded-ui/build.py --data /path/to/extracted/DATA \
  --players 24 --report /path/to/expanded-ui.json
```

Individual VS, GP and time-trial controls gain named variants for every rank.
At 24, two columns retain the twelve authored vertical positions with half-size
rows. Team controls gain twelve rows per team in their existing vertical window.
Each row retains the original control flags and independent aspect-ratio
transforms. Native rank lookup uses reserved BMG IDs for ranks above twelve,
preserving the original score/control messages immediately after rank twelve.
English, French, Spanish, German, Italian and Japanese generated labels follow
the archive's language; unknown language suffixes receive decimal rank text.

The tool validates the original variant names and record structure, rebuilds
BMG indices and offsets, and verifies lossless U8/Yaz0 reconstruction before
replacing files. It refuses already-expanded or unrecognized templates. Use a
fresh disc extraction for each build. The report identifies changed files and
hashes. Capacities through 100 can be generated, but text readability and menu
behavior beyond 24 are not validated; more columns are not a pagination policy.

Python checks cover compression boundaries, ordinal rules and distinct row
variants. `ExpandedResultsAssetTests` executes the original USA variant loader,
control-transform loader and BMG index/text readers against freshly generated
assets. It checks every individual/team row, both aspect ratios and all 24 rank
labels in the English/French/Spanish archives. Rendering and complete results
flows still need gameplay validation. HUD indicator textures are separate work.
