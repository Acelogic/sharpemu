# GTA V NID implementation progress

## Goal

Reach exact static Gen5 registration parity for all 1,432 GTA V application/runtime imports with Ghidra-backed contracts, without weakening failure behavior, then validate the result against GTA V and the existing multi-game test surface.

## Pinned inputs

- Integration branch: `codex/gta-v-nids`
- Integration worktree: `/Users/mcruz/Developer/sharpemu-gta-v-nids`
- Acelogic `main` base: `615bae08c2613b6b8363203b8c40f58e2bf6eac6`
- Current Acelogic fork-main sync: `b8a90e7` (merged into the integration branch as `5286387`)
- Remaining uncovered queue: `GTA_V_UNCOVERED_NIDS.csv`
- Coordinator manifest: `GTA_V_NID_SWARM_MANIFEST.json`
- Initial Acelogic-main queue: 911 unique uncovered application/runtime imports
- Pinned Aerolib symbol names: 1,418/1,432; the seven formerly catalog-unnamed queue entries now have exact Ghidra provider-function evidence without invented names
- Integrated from that queue on this branch: 821
- Remaining uncovered on this branch: 90
- Current static registration coverage: 1,342/1,432 (93.72%), up from 521/1,432 (36.38%) on the pinned main base
- Manifest lifecycle: 821 integrated, 90 named

The queue is a static import inventory. It is not yet a runtime call-frequency trace; `calls=0` means no runtime count has been established.

### Current static coverage by importing image

| Importing image | Gen5-registered NIDs | Unique imported NIDs | Coverage |
|---|---:|---:|---:|
| `eboot.bin` | 1,236 | 1,301 | 95.00% |
| `sce_module/libc.prx` | 90 | 104 | 86.54% |
| `sce_module/libSceJobManager.prx` | 138 | 146 | 94.52% |
| `sce_module/libSceNpCppWebApi.prx` | 88 | 95 | 92.63% |

These image rows overlap because the same NID can be imported by more than one image; they must not be summed. The deduplicated application/runtime union is the 1,432-NID denominator above.

## Current checkpoint

The generic blocked-SELF mapping fix, the expanded Variant-II static-TLS reservation, and the Ghidra-backed `sceKernelDirectMemoryQuery` enumeration fix are integrated. The current branch also contains 780 exact Gen5 provider-preferred registrations backed by selected Ghidra function records: 436 from GTA's shipped `libSceNpCppWebApi` provider and 344 from firmware providers analyzed on the Mac, rho, and Windows. Their generated HLE fallback is fail-closed; it returns `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED` and does not invent provider behavior.

Mac-local firmware Ghidra and an independent rho GTA-consumer Ghidra campaign proved the direct-memory-query contract used by GTA: flags `1`, a 24-byte output buffer, `[info+8]` continuation, and terminal result `0x8002000D`. The integrated fix returns containing-or-next direct allocations and uses that exact terminal result without inventing unproven coalescing or terminal-success behavior. On post-fix runs, all four GTA loops terminate at imports 419, 447, 463, and 473; execution advances beyond import 37,900.

Mac-local and independent rho provider Ghidra recovered `XlNp7jzGiPo` (`sceAgcDriverSetTFRing`) and `MM4IZSEYytQ` (`sceAgcDriverSetHsOffchipParam`) from `libSceAgcDriver.sprx`. Both semantic implementations are integrated. The Hs-offchip call uses the recovered two-`uint32` ABI, low-16-bit packing order, state gate, and error mapping; it is also provider-preferred when the exact guest export is available.

The post-provider x64 GTA run installed 1,956 direct bridges covering 482 unique NIDs. All 436 newly registered `libSceNpCppWebApi` NIDs resolved directly to GTA's shipped provider. The Hs-offchip call at import 39,003 received `(0, 0x1FF)`, returned zero, and cleared the former gate at `0x8002957516`; execution reached import 41,427. The other 344 provider registrations were not directly mapped because their firmware providers were absent in this run. Thirteen calls reached their explicit fail-closed fallback (12 AGC and one SystemService), with no invented success. The later stop is an unrelated, not-yet-attributed RenderThread access violation at guest RIP `0x805C273B7` while reading address zero. Full routing evidence is retained in [`docs/gta-v/provider-wave-runtime-20260718.md`](docs/gta-v/provider-wave-runtime-20260718.md); this is not a claim of full playability.

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
| AGC Hs-offchip parameter | `codex/gta-v-nids` | Ghidra-recovered `sceAgcDriverSetHsOffchipParam` contract, state, tests, and runtime gate | integrated as `740915e`; [Ghidra packet](docs/gta-v/agc-driver-hs-offchip-param.md) |
| Provider registration wave | `codex/gta-v-nids` | 780 exact Gen5, Ghidra-backed, provider-preferred registrations with fail-closed fallback | integrated as `0e4452d`; static coverage now 1,342/1,432 |
| Local reverse engineering | eight read-only Ghidra workers on the Mac | alternate/native variants for the remaining 23 small-provider NIDs | active; no integration-file ownership |
| Remote reverse engineering (Linux) | 40 two-core jobs in unique `/dev/shm` roots on `rho.cs.oswego.edu` | remaining 67 kernel/libc/POSIX contracts and provider evidence | active near the measured 80-core saturation point |
| Remote reverse engineering (Windows) | 16 one-core jobs in unique `%TEMP%` roots on `192.168.68.54` | remaining 23 small-provider NIDs and alternate/native variants | active at the measured throughput optimum |

No worker may edit this progress file, the central manifest, or the integration branch.

## Static cluster queue

| Cluster | Uncovered NIDs | Current lane |
|---|---:|---|
| Libc | 30 | rho Ghidra contract lane |
| Kernel | 19 | rho Ghidra semantic-HLE lane; kernel exports remain HLE-bound |
| POSIX | 9 | rho Ghidra contract lane |
| LibcInternal | 5 | rho Ghidra contract lane |
| AudioOut2 | 4 | Mac/Windows provider lane |
| LibcInternal;Libc | 3 | rho Ghidra contract lane |
| RazorCpu | 3 | Mac/Windows provider lane |
| Ajm | 2 | Mac/Windows alternate-provider lane; absent from the first Windows provider |
| Coredump | 2 | Mac/Windows provider lane |
| UlObjMgr | 2 | Mac/Windows provider lane |
| AppContent | 1 | Mac/Windows provider lane |
| AudioOut | 1 | Mac/Windows provider lane |
| Http | 1 | Mac/Windows provider lane |
| Ime | 1 | Mac/Windows provider lane |
| LibcInternalExt | 1 | rho Ghidra contract lane |
| NpTrophy2 | 1 | Mac/Windows provider lane |
| Pad | 1 | Mac/Windows provider lane |
| PlayerSelectionDialog | 1 | Mac/Windows provider lane |
| Random | 1 | rho Ghidra contract lane |
| Sysmodule | 1 | Mac/Windows provider lane |
| VideoOut | 1 | Mac/Windows provider lane |

The remaining CSV contains exactly 90 unique NIDs, and its NID set is identical to the 90 non-integrated manifest items.

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
- stay within the measured campaign points: 40 two-core Ghidra jobs on rho, 16 one-core Ghidra jobs on Windows, and eight one-core local Mac workers; scale again only after a new benchmark.

The rho smoke used only the 71,654-byte `libSceJobManager.prx` and completed in 20.09 seconds at 173% CPU with about 1.32 GiB peak RSS. It proved the ephemeral pipeline and cleanup, but stock Ghidra classified the PS5 SELF as a raw binary and recovered no real imports. Meaningful remote contracts therefore require a PS5 SELF loader or a locally reconstructed/decrypted ELF derivative before fan-out.

The Windows proof transferred only a locally reconstructed 1,334,184-byte sectionless libc ELF derivative, not the original SELF or the full game. A pinned Ghidra 12.1.2/JDK 21 run completed analysis in 30.783 seconds with eight analysis CPUs and about 1.40 GiB peak Java working set. It recovered 2,761 functions, 177,012 instructions, and three direct callers of the selected libc import. The later provider benchmark established 16 simultaneous one-core jobs as the throughput optimum at 0.4806 jobs/second, 82.92% average host CPU, and 100% peak CPU; 24-way concurrency was slower. Independent post-checks found zero campaign directories and zero campaign Java processes remaining. The retained capacity and cleanup records are [`windows-capacity-benchmark.json`](docs/gta-v/provider-evidence/windows-capacity-benchmark.json) and [`windows-cleanup-proof.json`](docs/gta-v/provider-evidence/windows-cleanup-proof.json).

The rho GTA campaign transferred only a 65,928,068-byte sectionless eboot derivative, not the original eboot or full game. Its eight-worker Ghidra run independently recovered all four direct-memory-query loops and their `0x8002000D` termination rule. Whole-program auto-analysis reached its 900-second cap, but the targeted import resolution and containing-function decompilation completed; the unique `/dev/shm` campaign directory was removed and a fresh glob check found zero residual directories. The compact hashes, address normalization, decompile evidence, measurements, and cleanup proof are retained in [`docs/gta-v/rho-direct-memory-query-ghidra.md`](docs/gta-v/rho-direct-memory-query-ghidra.md).

The rho AGC campaign transferred only the 141,176-byte reconstructed `libSceAgcDriver.sprx` provider. Three independent RAM-backed Ghidra passes recovered the public export, selected callback/helper, and initializer in 14.74-15.34 seconds each at roughly 0.83-1.17 GiB peak RSS. Cleanup traps removed every `/dev/shm/sharpemu-agc-settfring-*` root, and independent checks found zero residual campaign directories or Java processes. The Mac independently recovered the same control flow. The evidence and machine-readable contract are retained in [`docs/gta-v/agcdriver-settfring-ghidra.md`](docs/gta-v/agcdriver-settfring-ghidra.md) and [`docs/gta-v/agcdriver-settfring-contract.json`](docs/gta-v/agcdriver-settfring-contract.json).

The rho provider saturation campaign ran 40 two-core Ghidra jobs concurrently. It observed 80.35 cores in use with about 19.19 GiB peak aggregate RSS, no swap pressure, and no material I/O wait. It produced exact executable-body evidence for 191 AGC/AGC-driver/AMPR exports; 190 became provider registrations and the semantic `MM4IZSEYytQ` implementation was provider-preferred instead of duplicated. An independent cleanup check found zero campaign directories and zero Java workers. The evidence is retained in [`docs/gta-v/rho-provider-lle-ghidra.md`](docs/gta-v/rho-provider-lle-ghidra.md).

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
  - AGC Hs-offchip focused tests: 6/6 passed
  - libc `bsearch`/`strtoull` focused tests: 18/18 passed, including errno/TLS fault ordering
  - provider/NpCpp/direct-routing focused tests: 29/29 passed
- NID manifest/registration uniqueness check
  - manifest validator: 911/911 unique items valid
  - lifecycle: 821 integrated, 90 named
  - remaining CSV: 90/90 unique NIDs with module attribution and exact set equality to non-integrated manifest items
  - provider wave: exactly 780 Ghidra-backed `PreferLle` registrations plus one semantic HLE registration
- GTA V loader/import probe, then runtime unresolved trace
  - blocked-SELF `PT_DYNAMIC` translation: passed
  - static TLS reservation for the observed `0x13570` requirement: passed
  - guest entry and initial module initializers: reached
  - direct-memory-query enumeration contract: passed and runtime-verified across all four GTA loops
  - `sceAgcDriverSetTFRing` (`XlNp7jzGiPo`): former fatal gate cleared in the final x64 run
  - `sceAgcDriverSetHsOffchipParam` (`MM4IZSEYytQ`): former import-39,003 gate cleared with `(0, 0x1FF)`
  - all 436 new NpCppWebApi registrations resolved to direct guest-provider bridges
  - highest observed import ordinal: 41,427; later RenderThread access violation remains unattributed
- SharpEmu library and source-generator tests
  - SharpEmu.Libs.Tests after all current integrations: 755/755 passed
  - Release solution build: passed with 0 warnings and 0 errors
  - SharpEmu.SourceGenerators.Tests: 36/36 passed
  - SharpEmu.ShaderCompiler.Tests: 34/34 passed
- GTA V launch regression
- Existing game regressions where the changed subsystem is shared
