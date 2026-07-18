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

The current SharpEmu loader does not expose GTA V eboot imports because its fallback for a blocked SELF `PT_DYNAMIC` segment reads the logical file offset instead of translating through the containing SELF segment. A generic loader correction and focused regression test are in progress first. After that lands, GTA V will be run again to establish the actual unresolved-call order and prioritize implementations.

## Active lanes

| Lane | Branch/worktree | Ownership | Status |
|---|---|---|---|
| Integration | `codex/gta-v-nids` / `/Users/mcruz/Developer/sharpemu-gta-v-nids` | coordinator-owned manifest, queue, integration, regression | active |
| Loader prerequisite | `codex/nid-gta-loader` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-loader` | `SelfLoader.cs` and focused loader tests only | implementing |
| libc evidence and implementation | `codex/nid-gta-libc` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-libc` | approved libc export files and tests only | evidence review |
| Remote reverse engineering | ephemeral `/dev/shm` job on `rho.cs.oswego.edu` | portable headless-Ghidra proof and benchmarks only | probing |

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

## Rho policy

`rho` is suitable for parallel headless-analysis jobs: it exposes 88 CPUs, roughly 125 GiB RAM, and a 63 GiB empty `/dev/shm`. Remote jobs must:

- use a unique directory beneath `/dev/shm`;
- install/copy only the portable tooling and the smallest required binary slice or module;
- never transfer the whole game;
- register cleanup traps and remove the job directory on success or failure;
- return only reports, logs, scripts, and compact analysis artifacts;
- begin at 8-12 independent jobs and scale only after measured memory and I/O behavior.

The local Mac remains responsible for integration, builds, runtime capture, and final regression. Rho adds parallel workers; it does not replace the local coordinator.

## Validation gates

- Pinned-base build and test baseline: passed on 2026-07-18
  - Release solution build: passed (pre-existing catalog warnings remain)
  - SharpEmu.Libs.Tests: 567/567 passed
  - SharpEmu.SourceGenerators.Tests: 33/33 passed
  - SharpEmu.ShaderCompiler.Tests: 34/34 passed
- Focused tests for each implemented contract, including failure paths
- NID manifest/registration uniqueness check
- GTA V loader/import probe, then runtime unresolved trace
- SharpEmu library and source-generator tests
- GTA V launch regression
- Existing game regressions where the changed subsystem is shared
