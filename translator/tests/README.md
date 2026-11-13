# Translator tests

`Translator.Tests` is the default binary-free suite. It covers the decoder, IR/SSA, code
generation helpers, parsers with synthetic inputs, project-manifest loading, DOL-only image
construction, and generic entry-point translation.

Older asset-backed and host-C++-compiler-backed sources remain in `Translator.Tests`
but are excluded from its project file. Their game assets, compiler requirements
and private-method reflection checks are unsuitable for the default suite.

The expanded-player migration has a separate opt-in
`translator/tests/Translator.IntegrationTests` project. Its game-backed fixtures
require the verified USA `Assets/main.dol` and `Assets/StaticR.rel` from
`projects/mkwii-ntsc-u/recomp.yml`, plus a C++20 Clang compiler. Select that compiler
with `MKW_TEST_CXX` if it is not on `PATH`. For the Bullet Bill target regression:

```sh
MKW_TEST_CXX=/path/to/clang++ dotnet test \
  translator/tests/Translator.IntegrationTests -c Release \
  --filter FullyQualifiedName~BulletBillTargetTests
```

This executes the original target-calculation region and duration function with
fixtures for player-ID lookup and kart state. It checks every rank in every field
size through each configured capacity (12, 13, 24, 48 and 100), including safe-drop
gates. It does not launch or visually validate the complete game.

An external generic DOL can be included without adding it to the repository:

```powershell
$env:RECOMP_GENERIC_DOL = 'D:\path\to\main.dol'
dotnet test translator/Translator.sln -c Release
```

The test treats the file as a generic DOL: it reads the DOL entry point, builds a DOL-only image,
and translates that entry point without game-specific addresses or bindings.
