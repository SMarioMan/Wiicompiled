# Team-results reconstruction (2026-11-21)

Reconstructed against `11344508b1c8a1287973d7ec99841fa03c7f1b90` using the
user-supplied clean USA RVZ. Extracted `main.dol` and `StaticR.rel` match the
SHA-256 pins in `projects/mkwii-ntsc-u/recomp.yml`.

`TeamResult.items` and `addPoints` now use the existing half-capacity model.
Seventeen original instruction words pin construction, destruction, reservation,
load/update loops and computed field accesses. The animation wrapper retains
the original six-record unroll, team total and relocated child-animation loop,
then checks remaining score records independently. Diagnostic constructor and
destructor wrappers watch both inline team objects through their lifetimes.

The point-control generator preserves the three unrelated original variants and
message table, and gives every team row its own named point-control variant.
Both aspect-ratio transforms follow the generated row position and scale.

Validation in this instance:

- All 13 original-code team-storage tests passed at capacities 12, 13, 24, 25,
  26, 48, 52, 64, 100, 116, 118, 127 and 128.
- The complete declared-consumer proof passed, including twelve-player output
  equivalence and expanded C++ compilation.
- All 16 lifetime-wrapper cases passed, including both new TeamResult cases.
- The animation wrapper test passed with each additional row independently
  pending/finished, original pending delegation, signed scores and NaN behavior.
- All 977 translator unit tests and all 10 expanded-UI Python tests passed.
- Fresh 128-capacity assets generated from original USA files. Independent
  readback verified 64 team point variants, the three preserved unrelated
  variants and the original message table.
- The original resource loader resolved all point-control variants at 24 and 128
  and applied their corresponding team-row transforms in both aspect ratios.
- Original team-control loading reserved and populated every row and child point
  control at 12, 25 and 128 for both team IDs. The twelve authored animation-name
  pointers remained separate from row capacity.

Private Linux probes at `94c3750` finished a one-lap Team VS race with all 128
racers and entered the individual and team leaderboard pages. The team page had
64 populated row IDs per side. It animated team targets of 4,205 and 4,295 at one
point per update, which kept the original progression waiting for minutes in the
cloud probes. The expanded animation fix increases the display step with the
score budget while retaining the original per-row order, exact scores and child
animations. Original-code fixtures verify that both expanded sizes settle within
240 updates plus one update per row, and six populated rows retain retail timing.

The Linux rebuild with the score-tempo fix (`5e654d5`) finished one-lap Luigi
Circuit Team VS races with all 128 racers, rendered the individual and team
leaderboards, and reached the next race in both aspect ratios. The 16:9 run
used a 1280×720 window. The 4:3 run used a 960×720 window with
`widescreen = false` and `force_aspect = true`. Both displayed 64 rows per side,
with all 128 racer IDs represented once. Every row's live score matched its
target before the team page left the screen, with final totals of 4,205 and
4,295. The second race advanced to frame 328 at 16:9 and frame 332 at 4:3
with all 128 racers observed in both runs.

The executable SHA-256 was
`1b6ca3561df7127ac8760da5a17f3c32efafff262527df374b8827363b716447`.
These were normal-clock CPU-driven offline probes with ordinary controller input
through the results menus and read-only observation of guest scores and rows.
