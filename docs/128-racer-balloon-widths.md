# Balloon pool counts and selected ordinals

The existing layout already expands the inline pool to `10*N` eight-byte records,
its per-player records and following fields. The remaining overflow came from
control values: header bytes `N`, `5*N`, `10*N`, byte-masked pool scans, and five
selected pool ordinals stored as bytes on the grab function's stack. Totals wrap
at 26 racers; the half-pool count wraps at 52. An ordinal above 255 also truncates
even after widening the loop bound.

Format-31 `ByteOperations` preserves the existing relocated pool and four-byte
header ABI. The header now names its actual byte fields. Nine proven count reads
load its player byte and multiply by five or ten in full-width IR. Legacy mirrored
bytes remain truncated; migrated control flow does not consume them. Eleven
proven pool-index masks and nine scaled pool offsets widen before SSA. Player IDs,
held-balloon counts, five slot indexes and group flags retain their byte semantics.
Rules activate when the configured pool exceeds the byte domain; original-capacity
code and configured capacities through 25 retain their original IR.

The grab function (`808659C4`) has five scratch bytes at SP+8. Two selected-index
stores (`80865A98`, `80865AF0`) and their indexed load (`80865B30`) now use
halfwords with a doubled index. Five halfwords use ten bytes, fitting the sixteen
bytes before the saved GPRs at SP+0x18 in its original 0x40-byte frame. No frame
size or saved-register offsets change. The patch verifies original words and IR,
checks the declared scratch bounds and rejects counts beyond the halfword range.

The independent unchanged-field audit first reported nine missing reads, including
`80591D8C` in the kart cleanup function (`80591BE8`), outside the manager. It now
checks each discovered read's exact word, source offset, count symbol and factor,
and checks that only the constructor writes the two mirrored count bytes. Its
literal-global scan and explicit manager receivers do not prove every possible
indirect/cached pointer consumer closed.

`BattleBalloonStorageTests` preserves the original five storage cases and every
active count through 24. Thirteen additional full-field cases exercise all required
capacities, including 26, 52, 127 and 128. Each runs both factory/direct construction
and preferred/fallback selection. At 128, all 1,280 object pointers are distinct;
high selected indexes survive both scratch paths, every player's held pointers
match pool ownership, and release/transfer/reset/calc/model/cleanup paths retain
memory and register guards. The kart cleanup fixture keeps its original frame,
loop and epilogue while branching around unrelated scene work at explicit seams.

All 20 balloon/static-layout integration cases pass, including the existing game
layout audit and the new count inventory. The hardened factor/word inventory also
passes separately. The width infrastructure and its conditional-loop SSA regression
pass all 962 translator unit tests. A scratch temporary initially acquired an
undefined loop phi; the fix uses the existing block-local temporary convention.

Rendered balloon construction and animation/actions remain fixture callbacks.
Score/state update, resource loading, actual battle completion, scene heap teardown
and subsequent race startup still need gameplay tests. The object's legacy byte
ordinal at +0xB0 is written by its constructor; no byte reads were found in the
inspected balloon implementation range. It is not widened or treated as proof
that external/online consumers are absent. Those consumers and future 128-console
balloon packet identifiers remain separate audits. No complete 128-racer gameplay
or networking qualification follows from these manager tests.
