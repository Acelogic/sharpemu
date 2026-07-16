# ASTRO BOT title/menu investigation

## Goal

Reach a correctly rendered, stable, interactive ASTRO BOT title/menu using the original guest shaders. Diagnostic replacement colors are controls only and are not an acceptable final fix.

## Current state

- Durable worktree: `/Users/mcruz/Developer/sharpemu-astro-playable-next`
- Branch: `codex/astro-playable-next`
- Upstream base: `864cbb0` (`[AGC/Vulkan] Extend PS5 runtime and rendering compatibility (#216)`)
- Local rendering investigation changes are intentionally uncommitted and have not been pushed.
- The game boots, decodes the PS Studios video, and has reached `title_controller_ship` and `worldmap` in prior controlled runs.
- Original shaders now render the ordered controller-symbol animation and PS Studios wordmark again. E143's full-build gate contains three distinct 3D frames spanning 2.298 seconds, controller symbols, and a wordmark score of `0.797292`.
- AGC and the shader translator previously disagreed about runtime scalar metadata. A shared interleaved `(byteBias,dwordCount)` layout fixes that black regression without `OpArrayLength` or its invalid MoltenVK hidden binding.
- Ordered flips now yield to presentation before newer guest work drains. E134 recorded 35 enqueued flips, 35 presented flips, and zero coalesced flips while retaining the four-frame head-plus-newest-three memory bound.
- E143 completes `LoadLevelResources: title_controller_ship` and advances the target compute shader beyond its former repeatable sequence-3172 stall to at least sequence 5179 without a crash. Presentation still becomes a uniform red frame, and the title vertex stage has not executed.
- E162 independently revalidates the repaired intro with original shaders: multiple distinct 3D animation frames, controller symbols, and the PS Studios wordmark appear in order before `title_controller_ship` and `worldmap`. The title remains a solid red frame.
- E170 implements the missing `sce::Json::Value::referValue(const String&)` export identified with KawaiiDra. The live title now creates 30 active scene records, fills its rotating selector tables, advances the scheduler work counters, and reaches the explicit title-record producer.
- E171 revalidates the complete ordered intro after that fix. The visible title remains a uniform bright-red frame, so the menu is not rendered yet.

## Current blocker

The immediate intro regression, title-resource synchronization stall, and empty scene-record registration path are resolved. The remaining menu blocker is now the output boundary between compute shader `0x50740A700` and the geometry consumed by export shader `0x5002A9A00`: the 1920x1080 scene graph still presents a solid-red title frame instead of recognizable menu geometry.

E99-E102 corrected the earlier pixel-control-flow diagnosis. Replacing only the fragment shader with solid magenta still produced zero, while replacing the vertex stage with a fixed fullscreen triangle or replacing only ES `0x5002A9A00`'s position export filled the target with magenta. Coverage is lost in the original vertex position path, before the pixel shader matters.

E104 captured the original exported clip position as exactly `(0,0,0,1)` for every vertex, collapsing all triangles to a point. E108 found the pre-projection values `v5/v6/v8/v24` already zero. E109 then moved to PC `0x110`: EXEC was active, but the first position values loaded through `s16` were zero. E110 proved the vertex index is valid (`24736`) and in range for a 16 MiB, 64-byte-stride descriptor, while E111 sampled that exact 64-byte backing record and found it genuinely all zero on both rotating buffers.

E135 retested that boundary after both fixes. Compute shader `0x50740A700` runs with `gpu=True`, `global_writes=True`, and `VBfeI32`, with no `Vop3Raw149`; both ping-pong outputs and exact record-24736 addresses remained zero in that run. E136-E139 then found a sharper coherence boundary: `0x555F59000` is initially uploaded as an all-zero 8 MiB Vulkan texture, while later raw guest-memory probes reproducibly contain up to 1,353 nonzero sampled bytes.

E141-E143 add generic dirty tracking, inline refresh, and correct vertex/fragment/compute sampled-image transfer barriers. E143 proves these changes remove the former sequence-3172 stall and let title resource loading complete. E144 observes 42 complete 1.5 MiB writebacks through compute sequence 2913: all report `changed_bytes=0`, even after the raw guest-image probe becomes nonzero and 12 inline refreshes execute.

E145 closes the host-coherence question. A nonzero 8 MiB refresh payload (4,467 nonzero bytes, hash `0x4BEADF509FCEED46`) and the exact post-fence Vulkan image match byte-for-byte, while that same dispatch's complete `0x553C41DD0` output remains uniformly zero. The first bad boundary is now inside compute `0x50740A700`, not guest-memory tracking, detiling/upload, image identity, layout, or transfer synchronization. The target ES `0x5002A9A00` still never appears, so record 24736 remains unobserved.

E146 bypasses the translated runtime dword-count check for only compute `0x50740A700`; the full outputs remain unchanged. Runtime bounds metadata is therefore not suppressing the stores. The next distinction is whether any lanes reach the six sample sites, whether their normalized coordinates hit the sparse nonzero texels, and whether a nonzero sample survives EXEC to the final store.

E147 finds no overlapping decoded DMA or `WRITE_DATA` packet that seeds either 1.5 MiB ping-pong output. That result does not classify sample reachability: the four gate fields are not in those buffers.

E148's PC-`0x924` capture does not survive into the zero output, but this alone does not prove that PC executed or that all six sample sites were skipped. Static IR locates the real control inputs: binding 2 at `0x400904180` uses 8-byte mapping records whose second dword selects a 276-byte state record in binding 6 at `0x400BEC340`; gate dwords at state offsets 176, 180, 184, and 188 condition the sample sites. An outer branch at PC `0x22C` may skip all sample/store paths.

E149 confirms four base-indirect argument blocks remain zero and have no observed decoded packet or translated-shader writer, but those dispatches may legitimately contain no work and are not yet causally connected to the title producer. E150-E151 then sharpen the direct input boundary: the 4.5 MiB state table at `0x400BEC340` is not empty. It contains 1,572 sparse nonzero bytes near its end, while a separate 884,736-byte input at `0x40082C140` is fully zero. A zero 32-byte head was therefore misleading.

E152 performs the decisive indexed join. All 12,288 valid binding-2 mappings select one unique binding-6 state record, with zero out-of-range indices; that selected record has zero dwords at all four gate offsets. Compute `0x50740A700` is therefore following its guest control flow and deliberately skipping every gated sample path. Sparse nonzero state elsewhere is never selected. The generic join helper's focused tests pass 2/2.

E153-E154 find no decoded DMA, `WRITE_DATA`, `COPY_DATA`, or translated writable binding for either mapping or state before consumption. A broader shader inventory reproduces the same boundary after one discarded transient native exit: only the target compute binds those addresses, read-only. At that point CPU writes, untranslated commands, and resource identity remained unclassified; E156-E158 refine the CPU side below.

E155 follows the resources through `ps_logo` and `LoadLevelResources: title_controller_ship`. Across 25/26 snapshots, four rotating mapping tables at `0x400904180`, `0x4009F4200`, `0x400AE4280`, and `0x400BD4300` remain completely zero with the same hash; all mappings select one empty state record and all four gates stay zero. The shared state-table hash changes repeatedly, proving that state evolves while its selector tables do not. The two full visual attempts miss the wordmark and show mostly black/red frames, but their full 4.5 MiB per-dispatch hashing is a likely timing perturbation. This is not counted as a confirmed intro regression until a clean low-overhead control also fails.

E156-E158 validate the bounded first/every-sixteenth probe and CPU write watch. Guest CPU writes reach both the shared state table and each rotating mapping-table base, but the mapping snapshots stay uniformly zero. The state table is not merely alive: by occurrence 16 it contains nonzero gate fields in 73, 4, 1, and 52 records respectively. The failure is specifically that no mapping record selects any of those populated records.

E159-E161 test the four zero indirect dispatches only as a diagnostic lead. Forcing one workgroup exposed `DS_ADD_RTN_U32`, then `S_TRAP`, then an apparent MIMG opcode `0x66`. Raw instruction recovery corrected that last result: GFX10 stores MIMG OP[7] in bit 0, which the decoder dropped, so the true opcode is `0xE6` (`IMAGE_BVH_INTERSECT_RAY`). These are ray/BVH workers with guest-zero dispatch dimensions, not proven selector-table producers. Do not fake ray intersections or keep forcing them without a concrete writable-buffer link. Generic `DS_ADD_RTN_U32` lowering and the split MIMG opcode decode have focused test coverage; the ray operation remains explicitly unsupported.

E163-E164 identify the selector lifecycle in guest CPU code. Guest function `0x8002537D0` resolves the rotating per-object buffer and the 98,304-byte selector table, then clears the first dword of all `0x3000` eight-byte records at `0x800253858`. The same function normally refills selectors from active, previous-retired, and current-retired scene-record lists. KawaiiDra confirms that this clear is expected setup, not the defect.

The decisive E164b register/list capture found a valid companion per-object buffer (`r14=0x40082C140`) and selector buffer (`r15=0x400904180`), but all scanned list counts were initially zero. E169 then exposed the upstream cause during the real title level: 77 unresolved calls to NID `wLsJlmgEIaI` and 45 `Json.cpp:399` assertions.

KawaiiDra analysis in E170 identifies that NID as `sce::Json::Value::referValue(const String&)`. The generic HLE implementation returns a stable child reference for a present member and null for a missing member, matching the guest caller and firmware behavior. After the fix, unresolved calls and assertions fall to zero, the active list grows to 30 records, work counters reach `59860/59860/59860/0/0`, the full scheduler/spawn ladder executes, and explicit producer `0x800258340` runs. The selector tables now contain 785-829 nonzero bytes; by occurrence 512 they select 62 distinct state records, including 15 active `+176` gates and 12 active `+188` gates. The prior empty-list/empty-selector blocker is therefore closed.

E170 did not enable the exact output/readback traces needed to determine whether those active selectors make either 1.5 MiB ping-pong output nonzero or populate record 24736. E171 proves the intro still passes but the first and final title frames remain uniformly bright red. The active question is no longer whether title objects exist; it is whether the restored producer reaches and survives the target compute stores.

## Next experiment

Run the narrow post-fix geometry producer/output probe through several seconds after `Level has started: title_controller_ship`.

Capture:

- compute shader `0x50740A700` with `gpu=True`, `global_writes=True`, and no `Vop3Raw149`;
- complete post-fence writeback summaries for paired 1.5 MiB buffers `0x553C41DD0` and `0x553DC1DD0`;
- the rotating 16 MiB consumers at record 24736, including exact addresses `0x5540C45D0` and `0x5550C45D0` where applicable;
- the original export shader `0x5002A9A00` and its exact 64-byte record-24736 sample;
- the populated selector/state record that feeds the first changed or still-zero output.

The intro gate, JSON registration, selector refill, buffer allocation, CPU clear, host-image synchronization, depth, raster, pixel exports, and ray/BVH side jobs are already classified. If record 24736 remains zero, follow only address-filtered writable bindings from the now-populated selectors. Do not reopen final composition or blue/striped color interpretation until the position record is populated.

## Known controls and rejected leads

- A fixed solid fragment is useful only as a coverage control. E99 proved it cannot render when paired with the original vertex path; E101 proved the same target and raster state work with a fixed fullscreen vertex stage.
- E96 export-value replacement did not establish early pixel EXEC loss because later experiments proved there were no covered fragments to export.
- E106-E107 rule out the `VCmpxEqU32 1,v11` gate at PC `0x2BEC` as the immediate all-zero cause; forcing both its condition and active execution did not restore coverage.
- E108-E111 rule out the final matrix and the `s16` load implementation as the immediate zero source: the selected backing record itself contains 64 zero bytes despite a valid in-range index.
- The 4,096-instruction guard is not implicated: this shader has 1,054 instructions and no backward branch.
- PS `0x50063ED00` is a downstream copy pass. It faithfully overwrites the real scene with the already-blank 960x540 source; it is not the root producer.
- Depth initialization was a real earlier blocker, but the generic neutral first-use handling resolved it.
- Do not set `SHARPEMU_DISABLE_IMPORT_LOOP_GUARD=1`; it can turn a bounded startup failure into an unbounded loop.

## Test workflow

The runner stores complete logs, exact environment overrides, git state, milestone timing, and targeted SharpEmu-window screenshots under ignored `artifacts/astro-bot/`. It closes every SharpEmu process before each attempt, retries transient pre-title failures, and recycles a pre-title attempt after 60 seconds without emulator output.

By default it captures the emulator window every five seconds, switches to a dense interval from `ps_logo` through the title tail, writes an attempt timeline JSON, and builds timestamped contact and semantic milestone sheets. The classifier requires distinct ordered animation/controller/wordmark evidence; one stale wordmark frame cannot pass. Harness v8 enumerates all layer-zero macOS windows across Spaces, prefers the exact emulator PID, persists failed capture attempts, and atomically updates `run.json`. Use `--screenshot-interval 0` to disable the timeline and the `--screenshot-*` controls to tune density and sheet layout.

Check the machine and paths:

```sh
python3 scripts/astro-test.py doctor
```

Build only when sources are newer, boot to the title milestone, take a window-only screenshot, verify 10 seconds of stability, and stop:

```sh
python3 scripts/astro-test.py test --tag clean-control
```

Run an exact diagnostic without editing a shell script:

```sh
python3 scripts/astro-test.py test \
  --tag e97-first-gate \
  --env SHARPEMU_TRACE_PIXEL_SHADER_ADDRESS=0x5002AF200 \
  --env SHARPEMU_SHADER_MAX_STEPS=4096
```

Reuse a prebuilt binary for the fastest repeat:

```sh
python3 scripts/astro-test.py test --build never --tag e97-repeat
```

Keep the emulator open for manual input testing:

```sh
python3 scripts/astro-test.py run --build never --tag manual-input
```

Set a non-default game path with `--game` or `SHARPEMU_ASTRO_EBOOT`. The runner selects `osx-x64`, `linux-x64`, or `win-x64` automatically and supports targeted screenshots on all three hosts when their native capture dependencies are available.

## Acceptance criteria

- Release build succeeds with zero errors.
- A current-run milestone sheet contains at least three distinct animation frames spanning one second, controller symbols, and the later PS Studios wordmark.
- ASTRO BOT reaches `title_controller_ship` with original shaders.
- The real title/menu is visibly correct without title-specific bypass flags.
- The title remains stable for at least 60 seconds.
- Keyboard or controller input operates the menu.
- MRT readbacks and the final presented image are not uniformly black or diagnostic replacement colors.

## Evidence policy

Add one row to [experiments.md](experiments.md) after every meaningful experiment. Record the exact flags or code change, observed result, corrected conclusion, and the runner artifact directory. Aborted pre-title launches are configuration or stability evidence only, not shader evidence.

## Tooling validation

On 2026-07-16 the harness successfully performed a locked restore and Release `osx-x64` publish with the SDK pinned by `global.json`. A clean automated run recycled one stalled attempt, reached `title_controller_ship` on the retry at 98.7 seconds, and completed its stability check. Visual QA caught and corrected an initial rectangle-based screenshot bug; macOS capture now selects SharpEmu's largest layer-zero CoreGraphics window by exact window ID, even when another application covers it. Evidence is under `artifacts/astro-bot/runs/20260716-010424-harness-smoke/` and `artifacts/astro-bot/window-capture-validation.png`.

Timeline/contact-sheet generation was smoke-tested at `artifacts/astro-bot/harness-contact-sheet-smoke.png`. E102's retry produced 24 targeted frames and a contact sheet showing the full-color ASTRO splash near 10 seconds, black loading/title frames, and the forced magenta diagnostic near 115 seconds: `artifacts/astro-bot/runs/20260716-014913-e102-2a9-position-export/attempt-02-contact-sheet.png`.

Harness v8, dirty image refresh, and corrected sampled-stage barriers passed the full intro gate in E143. The semantic sheet visibly contains boot art, three animation frames, controller symbols, the wordmark, and the final red frame: `artifacts/astro-bot/runs/20260716-104419-e143-dirty-refresh-barriers/attempt-01-milestones.png`.

To stop raw diagnostics from exhausting the startup volume, 14 stale, reproducible `/private/tmp/astro-*` dump directories were removed after their conclusions had been journaled. This reclaimed 82.5 GiB and increased free space from 41 GiB to 124 GiB (98% to 94% used). No repository, source, journal, or unrelated temporary data was removed. New screenshots, dumps, and grids belong under this durable worktree's ignored `artifacts/astro-bot/` tree.
