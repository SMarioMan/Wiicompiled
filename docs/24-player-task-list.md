# Expanded-player migration task list

The target is **128 racers in vanilla Mario Kart Wii**, including the LAN mod.
Offline and LAN qualification have separate milestones. Keep racer capacity,
console capacity and network mask widths separate. See [the reference comparison
and implementation record](128-racer-comparison.md) and [the LAN validation
checkpoint](lan-expanded-validation.md).

- [x] Implement the auditor foundation: metadata-driven scalar read/write watches,
  object generations, overlap classification, log/strict JSON and controlled stale
  translated accesses. Stack lifetimes include canceled fibers; ArchiveMgr, AIManager, ItemManager, DriverManager, InputManager and the balloon manager have
  automatic constructor/destructor/release watches.
- [ ] Integrate remaining object/heap lifetimes and bulk paths and continue the
  instrumented gameplay discovery loop. The 128-capacity executable completes
  13/24/25-active VS probes with no hits in its exercised watches; this does not
  qualify all heap families or 128-active gameplay.
- [x] Relocate both ArchiveMgr racer arrays and retained state; all required storage
  capacities and 188 reviewed field operands pass. Mixed-resource loading and
  external/indirect consumer gameplay qualification remain open.
- [x] Migrate battle ranking scratch arrays and all nine signed position consumers
  in `GMDataBattle::HandlePositionTracking`; original-function fixtures pass every
  required capacity with strict stack auditing. Live battle qualification remains
  open.
- [x] Migrate the independent audio actor-sorting stack tables and their saved
  state after the 128-active Balloon Battle startup exposed an overflow. All
  required storage boundaries and a strict original-code control pass; rebuilt
  live battle qualification remains open.
- [x] Verify existing Coin Runners frame lowering at 116/118/127/128; active-racer
  gameplay coverage remains separate from the tested stack/storage paths.
- [x] Add ownership-safe light sharing on expanded-race pool exhaustion; original
  acquire/release/list tests pass every required count and repeated lifetimes.
- [x] Verify original EGG/NW4R backing allocation and all 528 activity records at
  128, plus all 132 creator registrations. Nonempty particle/effect pools, live
  high-ID draws/destruction and scene ownership remain open.
- [x] Widen known balloon pool count/index consumers and selected ordinals through
  128 active racers, retaining relocated storage and the four-byte header ABI.
- [x] Guard both inlined KMG battle-duration lookups in Raceinfo construction;
  the existing standalone helper guards did not cover them. Original constructor
  and helper checks pass all capacities, arenas and both modes. Full-duration live
  battle reruns remain required.
- [ ] Qualify balloon object resources/legacy-byte consumers and live battle
  state/scoring/lifecycle; live effects, light visuals and 128-racer heap ownership
  also remain open.
- [ ] Qualify the capacity boundary set and complete 128-entry single-screen
  results in 16:9 and 4:3, including teardown and another race.


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
  The `be5eb75` normal-timing candidate now completes all 24 room memberships
  and SDK connections at 63.09 seconds. Race/results/reconnection qualification
  and repeated admission comparisons remain open.
- [ ] **Lobby connection speed and reliability:** improve the connection algorithm
  for large rooms. Measure discovery, membership and complete peer connectivity
  separately; compare the original full mesh (276 links at 24 consoles) with a
  deterministic connection schedule and direct LAN transport. Bound retries and
  concurrent work, avoid reservation deadlocks, and verify late joins,
  disconnects, rejoining and race-start barriers. Benchmark repeated 12/24-player
  joins at normal timing; label clock-scaled functional results separately. The
  reservation queue now preserves age across retries, selects oldest requests
  with fair ties and prevents idle arrivals from bypassing queued peers. Native
  regressions cover these cases; repeated live comparisons remain pending.
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
  keep every racer visible together on one screen. Implement a compact 128-entry
  grid without pagination, scrolling, tabs or rotating subsets. The asset generator
  now supplies a 16-row, 8-column layout at 128, choosing readable card scale for
  smaller capacities and retaining original retail fonts/materials/digit artwork.
  Qualify its individual and team presentation in actual 16:9 and 4:3 rendering.
  At 24, generated individual controls now use
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

The disconnected-racer mask now uses one 32-bit word per 32 racer slots.
Constructor and reset paths clear all words, the departure callback writes each
racer's word, and packet-manager probes keep the full racer index live while
testing a single bit. Original-code controller and packet fixtures exercise all
four words at 128 racers, including several racers mapped to one console.
The same controller callback tracks departed console IDs in a separate packed
field and clears the associated packet holders through ID 127 in fixtures.
The SDK still stores 32 peers per room and exposes one-word connection and
suspend masks. The controller callback test does not change those SDK limits.

A thirteen-instance attempt additionally exposed literal twelve-player DWC setup
and reservation admission limits. All three server setup calls (including the
main-loop call that bypasses the standalone method), public matching and the
connection callbacks now use the checked capacity. The next live run admitted
the additional console far enough to expose the friend-room manager's fixed
24-control storage. That page, its two slots per console, Mii/control counts,
active-player map, reset arrays and referencing callbacks are now migrated.
Native lifecycle checks pass through 100; the 24-player room uses the original
widgets in two rows. Live admission/rendering/voting retesting is pending.

## Online selection-page checkpoint

The vote, rating and hidden selection-manager pages now have capacity-sized
player/card storage, moved state and movie fields, checked allocation sizes and
expanded initialization/destruction paths. Both menu archives derive additional
vote/member variants from their original retail cards; 24 cards use a three-by-eight
grid. Consumer proofs and asset layout tests pass. Retest actual voting, rating
presentation and the transition into racing with all 24 peers before closing the
network milestone.

See [the measured LAN checkpoint](lan-expanded-validation.md) for normal-timing
two-/twelve-console race passes and the unresolved 24-console reservation failures.

The larger-room reservation policy now widens the SDK's six-second wait only
in expanded builds for each other recently discovered or SDK-known peer, including
early growth through twelve members. Stock twelve-capacity builds retain the
original deadline. The allowance is bounded below the join watchdog, with
unchanged timer origins/retry counts. Native policy tests pass; repeat live
24-console joins before marking the timeout problem resolved.

The latest clock-scaled run reached all 24 SDK links at 187.45 seconds, then
exposed twelve-byte controller participant-map imports that left slots 13..24
inactive at race creation. Both RH1/SELECT copies and reset paths now operate on
the full map, with native checks through 100. Repeat the live transition before
claiming a 24-participant race.

## Low-priority extended goals

These follow completion of the base increased-player-cap multiplayer LAN work.

- [ ] **Visible automated LAN races for recording:** allow `MKW_TEST_LAN_CPU=1`
  without `--headless` in `runtime/src/main.cpp`, and add an optional `--visible`
  flag to `mods/lan/tests/local_pair.py` that omits `--headless` when launching
  instances. Keep the changes minimal, preserve default headless behavior and
  existing CPU-driving logic, and verify that both visible players drive
  autonomously and the race completes. This convenience feature remains below
  the 128-racer development work in priority.

- [ ] **Optional course-vote abstention patch, selected at build time:** add a
  WiiCompiled build argument that enables abstention in multiplayer course
  voting. Make abstention the default selection instead of Random. Select among
  actual votes when any player votes; use Random only when every player abstains.
  Preserve host/client agreement, UI styling and the unpatched build behavior.
- [ ] **Automatic joining of an ongoing hosted session, selected at launch:** add
  a launch argument so a freshly started auto-launch session finds and joins the
  hosted game seamlessly, including when a race is already in progress. Handle
  membership admission, current race/state synchronization, appropriate spawning
  and reconnection before qualifying mid-race joining.
- [ ] **Shared-speaker audio mixing across local instances, selected at launch:**
  add a launch setting that coordinates the mix across sessions on one machine.
  Reduce sound-effect loudness, play music only on the host, and have each
  player's own instance play their character, engine and other player-owned
  sounds so those sounds are not duplicated by other sessions. Match the balance
  and behavior of the game's native four-player split-screen mix across a much
  larger collection of local sessions. Validate host changes, joins/leaves,
  independent controls and the combined audible result.
