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

These checks do not qualify the original full loader, strict live result-page
accesses, rendered readability, leaving results or another race. Rebuild native
imports, per-game constants and build shards before gameplay validation.
128-console networking remains a separate unfinished migration.
