# Expanded-player migration task list

Continue from branch `work`; preserve its existing checked guest-layout and LAN
implementation. The repository and verified game instructions define behavior.
Video subtitles are observations and design references, not build instructions.

## Release and relocation

- [ ] Revalidate both existing Docker builders with a supplied disc and move each
  complete output directory before testing it. Windows execution needs a Windows
  host; cross-compilation alone is insufficient.
- [ ] Finish the USA capacity manifest without weakening instruction checks or
  setting `Complete` before all affected systems pass. Retain symbolic native
  branches and guest layout offsets; guest PCs are dispatch identities.
- [ ] Port verified migrations to other regions using their own binary evidence.
- [ ] Complete network wire layouts, packed teams, rating-sort scratch storage,
  participant masks and SDK limits; exercise 24 active LAN participants through
  joining, voting, racing, results, disconnects and a second race.
- [ ] Test larger capacities (including 100) separately from the 24-player target.
  Larger storage fixtures do not establish larger playable or network capacity.

## Reported gameplay issues

- [ ] **CPU roster:** extend the real CPU character/vehicle selection and duplicate
  avoidance beyond slot 12. Check weight-compatible vehicles and define repeat
  behavior after exhausting the eligible character pool.
- [ ] **Results:** give every participant a distinct row and valid rank text;
  paginate or scroll as necessary. Check individual/team totals and menu return.
- [ ] **Bullet Bill:** the overtaking-target lookup now follows the active field
  with exact authored small-field behavior. Native target/timer/drop-gate checks
  pass at 12, 13, 24, 48 and 100 capacity; visually verify rides and safe drop
  locations on courses before closing this issue.
- [ ] **Position indicators:** supply valid rank indicators beyond 12 instead of
  reusing the twelfth-place asset; verify HUD, results and localization.
- [ ] **Item tables:** revalidate existing capacity-sized probabilities/roulette
  interpolation for all item settings and partial fields, plus rank-sensitive
  effects. Do not merely extend the lookup bound over twelve authored columns.
- [ ] **Starting grid:** replace unlimited backward extension with course-aware
  placement. Check all courses, alternate starts, collision bounds and capacities
  through 100; generated grid tables alone do not prove safe spawn coordinates.
- [ ] **Stranded CPUs / Lakitu:** distinguish bad initial coordinates, route or
  checkpoint state and rescue storage/limits. Force falls for every slot and
  verify rescue, checkpoint progress and subsequent lap completion.

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

## Next LAN wire-layout checkpoint

The current `RKNetSelectPacket` remains an opaque 56-byte type. Inspection of the
verified USA `SELECTHandler::ResetPacket` (`0x80658BB8`) establishes these ranges:

| Original packet offset | Storage |
| --- | --- |
| `0x00..0x0f` | Fixed prefix, cleared as four words |
| `0x10..0x1f` | Two physical local-player records, eight bytes each |
| `0x20..0x23` | Fixed word, cleared separately |
| `0x24..0x27` | Packed team word |
| `0x28..0x33` | Twelve player-to-console bytes, reset to `0xff` |
| `0x34..0x37` | Fixed suffix, reset to `ff 00 ff 00` |

`SELECTHandler::GetTeam` (`0x80658718`) reads the one team word at handler
offset `0x2c` (send packet offset `0x24`) and selects bit `2 * console + local`.
It still cannot read a second packed word. Expanding the cache count did not fix
that wire representation. Keep the two physical local records fixed when
expanding participant storage.

Migrate packet fields, team readers/writers, temporary sort/copy frames, packet
resets, nested handler strides and send/receive section sizes together. Apply the
existing checked packed loads/stores, array transfers and terminal pointer fills
only after matching their actual instruction spans. Re-run the full consumer
audit: existing references with literal offsets inside the previously opaque
packet may need more specific symbols. Preserve stock emission at twelve and
test packet bytes and bounds before attempting 24 active LAN participants.
