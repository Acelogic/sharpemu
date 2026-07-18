# GTA V NID implementation progress

## Goal

Implement evidence-backed SharpEmu support for GTA V imports without weakening failure behavior, then validate the result against GTA V and the existing multi-game test surface.

## Pinned inputs

- Integration branch: `codex/gta-v-nids`
- Integration worktree: `/Users/mcruz/Developer/sharpemu-gta-v-nids`
- Acelogic `main` base: `615bae08c2613b6b8363203b8c40f58e2bf6eac6`
- Static uncovered queue: `GTA_V_UNCOVERED_NIDS.csv`
- Coordinator manifest: `GTA_V_NID_SWARM_MANIFEST.json`
- GTA V queue size: 911 unique uncovered application/runtime imports
- Named by the catalog: 904
- Observed but unnamed: 7

The queue is a static import inventory. It is not yet a runtime call-frequency trace; `calls=0` means no runtime count has been established.

## Current checkpoint

The generic blocked-SELF mapping fix is integrated. GTA V now maps its eboot `PT_DYNAMIC` range through the containing payload at physical offset `0x3EF0090`, then reaches TLS registration. The next verified prerequisite is a Variant-II static-TLS span of `0x13570`, which exceeds SharpEmu's current `0x10000` startup reservation. A generic reservation fix is isolated and in progress. Runtime call ordering will be captured after that gate.

## Active lanes

| Lane | Branch/worktree | Ownership | Status |
|---|---|---|---|
| Integration | `codex/gta-v-nids` / `/Users/mcruz/Developer/sharpemu-gta-v-nids` | coordinator-owned manifest, queue, integration, regression | active |
| Loader prerequisite | `codex/nid-gta-loader` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-loader` | `SelfLoader.cs` and focused loader tests only | integrated as `e6e71ac` |
| TLS prerequisite | `codex/nid-gta-tls` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-tls` | shared Variant-II reservation and focused TLS tests only | implementing |
| libc evidence and implementation | `codex/nid-gta-libc` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-libc` | 20 approved libc math exports and tests only | implementing |
| Local reverse engineering | separate read-only KawaiiDRA projects on the Mac | libc/POSIX and NpManager evidence packets | active/queued |
| Remote reverse engineering (Linux) | ephemeral `/dev/shm` job on `rho.cs.oswego.edu` | portable headless-Ghidra proof and benchmarks only | smoke passed; semantic loader gate identified |
| Remote reverse engineering (Windows) | ephemeral temp job on `192.168.68.54` | portable headless-Ghidra proof and benchmarks only | preflight passed |

No worker may edit this progress file, the central manifest, or the integration branch.

## Static cluster queue

| Cluster | Uncovered NIDs | Initial disposition |
|---|---:|---|
| NpCppWebApi | 436 | reverse engineer and prioritize from runtime trace |
| AGC | 119 | reverse engineer and prioritize from runtime trace |
| libc | 64 | implement small high-confidence contracts first |
| AMPR | 46 | reverse engineer and prioritize from runtime trace |
| AGC driver | 27 | research pending runtime evidence |
| kernel | 19 | compare existing contracts and prioritize from runtime trace |
| JSON2 | 17 | contract clustering pending runtime evidence |
| NpWebApi2 | 17 | reverse engineer and prioritize from runtime trace |
| voice | 15 | research pending runtime evidence |
| NpManager | 10 | compare existing KawaiiDRA evidence |
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

- use a unique directory beneath `/dev/shm`;
- install/copy only the portable tooling and the smallest required binary slice or module;
- never transfer the whole game;
- register cleanup traps and remove the job directory on success or failure;
- return only reports, logs, scripts, and compact analysis artifacts;
- begin at no more than 8 independent jobs on rho and 4-6 on the Windows host, then scale only after measured memory and I/O behavior.

The rho smoke used only the 71,654-byte `libSceJobManager.prx` and completed in 20.09 seconds at 173% CPU with about 1.32 GiB peak RSS. It proved the ephemeral pipeline and cleanup, but stock Ghidra classified the PS5 SELF as a raw binary and recovered no real imports. Meaningful remote contracts therefore require a PS5 SELF loader or a locally reconstructed/decrypted ELF derivative before fan-out.

The local Mac remains responsible for integration, builds, runtime capture, final regression, and additional read-only KawaiiDRA evidence lanes. The two remote hosts add parallel workers; they do not replace the local coordinator.

## Validation gates

- Pinned-base build and test baseline: passed on 2026-07-18
  - Release solution build: passed (pre-existing catalog warnings remain)
  - SharpEmu.Libs.Tests: 567/567 passed
  - SharpEmu.SourceGenerators.Tests: 33/33 passed
  - SharpEmu.ShaderCompiler.Tests: 34/34 passed
- Focused tests for each implemented contract, including failure paths
- NID manifest/registration uniqueness check
- GTA V loader/import probe, then runtime unresolved trace
  - blocked-SELF `PT_DYNAMIC` translation: passed
  - current runtime gate: expand static TLS reservation beyond the observed `0x13570` requirement
- SharpEmu library and source-generator tests
- GTA V launch regression
- Existing game regressions where the changed subsystem is shared
