# GTA V NID implementation progress

## Goal

Implement evidence-backed SharpEmu support for GTA V imports without weakening failure behavior, then validate the result against GTA V and the existing multi-game test surface.

## Pinned inputs

- Integration branch: `codex/gta-v-nids`
- Integration worktree: `/Users/mcruz/Developer/sharpemu-gta-v-nids`
- Acelogic `main` base: `615bae08c2613b6b8363203b8c40f58e2bf6eac6`
- Remaining uncovered queue: `GTA_V_UNCOVERED_NIDS.csv`
- Coordinator manifest: `GTA_V_NID_SWARM_MANIFEST.json`
- Initial Acelogic-main queue: 911 unique uncovered application/runtime imports
- Pinned Aerolib symbol names: 1,418/1,432; Acelogic labels 7 additional catalog-unnamed registrations, leaving 7 uncovered imports without a symbol name
- Integrated from that queue on this branch: 37
- Remaining uncovered on this branch: 874
- Current static registration coverage: 558/1,432 (38.97%), up from 521/1,432 (36.38%) on the pinned main base
- Manifest lifecycle: 37 integrated, 867 named, 7 observed-but-unnamed

The queue is a static import inventory. It is not yet a runtime call-frequency trace; `calls=0` means no runtime count has been established.

### Current static coverage by importing image

| Importing image | Gen5-registered NIDs | Unique imported NIDs | Coverage |
|---|---:|---:|---:|
| `eboot.bin` | 470 | 1,301 | 36.13% |
| `sce_module/libc.prx` | 90 | 104 | 86.54% |
| `sce_module/libSceJobManager.prx` | 78 | 146 | 53.42% |
| `sce_module/libSceNpCppWebApi.prx` | 62 | 95 | 65.26% |

These image rows overlap because the same NID can be imported by more than one image; they must not be summed. The deduplicated application/runtime union is the 1,432-NID denominator above.

## Current checkpoint

The generic blocked-SELF mapping fix, the expanded Variant-II static-TLS reservation, and the Ghidra-backed `sceKernelDirectMemoryQuery` enumeration fix are integrated. An x64 GTA V run processes 171,687 relocations, sets up 1,645 import stubs (including 502 LLE redirects), executes the guest entry point, and returns cleanly from the first module initializers.

Mac-local firmware Ghidra and an independent rho GTA-consumer Ghidra campaign proved the direct-memory-query contract used by GTA: flags `1`, a 24-byte output buffer, `[info+8]` continuation, and terminal result `0x8002000D`. The integrated fix returns containing-or-next direct allocations and uses that exact terminal result without inventing unproven coalescing or terminal-success behavior. On post-fix runs, all four GTA loops terminate at imports 419, 447, 463, and 473; execution advances beyond import 37,900. A combined stdout/stderr capture isolates the next fatal gate: unresolved `XlNp7jzGiPo` (`sceAgcDriverSetTFRing`) returns NOT_FOUND at `0x80029574A5`, and GTA immediately tests EAX and executes `int 0x41` at `0x80029574A9`. The stale-output loop is gone; the AGC-driver provider contract is now being recovered in Ghidra before any HLE change is accepted.

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
| NpManager async requests | `codex/gta-v-np-async` / `/Users/mcruz/Developer/sharpemu-gta-v-np-async` | Create/Delete/Abort/Poll registry and focused tests only | integrated as `f7105d4` |
| Local reverse engineering | separate read-only Ghidra projects on the Mac | libc/POSIX, NpManager, and GTA caller evidence packets | libc and NpManager packets complete; post-direct-query fault analysis active |
| Remote reverse engineering (Linux) | ephemeral `/dev/shm` job on `rho.cs.oswego.edu` | reconstructed eboot derivative and independent GTA caller report | passed; targeted report returned and cleanup independently verified |
| Remote reverse engineering (Windows) | ephemeral `%TEMP%` job on `192.168.68.54` | reconstructed libc ELF derivative and headless-Ghidra call-site proof | passed; cleanup independently verified |

No worker may edit this progress file, the central manifest, or the integration branch.

## Static cluster queue

| Cluster | Uncovered NIDs | Initial disposition |
|---|---:|---|
| NpCppWebApi | 436 | reverse engineer and prioritize from runtime trace |
| AGC | 119 | reverse engineer and prioritize from runtime trace |
| libc | 32 | implement small high-confidence contracts first |
| AMPR | 46 | reverse engineer and prioritize from runtime trace |
| AGC driver | 27 | research pending runtime evidence |
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
- NID manifest/registration uniqueness check
  - manifest validator: 911/911 unique items valid
  - lifecycle: 37 integrated, 867 named, 7 observed
  - remaining CSV: 874/874 unique NIDs with module attribution
- GTA V loader/import probe, then runtime unresolved trace
  - blocked-SELF `PT_DYNAMIC` translation: passed
  - static TLS reservation for the observed `0x13570` requirement: passed
  - guest entry and initial module initializers: reached
  - direct-memory-query enumeration contract: passed and runtime-verified across all four GTA loops
  - current runtime gate: recover `sceAgcDriverSetTFRing` (`XlNp7jzGiPo`) from firmware Ghidra evidence
- SharpEmu library and source-generator tests
  - SharpEmu.Libs.Tests after all current integrations: 712/712 passed
  - Release solution build: passed with 0 warnings and 0 errors
  - SharpEmu.SourceGenerators.Tests: 33/33 passed
  - SharpEmu.ShaderCompiler.Tests: 34/34 passed
- GTA V launch regression
- Existing game regressions where the changed subsystem is shared
