# ASTRO BOT menu progress

Last updated: 2026-07-16

This is the canonical working journal. The compact decisive-experiment ledger is in
[`docs/astro-bot/experiments.md`](docs/astro-bot/experiments.md). Earlier
committed chronology remains available in Git history; recent uncommitted work
has been reduced to the decisive results below.

## Goal

Reach a correctly rendered, stable, interactive ASTRO BOT title/menu with the
original guest shaders. Replacement colors and title-specific bypasses are
diagnostic controls only.

## Current checkpoint

- Worktree: `/Users/mcruz/Developer/sharpemu-astro-playable-next`
- Branch: `codex/astro-playable-next`
- Base commit before this checkpoint: `fc5d762`.
- The original-shader boot sequence renders boot art, multiple controller-symbol
  animation frames, and the PS Studios wordmark. The F1 performance overlay is
  enabled by default.
- The exact live milestone `GAME: Level has started: title_controller_ship`
  passes reproducibly after the bounded host-buffer LRU fix.
- Semantic-aware interpolant mapping now renders the controller-symbol animation
  with coherent geometry and substantially more accurate brightness/color.
- The title/worldmap output is still a uniform red frame rather than the
  recognizable menu, and sustained title performance is about 1.2 FPS.
- E206 proves the four rotating 96 KiB CPU selector tables are populated once the
  title is live. The active boundary has moved downstream to ObjectUpdate output,
  Emitter output, and vertex record 24736.

## Solved blockers

| Area | Decisive evidence | Retained resolution |
| --- | --- | --- |
| Real render targets and MRT | E01-E02 | Decode the full Gen5 register state and retain typed two-target MRT support. |
| Missing imports and JSON ABI | E03, E44, E170 | Implement the required NIDs plus stable `sce::Json::Value`/`String` reference semantics, including `referValue(const String&)`. |
| First-use depth | E13 | Track initialization source and apply a compare-neutral first-use depth value. Do not use a title depth bypass. |
| Shader semantics | E26, E37 | Retain GFX10 literal FMA opcodes and EXEC-only `VCMPX` behavior. |
| Render-to-texture identity | E27, E57 | Reuse compatible UNORM/SRGB mutable views and preserve the storage-image lifecycle. |
| PS Studios video | E50 | Preserve the full FFmpeg-backed AvPlayer decode/callback/upload path during upstream merges. |
| Intro presentation starvation | E134 | Yield after ordered flips while keeping the four-frame head-plus-newest-three memory bound. |
| Sampled-image coherency | E143 | Refresh dirty promoted images and use sampled-stage transfer barriers for vertex, fragment, and compute consumers. |
| Large compute programs | E188-E190 | Decode using the AGC header's exact shader size instead of the old 4,096-instruction ceiling. |
| Post-sync title-start regression | E197-E201 | Replace the sticky 128 MiB host-buffer admission policy with a bounded global LRU that evicts cold idle allocations. |
| Video layout and packed target order | E208 | Honor extended NV12 pitch/plane layout and `CB_COLOR_INFO.COMP_SWAP`; retain the focused layout/format tests. |
| Pixel/vertex semantic linkage | E215-E216 | Match AGC semantic IDs, retain custom/flat controls, use unique host pixel locations, and fan guest vertex exports into every consuming attribute. |

## Decisive recent evidence

| IDs | Result | Conclusion | Evidence |
| --- | --- | --- | --- |
| E170-E171 | `referValue` removes JSON assertions, restores active title records and selector refill, and preserves the ordered intro; the title remains red. | Scene registration is fixed. Continue at the geometry-producer boundary. | `artifacts/astro-bot/forensics/e170-json-refervalue/` and Git history. |
| E187 | Original shaders show the wordmark and reach the exact title start with the F1 overlay visible. | The remaining visual blocker is after the intro. | Prior run recorded in Git history. |
| E188-E190 | Emitter dispatch plumbing is valid; shader `0x555F4F500` was truncated by the decoder. Exact-size decoding emits SPIR-V and dispatches `192x1x1` with GPU/global writes enabled. | Large-shader decoding is fixed; do not reopen the old instruction ceiling. | `artifacts/astro-bot/runs/20260716-160643-e190-emitter-after-size-bound/`. |
| E192-E193 | Upstream sync builds/tests, the intro survives, but exact title start regresses. | The regression entered through the sync rather than the intro or Emitter decoder. | `artifacts/astro-bot/runs/20260716-162846-e193-post-sync-full-title-visual/`. |
| E194, E196 | Before exact title start, selectors are zero and the builder only enters/maps/clears them. | Useful early-state evidence only; it is not evidence about the live menu. | `artifacts/astro-bot/runs/20260716-163734-e194-full-title-producer-chain/` and `.../20260716-165657-e196-selector-exec-ladder/`. |
| E197 | Detached pre-sync commit `890224a` reaches exact title start. | The large decoder/Emitter changes are exonerated. | `../sharpemu-astro-pre-sync-ab/artifacts/astro-bot/runs/20260716-170042-e197-pre-sync-decoder-title-ab/`. |
| E198-E199 | Both zero-cache and effectively unlimited-cache controls reach exact title start. | Reuse is valid; the bad behavior was sticky 128 MiB admission and title-stage churn. | `artifacts/astro-bot/runs/20260716-170813-e198-no-host-buffer-cache-title-ab/` and `.../20260716-171354-e199-unbounded-host-buffer-cache-title-ab/`. |
| E200-E201 | The bounded global LRU passes 193/193 library tests and reaches exact title start twice at the normal 128 MiB budget. | Host-buffer cache regression is fixed reproducibly. | `artifacts/astro-bot/runs/20260716-172145-e200-lru-host-buffer-cache-title-ab/` and `.../20260716-172407-e201-lru-host-buffer-cache-repeat/`. |
| E204 | Compact current-build evidence preserves the controller-symbol animation and PS Studios wordmark before the title frame. | Intro remains a regression gate, but capture timing is not the current work item. | `artifacts/astro-bot/runs/20260716-174618-e204-lru-visual-gate-final/`. |
| E206 | Exact title starts. The dead path at `0x800253C22` does not execute; gate `0x800253C42`, live refill `0x800253C48`, and the following store path execute. Each live 96 KiB table has 653 nonzero bytes; list counts are `28/0/0/0`, work counters are `24898/24898/24898/0/0`, and the indexed join selects 40 state records with active `+176` and `+188` gates. | The selector builder, CPU write visibility, and selector-to-state join work at the live title. The next unknown is the compute output, not selector population. | `artifacts/astro-bot/runs/20260716-180409-e206-live-selector-to-geometry/`. |
| E207 | Sandboxed launches reached `Window.Create` on managed thread 1 but blocked in `glfwInit -> NSApplication.run` before Cocoa's application-finished-launch callback. A stale E207 process made later retries non-singleton. Launching the same diagnostic build through a normal Terminal-owned `.command` completed `Window.Create`, entered initialization, attached keyboard input, selected the Apple M3 Max Vulkan device, reported Cocoa, presented the splash, started `ps_logo`, reached exact `title_controller_ship`, and loaded `worldmap`. | The missing emulator window was a macOS LaunchServices/Cocoa bootstrap problem, not headless rendering or an ASTRO shader regression. Gate Cocoa readiness within 10 seconds and run visible macOS tests from the interactive desktop session. | Failed control: `artifacts/astro-bot/runs/20260716-191348-visible-e206-literal/`; successful visible launch: `artifacts/astro-bot/runs/20260716-192736-terminal-visible/`. |
| E208 | Extended AvPlayer frames used a guest-reported aligned pitch while the HLE copied tightly packed NV12, and packed 2:10:10:10 render targets ignored `COMP_SWAP`. | Copy Y/UV rows into the declared 256-byte-aligned NV12 layout and decode component swap independently from format. These are generic video/target fixes with focused tests. | Source tests `AvPlayerNv12LayoutTests` and `VulkanRenderTargetFormatTests`. |
| E209-E211 | `V_INTERP_MOV_F32` was first attempted with `PerVertexKHR`, which MoltenVK cannot lower. Derivative reconstruction compiled only after moving derivatives before the divergent PC dispatcher, but the image remained tiled/duplicated. | Keep the standard barycentric path for ordinary interpolation. Do not retry `PerVertexKHR` on MoltenVK or emit derivatives inside divergent control flow. | `artifacts/astro-bot/forensics/e210-vinterp-derivative/`, `.../e211-vinterp-uniform/`. |
| E212-E214 | Immediate post-draw readback already contained the repeated bands. Shader dumps identified ES `0x50076BE00` and PS `0x50076D300`; treating their attributes as identity-mapped was the remaining false assumption. | Corruption originated before presentation. Decode the AGC semantic tables rather than tuning the presenter. | `artifacts/astro-bot/runs/20260716-221034-e212-vinterp-uniform-readback/`, `.../20260716-223129-e214b-vinterp-vertex-program/`. |
| E215 | Static headers prove PS semantics `0,2,3` map to VS outputs `0,2,3`; input 3 is custom/flat and carries the packed-normal values. The runtime registers are exactly `0x000,0x002,0x423`. Controller geometry/color becomes coherent, but other shaders expose duplicate host locations. | The earlier conclusion that packed VS parameter 3 was unused was wrong. Generic semantic mapping is required. | `artifacts/astro-bot/runs/20260716-225754-e215-semantic-interpolant-mapping/`. |
| E216 | Host locations keyed by pixel attribute plus vertex-export fan-out eliminate duplicate `locn0` declarations. Fourteen interpolation and two AGC mapping tests pass. A visible original-shader run renders the corrected controller sequence, reaches exact title start, loads `worldmap`, and reports no MoltenVK/pipeline errors; the final frame remains uniform red. | Stage linkage is fixed without title-specific shader replacement. Resume at the existing title producer/composition boundary; the menu is not rendered yet. | `artifacts/astro-bot/runs/20260716-230809-e216-unique-host-interpolants/`. |

## Corrected conclusions: do not repeat

- A zero selector snapshot before the exact title-start marker is expected early
  state, not proof that the live menu selector producer is broken.
- The selector clear at `0x800253858` is normal setup. Do not bypass it.
- E206 supersedes the live-menu interpretation of E194/E196: the refill and gate
  paths do execute once scene lists become active.
- Depth, raster coverage controls, pixel exports, final composition, and the
  blue/striped format issue are downstream. Do not reopen them until vertex
  record 24736 is populated.
- The guest-zero ray/BVH dispatches are not proven title producers. Do not force
  their workgroup counts or fake ray intersections without an address link.
- Do not set `SHARPEMU_DISABLE_IMPORT_LOOP_GUARD=1`.
- Screenshot cadence/classification is not the current bottleneck. Use no-screen
  probes while following the producer chain.
- `--no-screenshot` disables only capture; it does not make SharpEmu headless.
- On macOS, if `GLFW Vulkan loader wired` is not followed by `GLFW windowing
  platform in use: Cocoa` within roughly 10 seconds, stop the run as a Cocoa
  bootstrap failure instead of waiting for a guest milestone.
- PS inputs are not identity-mapped. For the corrected intro shader, attribute
  sources are `0,2,3`, and source 3 is the custom packed-normal payload.
- Never declare two Vulkan fragment inputs at the same host location when guest
  controls alias one vertex parameter. Keep pixel locations unique and duplicate
  the source value in the vertex stage.

## Current producer chain

```text
scene lists               live (28 active records)
  -> 96 KiB selectors     live (653 nonzero bytes/table)
  -> indexed state table  live (40 selected records; active gates)
  -> CS 0x50740A700       dispatch/translation known; output now unclassified
  -> paired 1.5 MiB outputs
  -> large Emitter CS     exact-size decode and dispatch fixed
  -> rotating 16 MiB geometry buffers / record 24736
  -> ES 0x5002A9A00 position export
  -> final composition and blue/striped color handling
```

## Next experiment

1. At a live exact title, hash/read back the complete paired 1.5 MiB outputs of
   compute shader `0x50740A700` after the fence, not just their heads.
2. If either output changes, trace only its address-filtered consumers into the
   large Emitter and verify the rotating 16 MiB targets plus exact record 24736.
3. If both stay zero despite E206's active selectors/gates, use KawaiiDra on the
   first active sample/store path and inspect lane reachability, sampled values,
   EXEC survival, and the writable binding.
4. Once record 24736 is nonzero, validate ES `0x5002A9A00`; only then return to
   packed-half exports, component swaps, and final render-to-texture composition.

The E206 no-screenshot control was:

```sh
python3 scripts/astro-test.py test \
  --build never \
  --tag e206-live-selector-to-geometry \
  --timeout 200 --stability 20 --retries 2 \
  --no-screenshot --no-require-ps-studios \
  --env SHARPEMU_TRACE_GUEST_EXEC_ADDRS=0x800253C22,0x800253C2A,0x800253C42,0x800253C48 \
  --env SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_SHADER_ADDRESS=0x50740A700 \
  --env SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_SPEC=2,8,4,6,276,176,180,184,188 \
  --env SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_INTERVAL=8 \
  --env SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_CPU_WRITES=1
```

## Validation and artifact policy

- Release `osx-x64` publish passed for E216; the transplanted worktree Release
  build passes with zero warnings and zero errors.
- Library tests: 205/205 passed (including 2/2 AGC mapping tests).
- Shader tests: 27/27 passed (including 14/14 interpolation tests).
- Harness tests: 6/6 passed.
- No SharpEmu process remained after E216.
- Retain the PR #216 baseline at
  `artifacts/astro-bot/baselines/pr216/attempt-01-contact-sheet.png`.
- Retain compact proof for E190, E193, E194, E196, E198-E201, E203, E204, and
  E206. Raw per-frame PNG directories and superseded E191/E195/E202/E203b/E205
  runs were pruned after their conclusions were recorded.
- The current `runs/` tree fell from about 2.15 GB to 33 MB. No source, baseline,
  compact sheet, decisive log, or manifest was removed.

## Acceptance criteria

- Release builds with no errors and focused tests pass.
- Original shaders show the ordered intro and reach exact `title_controller_ship`.
- Recognizable menu geometry renders without title-specific bypasses.
- The menu remains stable for at least 60 seconds.
- Keyboard or controller input operates the menu.
- MRTs and the final image are neither uniformly black/red nor blue/striped.
