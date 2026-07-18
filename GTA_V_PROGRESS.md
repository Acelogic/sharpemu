# GTA V NID implementation progress

## Goal

Implement evidence-backed SharpEmu support for GTA V imports without weakening failure behavior, then validate the result against GTA V and the existing multi-game test surface.

## Pinned inputs

- Integration branch: `codex/gta-v-nids`
- Integration worktree: `/Users/mcruz/Developer/sharpemu-gta-v-nids`
- Acelogic `main` base: `615bae08c2613b6b8363203b8c40f58e2bf6eac6`
- Current Acelogic fork-main sync: `b8a90e7` (merged into the integration branch as `5286387`)
- Remaining uncovered queue: `GTA_V_UNCOVERED_NIDS.csv`
- Coordinator manifest: `GTA_V_NID_SWARM_MANIFEST.json`
- Initial Acelogic-main queue: 911 unique uncovered application/runtime imports
- Pinned Aerolib symbol names: 1,418/1,432; Acelogic labels 7 additional catalog-unnamed registrations, leaving 7 uncovered imports without a symbol name
- Integrated from that queue on this branch: 40
- Remaining uncovered on this branch: 871
- Current static registration coverage: 561/1,432 (39.18%), up from 521/1,432 (36.38%) on the pinned main base
- Manifest lifecycle: 40 integrated, 864 named, 7 observed-but-unnamed

The queue is a static import inventory. It is not yet a runtime call-frequency trace; `calls=0` means no runtime count has been established.

### Current static coverage by importing image

| Importing image | Gen5-registered NIDs | Unique imported NIDs | Coverage |
|---|---:|---:|---:|
| `eboot.bin` | 473 | 1,301 | 36.36% |
| `sce_module/libc.prx` | 90 | 104 | 86.54% |
| `sce_module/libSceJobManager.prx` | 78 | 146 | 53.42% |
| `sce_module/libSceNpCppWebApi.prx` | 62 | 95 | 65.26% |

These image rows overlap because the same NID can be imported by more than one image; they must not be summed. The deduplicated application/runtime union is the 1,432-NID denominator above.

## Current checkpoint

The generic blocked-SELF mapping fix, the expanded Variant-II static-TLS reservation, and the Ghidra-backed `sceKernelDirectMemoryQuery` enumeration fix are integrated. An x64 GTA V run processes 171,687 relocations, sets up 1,645 import stubs (including 502 LLE redirects), executes the guest entry point, and returns cleanly from the first module initializers.

Mac-local firmware Ghidra and an independent rho GTA-consumer Ghidra campaign proved the direct-memory-query contract used by GTA: flags `1`, a 24-byte output buffer, `[info+8]` continuation, and terminal result `0x8002000D`. The integrated fix returns containing-or-next direct allocations and uses that exact terminal result without inventing unproven coalescing or terminal-success behavior. On post-fix runs, all four GTA loops terminate at imports 419, 447, 463, and 473; execution advances beyond import 37,900.

Mac-local and independent rho provider Ghidra then recovered `XlNp7jzGiPo` (`sceAgcDriverSetTFRing`) through export `0x6FF0`, selected callback `0x6F90`, and validation/ioctl helper `0x9C20`. The integrated implementation applies the recovered base-Prospero size cap and validation order, records accepted ring state, preserves prior state on failure, and writes no guest output. A final x64 GTA run clears the former fatal return at `0x80029574A5`, starts the RAGE worker threads, and advances to import 39,003. The next fatal runtime gate is unresolved `MM4IZSEYytQ` (`sceAgcDriverSetHsOffchipParam`) at return `0x8002957516`; GTA faults at `0x800295751A` with the unresolved NOT_FOUND result in RAX. This is a later call in the same AGC initialization sequence, proving the TFRing gate was removed without claiming full launch success.

## Active lanes

| Lane | Branch/worktree | Ownership | Status |
|---|---|---|---|
| Integration | `codex/gta-v-nids` / `/Users/mcruz/Developer/sharpemu-gta-v-nids` | coordinator-owned manifest, queue, integration, regression | active |
| Loader prerequisite | `codex/nid-gta-loader` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-loader` | `SelfLoader.cs` and focused loader tests only | integrated as `e6e71ac` |
| TLS prerequisite | `codex/nid-gta-tls` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-tls` | shared Variant-II reservation and focused TLS tests only | integrated as `84652f1` |
| libc math implementation | `codex/nid-gta-libc` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-libc` | 20 approved libc math exports and tests only | integrated as `0c84a2f` |
| libc core implementation | `codex/nid-gta-libc-core` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-libc-core` | 12 approved libc math/RNG/string/time exports and tests only | integrated as `6fb1d12` |
| Direct-memory-query implementation | `codex/nid-gta-direct-query` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-direct-query` | firmware/GTA Ghidra contract and kernel implementation/tests | integrated as `ce35c99`; GTA loop removal runtime-verified |
| NpManager premium callbacks | `codex/nid-gta-np-premium-callbacks` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-np-premium-callbacks` | two firmware-proven callback exports and focused tests only | integrated as `f92ed50` |
| NpManager async requests | `codex/gta-v-np-async` / `/Users/mcruz/Developer/sharpemu-gta-v-np-async` | Create/Delete/Abort/Poll registry and focused tests only | integrated as `f7105d4`; [Ghidra packet](docs/gta-v/npmanager-async-ghidra.md) |
| libc search/conversion | `codex/gta-v-libc-deferred` / `/Users/mcruz/Developer/sharpemu-gta-v-libc-deferred` | Ghidra-exact `bsearch` and `strtoull` contracts and tests | integrated as `eb7a842` plus errno-order fix `8302781`; independent review passed |
| AGC TFRing | `codex/gta-v-agcdriver-tfring` / `/Users/mcruz/Developer/sharpemu-gta-v-agcdriver-tfring` | `sceAgcDriverSetTFRing` contract, state, and focused tests | integrated as `63f3515`; [Ghidra packet](docs/gta-v/agcdriver-settfring-ghidra.md) |
| Local reverse engineering | separate read-only Ghidra projects on the Mac | libc/POSIX, NpManager, AGC provider, and GTA caller evidence packets | current packets complete; next AGC fatal gate identified |
| Remote reverse engineering (Linux) | ephemeral `/dev/shm` job on `rho.cs.oswego.edu` | reconstructed eboot derivative and independent GTA caller report | passed; targeted report returned and cleanup independently verified |
| Remote reverse engineering (Windows) | ephemeral `%TEMP%` job on `192.168.68.54` | reconstructed libc ELF derivative and headless-Ghidra call-site proof | passed; cleanup independently verified |

No worker may edit this progress file, the central manifest, or the integration branch.

## Static cluster queue

| Cluster | Uncovered NIDs | Initial disposition |
|---|---:|---|
| NpCppWebApi | 436 | reverse engineer and prioritize from runtime trace |
| AGC | 119 | reverse engineer and prioritize from runtime trace |
| libc | 30 | implement small high-confidence contracts first |
| AMPR | 46 | reverse engineer and prioritize from runtime trace |
| AGC driver | 26 | `SetHsOffchipParam` is the next runtime-fatal Ghidra target |
| kernel | 19 | compare existing contracts and prioritize from runtime trace |
| JSON2 | 17 | contract clustering pending runtime evidence |
| NpWebApi2 | 17 | reverse engineer and prioritize from runtime trace |
| voice | 15 | research pending runtime evidence |
| NpManager | 5 | implement only completed firmware-Ghidra contracts; defer provider-backed behavior |
| POSIX | 9 | implement only where host/guest semantics are proven |
| video recording | 9 | research pending runtime evidence |

Counts above use the generated swarm-manifest clustering, which can group a few import-module spellings differently from the raw CSV.

## Implementation contract

Every implementation must have:

1. A pinned source or binary-evidence reference.
2. A recovered signature and parameter/output contract.
3. Explicit success, failure, and side-effect behavior.
4. Focused positive and negative tests.
5. No unconditional success stub, invented output, or silent state mutation.

Large subsystems remain evidence/research lanes until this contract is met. The coordinator integrates one reviewed commit at a time and updates the manifest only after validation.

## Remote-worker policy

`rho` is suitable for parallel headless-analysis jobs: it exposes 88 CPUs, roughly 125 GiB RAM, and a 63 GiB empty `/dev/shm`. `DESKTOP-RAAKAQJ` (`192.168.68.54`) adds 32 logical CPUs, roughly 191 GiB RAM, and ample temporary disk. It currently has Java 17 but no Ghidra, so its jobs require an ephemeral JDK 21 and Ghidra 12.1.2 bundle. Remote jobs must:

- use a unique directory beneath `/dev/shm` on rho or `%TEMP%` on Windows;
- install/copy only the portable tooling and the smallest required binary slice or module;
- never transfer the whole game;
- register cleanup traps and remove the job directory on success or failure;
- return only reports, logs, scripts, and compact analysis artifacts;
- begin at no more than 8 independent jobs on rho and 4-6 on the Windows host, then scale only after measured memory and I/O behavior.

The rho smoke used only the 71,654-byte `libSceJobManager.prx` and completed in 20.09 seconds at 173% CPU with about 1.32 GiB peak RSS. It proved the ephemeral pipeline and cleanup, but stock Ghidra classified the PS5 SELF as a raw binary and recovered no real imports. Meaningful remote contracts therefore require a PS5 SELF loader or a locally reconstructed/decrypted ELF derivative before fan-out.

The Windows proof transferred only a locally reconstructed 1,334,184-byte sectionless libc ELF derivative, not the original SELF or the full game. A pinned Ghidra 12.1.2/JDK 21 run completed analysis in 30.783 seconds with eight analysis CPUs and about 1.40 GiB peak Java working set. It recovered 2,761 functions, 177,012 instructions, and three direct callers of the selected libc import. An independent post-check found zero campaign directories and zero campaign Java processes remaining. A conservative campaign limit is four Windows jobs at eight CPUs each, or four to six jobs at four to six CPUs each.

The rho GTA campaign transferred only a 65,928,068-byte sectionless eboot derivative, not the original eboot or full game. Its eight-worker Ghidra run independently recovered all four direct-memory-query loops and their `0x8002000D` termination rule. Whole-program auto-analysis reached its 900-second cap, but the targeted import resolution and containing-function decompilation completed; the unique `/dev/shm` campaign directory was removed and a fresh glob check found zero residual directories. The compact hashes, address normalization, decompile evidence, measurements, and cleanup proof are retained in [`docs/gta-v/rho-direct-memory-query-ghidra.md`](docs/gta-v/rho-direct-memory-query-ghidra.md).

The rho AGC campaign transferred only the 141,176-byte reconstructed `libSceAgcDriver.sprx` provider. Three independent RAM-backed Ghidra passes recovered the public export, selected callback/helper, and initializer in 14.74-15.34 seconds each at roughly 0.83-1.17 GiB peak RSS. Cleanup traps removed every `/dev/shm/sharpemu-agc-settfring-*` root, and independent checks found zero residual campaign directories or Java processes. The Mac independently recovered the same control flow. The evidence and machine-readable contract are retained in [`docs/gta-v/agcdriver-settfring-ghidra.md`](docs/gta-v/agcdriver-settfring-ghidra.md) and [`docs/gta-v/agcdriver-settfring-contract.json`](docs/gta-v/agcdriver-settfring-contract.json).

The local Mac remains responsible for integration, builds, runtime capture, final regression, and additional read-only Ghidra evidence lanes. The two remote hosts add parallel workers; they do not replace the local coordinator.

## Validation gates

- Pinned-base build and test baseline: passed on 2026-07-18
  - Release solution build: passed (pre-existing catalog warnings remain)
  - SharpEmu.Libs.Tests: 567/567 passed
  - SharpEmu.SourceGenerators.Tests: 33/33 passed
  - SharpEmu.ShaderCompiler.Tests: 34/34 passed
- Focused tests for each implemented contract, including failure paths
  - blocked-SELF loader tests: 13/13 passed
  - static-TLS focused tests: 7/7 passed
  - libc math focused tests: 77/77 passed
  - libc core focused tests: 109/109 passed
  - NpManager premium callback focused tests: 7/7 passed
  - direct-memory-query focused tests: 16/16 passed
  - NpManager async-request focused tests: 13/13 passed; concurrency case repeated 20 times in the isolated lane
  - AGC TFRing focused tests: 8/8 passed
  - libc `bsearch`/`strtoull` focused tests: 18/18 passed, including errno/TLS fault ordering
- NID manifest/registration uniqueness check
  - manifest validator: 911/911 unique items valid
  - lifecycle: 40 integrated, 864 named, 7 observed
  - remaining CSV: 871/871 unique NIDs with module attribution
- GTA V loader/import probe, then runtime unresolved trace
  - blocked-SELF `PT_DYNAMIC` translation: passed
  - static TLS reservation for the observed `0x13570` requirement: passed
  - guest entry and initial module initializers: reached
  - direct-memory-query enumeration contract: passed and runtime-verified across all four GTA loops
  - `sceAgcDriverSetTFRing` (`XlNp7jzGiPo`): former fatal gate cleared in the final x64 run
  - current runtime gate: recover `sceAgcDriverSetHsOffchipParam` (`MM4IZSEYytQ`) from firmware Ghidra evidence
- SharpEmu library and source-generator tests
  - SharpEmu.Libs.Tests after all current integrations: 739/739 passed
  - Release solution build: passed with 0 warnings and 0 errors
  - SharpEmu.SourceGenerators.Tests: 33/33 passed
  - SharpEmu.ShaderCompiler.Tests: 34/34 passed
- GTA V launch regression
- Existing game regressions where the changed subsystem is shared
