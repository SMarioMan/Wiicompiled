# Linking BrainSlug modules for native translation

`link-brainslug` imports big-endian ELF32 PowerPC relocatable modules into the
existing Kamek overlay pipeline. This is a development tool for the LAN port;
it does not yet produce a playable LAN release.

With the project's verified `Assets/main.dol` and `Assets/StaticR.rel` installed:

```sh
dotnet run --project translator/src/Translator.Cli -c Release -- \
  link-brainslug --project projects/mkwii-ntsc-u/recomp.yml \
  --module /path/to/rmc-local-net.mod \
  --symbols-dir /path/to/brainslug/source/symbols \
  --module-guest-base 0x81800000 --out build/lan/Code.pul
```

The command checks the project's game hashes, resolves imported functions from
its function map and BrainSlug SDK XML signatures, and writes `Code.pul` plus
JSON containing hook addresses, module symbol offsets, resolved imports and
input/output SHA-256 hashes. Optional `--imports addresses.json` supplies explicit
bindings as a JSON object of symbol names to hexadecimal guest address strings.
Signature matching requires a unique executable match; ambiguous or missing
matches fail instead of guessing. Derived SDK data-symbol signatures are not
supported yet.

ELF `ADDR32`, `ADDR16_LO`, `ADDR16_HI`, `ADDR16_HA` and `REL24` relocations become
Kamek commands. Allocated sections retain their alignment and BSS starts zeroed.
Declared optional/mandatory function replacements create original-function
trampolines; calls to the replaced import use that trampoline. Moving a direct
branch-and-link preserves its original LR return PC and the scratch registers.
The continuation planner also registers return PCs materialized into LR.
Unsupported ELF layouts, relocations, exports and unsafe indirect linking entry
instructions fail explicitly.

The module image can move to another aligned guest base within PowerPC branch
reach. Native generated code can grow independently of guest address identities.
This does not relocate the original game's data structures or expand player
arrays. Runtime writes to PowerPC instructions must still be lowered into static
patches before translation; the LAN module performs such writes in its startup
and OSLink hooks. Linking alone cannot make those writes change native control
flow.

Validation includes loading a synthetic module through the production overlay
builder at two guest bases, original-function branches and LR continuations,
BSS, pointer/scalar relocations, malformed input, and signature ambiguity checks.
The original LAN v0.9 module and the attached script's modified module have also
been linked against the verified USA game (ten and twelve hooks respectively).
The native LAN port has joined two instances in one room and verified both
simulations advancing in an online VS race. Rendering and race completion remain
unverified; 24-player support is not implemented. See
[LAN build and test instructions](../mods/lan/README.md).

`--patches static-patches.json` additionally lowers explicit build-time writes.
Each record has `Operation`, `Address` and `Value` strings. References are hex
literals, `game:importName` or `module:symbolName`, with optional `+0xOFFSET`
terms. Supported operations are scalar writes, pointer/halfword relocations,
branches, `Copy32`/`Copy16` from clean game code and `CaptureBranchTarget` from
an original relative branch-and-link. Captures happen before any writes, so
module trampolines and original-call pointers cannot accidentally capture their
own replacements. The JSON hash is recorded in link provenance. The LAN builder
uses this interface for its startup and OSLink patches.

Module images now load after the embedded game data and before DOL constructors
or the guest entry point. The later REL initializer applies base patches without
recopying the module image, preserving any state changed by early hooks. Mod
constructors still run after the REL prolog. This ordering is necessary for the
LAN boot-argument hook, which runs before StaticR links.
