# Effect backing capacities

The creator namespace, EGG group-slot table and NW4R activity-list table have
different capacities. Existing checked bootstrap operands at USA `806770A4`
and `8067713C` already use `players+4`, retaining our reserved-role model. At
128 that requires 132 creators/EGG slots and four NW4R groups each: 528 activity
records, exceeding the reference implementation's 130/520 minimum.

The original USA EGG constructor `80225184` stores the EGG count at `+4`, four
times that count at `+0`, and allocates/clears the creator-pointer table at `+28`.
`EffectCreatorStorageTests` now also passes 127/128, including all 132 actual
creator registrations, their IDs and four-group bases, plus table/object guards.

The previous fixture mocked the complete memory-manager creation routine. The
new `EffectActivityStorageTests` executes original USA `802246C8`, including its
arena-size arithmetic, temporary block allocator, four resource pool headers and
NW4R `EffectSystem::Initialize` (`800270A0`). It executes the actual activity-list
constructors, array-header construction, complete initialization loop and NW4R
list initialization. Its capacity is taken from the original EGG constructor's
four-times-group count, not a separate guessed constant.

The fixture passes all required counts: 12, 13, 24, 25, 26, 48, 52, 64, 100,
116, 118, 127 and 128. It verifies the 28-byte record stride, both lists' link
offset `0x14`, zero head/tail/count state, final group bounds, computed arena
allocation, original array header, saved registers and table/allocator/arena
guards over two fresh initialization cycles. The reserved creator count is read
from the existing bootstrap relocation metadata.

Other effect/emitter/particle pools are configured empty in this backing test;
their pool headers and the allocator still execute. Even empty arrays contribute
32 bytes each to the original budget before the fixed `0x220` reserve. This is
not an effect-heavy gameplay or teardown test.

The existing allocation propagation is correct for this path, so no new layout
or native workaround was added. Remaining work includes nonempty resource pools,
racer 127 effect lookup/draw/retirement, scene heap ownership and actual next-race
startup. Correct creator and activity capacities alone do not establish those.
