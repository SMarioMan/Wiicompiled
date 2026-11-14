# Expanded-race light pool ownership

This resource limit is independent of the racer layout arrays. Original NTSC-U
`LightMgrHolder::AcquireLight` (`8055D018`) takes the first available light,
moves it into the acquired list and sets its acquired flag. It returns null
when the available list is empty. `8055D084` performs the inverse operation.
Each kart requests two lights, with further item requests during a race.

`LightMgrHolderRace::LoadBLIGHT` (`8055D608`) constructs pooled lights with a
light-set ID derived from `index+8` and narrowed by `extsb` at `8055D6B8`.
Increasing the ID range would violate that representation. The new code leaves
all pool allocation and light IDs unchanged.

MKW100's light-pool source identified exhausted acquisition and reference-safe
sharing as discovery leads. Our wrapper delegates original acquisition first.
When that returns null during a race with more than twelve active racers, it
shares an already acquired light and records one additional owner. Release
suppresses the original return-to-free-list operation until the last owner.
The final release delegates the original guest function and clears its flag.

Bookkeeping is per holder, not a global light-reference map. The verified base
constructor (`8055CCA4`, PAL identity `80561024`) resets only that holder; the
race/menu/Mii subclasses call it. The reference constructor identity `80561688`
is actually the race subclass in our region map. Constructing another holder
must not clear a live pool's references. Race destruction (`8055DB40`) retires
only its generation, so an address reused during a guest callback is distinct.
All wrappers use the existing PAL-identity region mapping, with USA instructions
as the verification source. No reference PAL data addresses are imported.

`LightPoolOwnershipTests` runs the real USA acquire/release/destructor functions
and real NW4R list initialization, append, removal and iteration. Pool creation
is a fixture callback; its 120 slots represent IDs 8..127 without increasing the
original representation. The unwrapped acquire function independently returns
null on acquisition 121. The sharing path serves `2*N+16` owners per holder for
all required counts: 12, 13, 24, 25, 26, 48, 52, 64, 100, 116, 118, 127 and 128.

An independent owner model checks flags, list counts and untouched light bytes
after each acquire/release. Releases are shuffled, two holders remain live
together, and destruction/reconstruction at reused addresses repeats while
owners were still present in the previous scene. All checks pass. A twelve-racer
control with an intentionally exhausted one-slot pool retains the original null
result. All 951 translator unit tests pass after the native wrappers were added.

Remaining qualification: actual BLIGHT/resource loading, mixed karts/items,
rendering, scene exit and another live race. Shared lights use the position and
color of their last updater, so ownership safety does not prove identical visual
lighting. An entirely empty acquired pool still returns the original null; this
fix addresses exhaustion after valid lights have been acquired, not missing scene
resources. Complete 128-racer gameplay and 128-console networking remain open.
