# Expanded-field rescue validation

`runtime/tests/forced_falls.py` exercises the USA game's actual fall detection,
respawn and CPU driving. Run it against a portable Linux product with a valid
slot-zero license and a working SDL display backend:

```sh
python3 runtime/tests/forced_falls.py \
  --product /path/to/portable-product --players 24 --course 8 \
  --log /tmp/rescue-game.log --report /tmp/rescue-report.json
```

The product must contain a build whose experimental guest layout supports the
requested count. The script launches its own headless process and compiles a
small Linux `LD_PRELOAD` helper using `cc` (override with `--cc`). Only that child
uses private files for its normal shared RAM backing. It retains the runtime's
shared aliases, page protections and allocation sizes. No production executable
or game asset is modified, and no debugger or process-memory permission is
required.

After racing starts, the script stops its child briefly, moves every kart's
current and previous physics position below the course, and resumes it. It does
not set rescue flags, call respawn functions, advance checkpoints or supply
controller input. A pass requires every slot to enter the game's respawn state,
return to course height, move at least 200 units after 120 simulation frames,
and advance its lap. The JSON report retains per-slot checkpoints and the
executable SHA-256, including an incomplete report on failure. This is one
course and one race mode per invocation; it does not certify every course or
online rescue synchronization.

The first expanded-field run exposed an additional twelve-entry limit:
`KMP::Holder<JGPT>::GetPosition` (USA `0x805144F8`) indexes twelve signed-byte
coordinate pairs directly with the player ID. Slots above twelve read adjacent
constants, and several CPUs repeatedly respawned outside the road. The native
wrapper samples the original twelve transformed positions and packs expanded
fields inside their footprint. The original twelve-player path, battle center
placement, and `255` center sentinel retain the original helper.

`RespawnGridTests` executes the actual translated helper and wrapper through
100 players, checking rotated footprints, distinct bounded positions, stock
placement, battle centers, the sentinel, registers and output guards. These
geometry checks complement the actual-game fall test; neither alone proves
all-course collision and checkpoint safety.
