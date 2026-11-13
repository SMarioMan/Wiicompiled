# Expanded-player migration task list

Continue from branch `work`; preserve its existing checked guest-layout and LAN
implementation. The repository and verified game instructions define behavior.
Video subtitles are observations and design references, not build instructions.

## Release and relocation

- [ ] Revalidate both existing Docker builders with a supplied disc and move each
  complete output directory before testing it. Windows execution needs a Windows
  host; cross-compilation alone is insufficient. Fresh USA base exports now build
  on both platforms. Moved Linux first-run and twelve-player VS round-trip checks
  pass; moved Windows imports are accounted for, with native execution pending.
- [ ] Finish the USA capacity manifest without weakening instruction checks or
  setting `Complete` before all affected systems pass. Retain symbolic native
  branches and guest layout offsets; guest PCs are dispatch identities.
- [ ] Port verified migrations to other regions using their own binary evidence.
- [ ] Complete network wire layouts, packed teams, rating-sort scratch storage,
  participant masks and SDK limits; exercise 24 active LAN participants through
  joining, voting, racing, results, disconnects and a second race.
- [ ] **Lobby connection speed and reliability:** improve the connection algorithm
  for large rooms. Measure discovery, membership and complete peer connectivity
  separately; compare the original full mesh (276 links at 24 consoles) with a
  deterministic connection schedule and direct LAN transport. Bound retries and
  concurrent work, avoid reservation deadlocks, and verify late joins,
  disconnects, rejoining and race-start barriers. Benchmark repeated 12/24-player
  joins at normal timing; label clock-scaled functional results separately.
- [ ] Test larger capacities (including 100) separately from the 24-player target.
  Larger storage fixtures do not establish larger playable or network capacity.

## Reported gameplay issues

- [ ] **CPU roster:** extend the real CPU character/vehicle selection and duplicate
  avoidance beyond slot 12. Check weight-compatible vehicles and define repeat
  behavior after exhausting the eligible character pool. The expanded normal-menu
  counter/character selector now scan every slot, reserve non-CPU choices and
  exhaust eligible characters before balanced repeats. Native checks pass through
  100; normal-menu vehicle assignment and rendered races still need validation.
- [ ] **Results:** give every participant a distinct row and valid rank text;
  paginate or scroll as necessary. At 24, generated individual controls now use
  two columns, team controls have twelve rows per team, and reserved localized
  message IDs avoid unrelated strings. Original-loader checks and an actual
  24-player individual results screen confirm distinct, readable ranks through
  24th and the expected one-race points. Still check accumulated totals, team
  results and menu return.
- [ ] **Bullet Bill:** the overtaking-target lookup now follows the active field
  with exact authored small-field behavior. Native target/timer/drop-gate checks
  pass at 12, 13, 24, 48 and 100 capacity; visually verify rides and safe drop
  locations on courses before closing this issue.
- [ ] **Position indicators:** supply valid rank indicators beyond 12 instead of
  reusing the twelfth-place asset. The generator now composes original digit
  and ordinal artwork and expands both single/multiplayer texture-pattern
  animations. Original step-curve checks pass, and the original-style 24th
  indicator now renders correctly during an actual Luigi Circuit race. Still
  check the other ranks, split-screen variants and localization.
- [ ] **Item tables:** revalidate existing capacity-sized probabilities/roulette
  interpolation for all item settings and partial fields, plus rank-sensitive
  effects. Do not merely extend the lookup bound over twelve authored columns.
- [ ] **Starting grid:** replace unlimited backward extension with course-aware
  placement. The native KTPT wrapper now packs expanded fields inside the actual
  original twelve-racer footprint, with native coordinate/spacing checks through
  100. Check all courses, alternate starts, collision bounds and checkpoint state;
  footprint membership alone does not prove safe spawn coordinates.
- [ ] **Stranded CPUs / Lakitu:** distinguish bad initial coordinates, route or
  checkpoint state and rescue storage/limits. The actual-game forced-fall test
  exposed the original twelve-pair JGPT offset table; the bounded rescue wrapper
  now passes all 24 slots in Luigi Circuit VS and Mario Circuit team VS,
  including resumed movement and lap advancement. See [the test and limits](forced-fall-validation.md). Still check
  other courses, battle modes and online synchronization.

## Subtitle lessons to retain

The attached *Breaking The Limits: 24 PLAYERS AT ONCE!!* subtitles describe an
out-of-range Bullet Bill target table: 23rd may target first while 24th drops out
early. They also describe spreading the original item distribution across ranks,
waiting for the field to finish, and resource-loading limits with unique racers.

The *24 players at once... on custom tracks!* subtitles describe fixing the Bullet
Bill table and interpolating item probabilities between authored ranks. They also
report incorrect starting coordinates, team-state residue and unfinished podium
behavior. Use those as regression scenarios, not proof that WiiCompiled has the
same implementation or fixes. CTGP-R-style menus and scoring are inspiration;
their implementation is not present in this repository.

For detailed existing verification and limitations, see
[guest layouts](guest-layout-relocation.md), [Docker builds](building-docker.md)
and [LAN tests](../mods/lan/README.md).

## LAN wire-layout checkpoint

The SELECT packet now has symbolic fields for its fixed prefix, two physical
local-player records, settings word, packed teams, participant map and flags.
Team words grow in groups of twelve consoles, the map grows with capacity, and
packet alignment is preserved. Public/private team assignment, rating-sort
scratch frames, resets, copies, getters and voting comparisons use these fields.
The combined receive packet and all section-holder constructors use symbolic
section capacities; the original byte-packed controller arena remains packed.

Native storage/team fixtures pass at 12, 13, 24, 48 and 100 capacity. Actual voting
and host settings import pass at 12, 13 and 24. Larger live LAN counts remain
limited by 32-bit console masks; at 100, scanning repeats mask bits after console
63. Storage tests do not establish online support above that limit. Discovery
protocol 3 excludes earlier capacity-matched builds with incompatible SELECT
sections. RH1 now also has a capacity-sized serialized map, checked lengths,
relocated temporary packets and complete creator/import loops. Native fixtures
cover both buffers and snapshots through 24 active-addressable slots; the
remaining packet formats and full 24-active-peer gameplay still need testing.
A thirteen-instance attempt additionally exposed literal twelve-player DWC setup
and reservation admission limits. All three server setup calls (including the
main-loop call that bypasses the standalone method), public matching and the
connection callbacks now use the checked capacity. The next live run admitted
the additional console far enough to expose the friend-room manager's fixed
24-control storage. That page, its two slots per console, Mii/control counts,
active-player map, reset arrays and referencing callbacks are now migrated.
Native lifecycle checks pass through 100; the 24-player room uses the original
widgets in two rows. Live admission/rendering/voting retesting is pending.
