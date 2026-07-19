# GTA V Gen5 final parity validation — 2026-07-18

## Scope

The current static validation covers exact registration parity for the pinned GTA V Gen5 inventory: 1,432 unique registrations (1,426 function imports and 6 object imports). It does not claim complete semantic parity or GTA V playability.

The statically validated implementation commit is `0996daba06ca48f2471d9c86b4b402c4bb845deb`. The pinned inventory SHA-256 is `efb0a69b0e5e32274db2ca86558041318e9ba65011c0d94f3362629bf826f73a`. The retained runtime trace below is historical evidence from pre-rebase commit `4ea43616102ba8b2a5bf59b745cd3b758d05e110`; it does not validate the rebased GPU scheduler integration.

## Build and test gates

| Gate | Result |
|---|---:|
| Focused GTA parity, libc35, kernel contract, and data-symbol tests | 52/52 passed |
| `SharpEmu.Libs.Tests` | 1054/1054 passed |
| `SharpEmu.SourceGenerators.Tests` | 36/36 passed |
| `SharpEmu.ShaderCompiler.Tests` | 35/35 passed |
| `SharpEmu.ShaderCompiler.Metal.Tests` | 27/27 passed |
| Release solution build | succeeded, 65 warnings, 0 errors |

The exact commands and counts are recorded in `artifacts/gta-v-nid-evidence/final-parity-validation-20260718.json` and are checked by `scripts/update-gta-v-final-parity-tracker.py`.

## Historical runtime command

The x86-64 CLI was published with:

```sh
dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r osx-x64 --nologo
```

The retained GTA V trace was collected under Rosetta with full import tracing at pre-rebase commit `4ea43616102ba8b2a5bf59b745cd3b758d05e110`:

```sh
set +e
SHARPEMU_LOG_ALL_IMPORTS=1 SHARPEMU_LOG_IMPORTS=1 \
  gtimeout -s TERM 45 \
  arch -x86_64 artifacts/publish/SharpEmu.CLI/Release/net10.0/osx-x64/SharpEmu \
  --cpu-engine=native --log-level=info \
  "/Volumes/Untitled/games/sharpemu/Games/GTA V/eboot.bin" \
  > artifacts/gta-v-final-parity-x64.log 2>&1
rc=$?
set -e
echo "exit=$rc"
```

The process reached its terminal fault naturally before the timeout. The exit status was 139.

## Historical trace results

| Check | Result |
|---|---:|
| Final libc provider routes | 34/34 direct bridges |
| Final data objects incorrectly routed as callables | 0 events |
| Imported data relocations | 11 rebound, 0 unresolved |
| Highest import ordinal | 41,427 |
| `MM4IZSEYytQ` checkpoint | reached at import 39,003 |
| Terminal signal | SIGSEGV (11) |
| Terminal RIP | `0x0000000805C273B7` |
| Fault address / access | `0x0000000000000000`, read |
| Terminal thread | `[RAGE] RenderThread` |
| Faulting guest thread's last import | `enqPGLfmVNU` (`strtok_r`) |

The terminal fault is the same later-state fault observed before the final parity wave; the registration work did not regress the prior 41,427-import checkpoint.

## Durable historical evidence

The tracked compressed trace is `artifacts/gta-v-nid-evidence/final-parity-runtime-20260718/gta-v-final-parity-x64.log.gz`.

- Compressed SHA-256: `68848eeaeb458489144abdb73c66a566f22fd3b40b57ad10f4e51c3a68012a1b`
- Raw SHA-256: `585ff7f6635ce07830b2078a46aa6e5cebdd8ddb1f83a0880b9fc40bf0b564f8`
- Raw size: 22,997,934 bytes
- Raw lines: 127,576

The final tracker decompresses this evidence and recomputes the provider-route set, object-callable count, data relocation totals, maximum import ordinal, MM4 checkpoint, and terminal signal tuple before it can close the 67-NID queue.

The recorded test/build counts validate `0996daba06ca48f2471d9c86b4b402c4bb845deb` and are rerunnable validation records, not retained raw command transcripts. The historical runtime routing, relocation, checkpoint, thread, terminal-signal claims, and shell exit status belong to `4ea43616102ba8b2a5bf59b745cd3b758d05e110` and are independently recomputed from the tracked compressed trace.

## Remaining semantic limits

Registration parity is complete, but semantic work remains. Eighteen kernel/POSIX registrations intentionally fail closed, `sceLibcInternalBacktraceForGame` is a fail-closed HLE contract, nonzero `recv`/`send` flags remain unsupported, and 34 libc registrations depend on their Ghidra-identified firmware providers with fail-closed fallback behavior. These limits are tracked explicitly rather than represented as complete implementations.
