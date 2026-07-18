#!/usr/bin/env python3
"""Validate and record the final 67-NID GTA V Gen5 parity wave."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
import subprocess
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


INVENTORY_RELATIVE = Path("docs/gta-v/gta-v-gen5-nid-inventory-base-615bae08.csv")
INVENTORY_SHA256 = "efb0a69b0e5e32274db2ca86558041318e9ba65011c0d94f3362629bf826f73a"
MANIFEST_RELATIVE = Path("GTA_V_NID_SWARM_MANIFEST.json")
UNCOVERED_RELATIVE = Path("GTA_V_UNCOVERED_NIDS.csv")
RHO_PACKET = Path("artifacts/gta-v-nid-evidence/rho-remaining90-contracts-20260718")
LIBC_QUEUE = Path("docs/gta-v/provider-evidence/libc35/prefer-lle-registration-queue.csv")
LIBC_NON_LLE_QUEUE = RHO_PACKET / "libc-non-lle-contract-queue.csv"
KERNEL_QUEUE = RHO_PACKET / "kernel-hle-contract-queue.csv"
DATA_QUEUE = RHO_PACKET / "data-import-disposition.csv"
ATTRIBUTE_RE = re.compile(r"\[SysAbiExport\(\s*(.*?)\)\]", re.DOTALL)

LIBC_SOURCES = (
    Path("src/SharpEmu.Libs/Lle/LibcProviderLleExports.cs"),
    Path("src/SharpEmu.Libs/Lle/LibcInternalProviderLleExports.cs"),
    Path("src/SharpEmu.Libs/LibcInternalBacktraceExports.cs"),
)
KERNEL_SOURCE = Path("src/SharpEmu.Libs/Kernel/GtaVKernelContractExports.cs")
DATA_SOURCE = Path("src/SharpEmu.HLE/DataSymbolRegistry.cs")

BACKTRACE_NID = "EHsF2i9FXPM"
LEGACY_STACK_GUARD_NID = "f7uOxY9mM1U"
DATA_REGISTRATIONS = {
    "djxxOmW6-aw": ("__progname", "libkernel"),
    "P330P3dFF68": ("Need_sceLibc", "libc"),
    "ZT4ODD2Ts9o": ("Need_sceLibcInternal", "libSceLibcInternal"),
    "H8AprKeZtNg": ("_Stderr", "libc"),
    "2sWzhYqFH4E": ("_Stdout", "libc"),
}
KERNEL_SEMANTIC_NIDS = {
    "NhpspxdjEKU",  # _nanosleep
    "c7ZnT7V1B98",  # rmdir
    "cfwBSQyr5Ys",  # diagnostic sink
    "VAzswvTOCzI",  # unlink
    "TXFFFiNldU8",  # getpeername
    "5jRCs2axtr4",  # inet_ntop
    "Ez8xjo9UF4E",  # recv, flags == 0
    "fZOeZIOEmLw",  # send, flags == 0
    "TUuiYS2kE8s",  # shutdown
}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_csv(path: Path) -> tuple[list[str], list[dict[str, str]]]:
    with path.open(newline="", encoding="utf-8") as handle:
        reader = csv.DictReader(handle)
        require(reader.fieldnames is not None, f"{path} has no header")
        return list(reader.fieldnames), list(reader)


def field(body: str, name: str) -> str:
    match = re.search(rf'\b{name}\s*=\s*"([^"]+)"', body)
    require(match is not None, f"missing {name} in SysAbiExport")
    return match.group(1)


def parse_exports(repo: Path, sources: tuple[Path, ...]) -> dict[str, dict[str, Any]]:
    exports: dict[str, dict[str, Any]] = {}
    for relative in sources:
        text = (repo / relative).read_text(encoding="utf-8")
        for match in ATTRIBUTE_RE.finditer(text):
            body = match.group(1)
            require("Target = Generation.Gen5" in body, f"non-Gen5 export in {relative}")
            require("Generation.Gen4" not in body, f"Gen4 leakage in {relative}")
            nid = field(body, "Nid")
            require(nid not in exports, f"duplicate final-wave callable NID {nid}")
            exports[nid] = {
                "nid": nid,
                "name": field(body, "ExportName"),
                "library": field(body, "LibraryName"),
                "prefer_lle": "PreferLle = true" in body,
                "source": relative.as_posix(),
            }
    return exports


def verify_commit(repo: Path, commit: str) -> str:
    resolved = subprocess.run(
        ["git", "rev-parse", f"{commit}^{{commit}}"],
        cwd=repo,
        check=True,
        text=True,
        capture_output=True,
    ).stdout.strip()
    ancestry = subprocess.run(
        ["git", "merge-base", "--is-ancestor", resolved, "HEAD"],
        cwd=repo,
        check=False,
    )
    require(ancestry.returncode == 0, f"commit {resolved} is not integrated into HEAD")
    return resolved


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--libc-commit", required=True)
    parser.add_argument("--kernel-commit", required=True)
    parser.add_argument("--data-commit", required=True)
    parser.add_argument("--hardening-commit", required=True)
    parser.add_argument("--integration-validation", required=True)
    parser.add_argument("--runtime-validation", required=True)
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument(
        "--repo",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    return parser.parse_args()


def common_validation(args: argparse.Namespace, lane: str) -> dict[str, Any]:
    return {
        "branch": lane,
        "integration": args.integration_validation,
        "games": [args.runtime_validation],
    }


def main() -> None:
    args = parse_args()
    repo = args.repo.resolve()
    commits = {
        "libc": verify_commit(repo, args.libc_commit),
        "kernel": verify_commit(repo, args.kernel_commit),
        "data": verify_commit(repo, args.data_commit),
        "hardening": verify_commit(repo, args.hardening_commit),
    }

    inventory_path = repo / INVENTORY_RELATIVE
    require(sha256(inventory_path) == INVENTORY_SHA256, "pinned 1,432-NID inventory hash mismatch")
    _, inventory = read_csv(inventory_path)
    inventory_nids = [row["nid"] for row in inventory]
    require(len(inventory_nids) == len(set(inventory_nids)) == 1_432, "inventory cardinality mismatch")
    require(
        Counter(row["symbol_kinds"] for row in inventory) == {"function": 1_426, "object": 6},
        "inventory kind split mismatch",
    )

    libc_exports = parse_exports(repo, LIBC_SOURCES)
    kernel_exports = parse_exports(repo, (KERNEL_SOURCE,))
    require(len(libc_exports) == 35, f"expected 35 libc-family exports, found {len(libc_exports)}")
    require(len(kernel_exports) == 27, f"expected 27 kernel/POSIX exports, found {len(kernel_exports)}")
    require(sum(export["prefer_lle"] for export in libc_exports.values()) == 34, "libc PreferLle split mismatch")
    require(not libc_exports[BACKTRACE_NID]["prefer_lle"], "backtrace must not PreferLle")
    require(not any(export["prefer_lle"] for export in kernel_exports.values()), "kernel/POSIX must remain HLE-bound")

    data_source_text = (repo / DATA_SOURCE).read_text(encoding="utf-8")
    for nid, (name, library) in DATA_REGISTRATIONS.items():
        require(nid in data_source_text and name in data_source_text and library in data_source_text, f"missing data registration {nid}")
    all_callable_nids: set[str] = set()
    for source in (repo / "src").rglob("*.cs"):
        for match in ATTRIBUTE_RE.finditer(source.read_text(encoding="utf-8")):
            nid_match = re.search(r'\bNid\s*=\s*"([^"]+)"', match.group(1))
            if nid_match:
                all_callable_nids.add(nid_match.group(1))
    require(not (set(DATA_REGISTRATIONS) & all_callable_nids), "data NID leaked into callable registry")

    _, libc_queue_rows = read_csv(repo / LIBC_QUEUE)
    _, non_lle_rows = read_csv(repo / LIBC_NON_LLE_QUEUE)
    _, kernel_queue_rows = read_csv(repo / KERNEL_QUEUE)
    _, data_queue_rows = read_csv(repo / DATA_QUEUE)
    libc_evidence = {row["nid"]: row for row in libc_queue_rows}
    non_lle_evidence = {row["nid"]: row for row in non_lle_rows}
    kernel_evidence = {row["nid"]: row for row in kernel_queue_rows}
    data_evidence = {row["nid"]: row for row in data_queue_rows}
    require(len(libc_evidence) == 34, "libc evidence cardinality mismatch")
    require(set(non_lle_evidence) == {BACKTRACE_NID}, "backtrace evidence mismatch")
    require(set(kernel_evidence) == set(kernel_exports), "kernel source/evidence set mismatch")
    require(set(data_evidence) == set(DATA_REGISTRATIONS), "data source/evidence set mismatch")
    require(set(libc_evidence) | {BACKTRACE_NID} == set(libc_exports), "libc source/evidence set mismatch")

    final_wave_nids = set(libc_exports) | set(kernel_exports) | set(DATA_REGISTRATIONS)
    require(len(final_wave_nids) == 67, f"final wave has {len(final_wave_nids)} NIDs, expected 67")
    uncovered_path = repo / UNCOVERED_RELATIVE
    uncovered_header, uncovered = read_csv(uncovered_path)
    uncovered_nids = {row["nid"] for row in uncovered}
    require(
        not uncovered_nids or uncovered_nids == final_wave_nids,
        "uncovered CSV is neither the exact final 67 queue nor the completed empty queue",
    )

    manifest_path = repo / MANIFEST_RELATIVE
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    by_nid = {item["nid"]: item for item in manifest["items"]}
    require(len(by_nid) == len(manifest["items"]) == 911, "manifest cardinality mismatch")
    require(final_wave_nids <= set(by_nid), "final wave NID absent from manifest")
    non_integrated = {item["nid"] for item in manifest["items"] if item["status"] != "integrated"}
    require(not non_integrated or non_integrated == final_wave_nids, "manifest non-integrated set mismatch")

    for nid, export in libc_exports.items():
        item = by_nid[nid]
        item["status"] = "integrated"
        if nid == BACKTRACE_NID:
            row = non_lle_evidence[nid]
            item["evidence"] = {
                "aerolib_name": item.get("symbol"),
                "binary_hash": None,
                "reference_functions": [
                    f"firmware Ghidra export {row['firmware_function_entry']}",
                    row["evidence_file"],
                    LIBC_NON_LLE_QUEUE.as_posix(),
                ],
                "call_sites": [],
                "confidence": row["confidence"],
                "conflicts": ["Ghidra found a diagnostic body, but GTA did not resolve its library namespace at runtime"],
            }
            item["contract"] = {
                "signature": row["abi_summary"],
                "returns": [row["return_error_contract"], "SharpEmu currently fails closed with ORBIS_GEN2_ERROR_NOT_IMPLEMENTED"],
                "output_writes": [row["output_state_contract"], "the fail-closed path writes no guest output"],
                "validation_rules": [row["implementation_gate"]],
                "state_transitions": ["none on the fail-closed path"],
                "ownership": ["diagnostic-only; no retained guest ownership"],
                "synchronization": ["none on the fail-closed path"],
            }
            item["blockers"] = ["runtime provider routing or a fuller diagnostic/backtrace HLE contract remains unproven"]
        else:
            row = libc_evidence[nid]
            item["evidence"] = {
                "aerolib_name": item.get("symbol"),
                "binary_hash": row["provider_sha256"],
                "reference_functions": [
                    f"{row['provider']} Ghidra export {row['function_entry']}",
                    f"body SHA-256 {row['function_body_sha256']}",
                    LIBC_QUEUE.as_posix(),
                ],
                "call_sites": row["runtime_symbol_addresses"].split(";") if row["runtime_symbol_addresses"] else [],
                "confidence": row["confidence"],
                "conflicts": ["provider body is proven; a complete semantic HLE replacement is not claimed"],
            }
            item["contract"] = {
                "signature": "provider-defined Gen5 ABI; the exact guest export is authoritative",
                "returns": ["loaded guest provider result", "ORBIS_GEN2_ERROR_NOT_IMPLEMENTED when the provider is unavailable"],
                "output_writes": ["provider-defined; the fail-closed fallback writes nothing"],
                "validation_rules": ["exact NID/name/library and Ghidra body hash", "runtime-loaded provider route is required"],
                "state_transitions": ["provider-defined; none in the fallback"],
                "ownership": ["provider-defined"],
                "synchronization": ["provider-defined"],
            }
            item["blockers"] = ["semantic behavior remains guest-provider-dependent"]
        item["implementation"] = {
            "worktree": str(repo),
            "branch": "codex/gta-v-nids",
            "commit": commits["libc"],
            "files": [
                export["source"],
                "tests/SharpEmu.Libs.Tests/Lle/Libc35ExportsTests.cs",
                "docs/gta-v/libc35-lle-ghidra.md",
            ],
        }
        item["validation"] = common_validation(args, "35/35 exact libc-family registrations validated")

    for nid, export in kernel_exports.items():
        item = by_nid[nid]
        row = kernel_evidence[nid]
        semantic = nid in KERNEL_SEMANTIC_NIDS
        item["status"] = "integrated"
        item["evidence"] = {
            "aerolib_name": item.get("symbol"),
            "binary_hash": "0d91281f1d2cdcf4d8c2f4b920766b645ea086e679bd95074f30510178a706b0",
            "reference_functions": [
                f"libkernel Ghidra export {row['function_entry']}",
                f"body SHA-256 {row['function_body_sha256']}",
                KERNEL_QUEUE.as_posix(),
            ],
            "call_sites": [],
            "confidence": row["confidence"],
            "conflicts": [] if semantic else ["the recovered implementation gate is not yet modeled in SharpEmu"],
        }
        item["contract"] = {
            "signature": row["abi_summary"],
            "returns": [
                row["return_error_contract"],
                "implemented semantic path" if semantic else "explicit ORBIS_GEN2_ERROR_NOT_IMPLEMENTED until the gate is met",
            ],
            "output_writes": [
                row["output_state_contract"],
                "focused positive/negative tests" if semantic else "no guest output writes on the fail-closed path",
            ],
            "validation_rules": [row["implementation_gate"]],
            "state_transitions": [row["output_state_contract"] if semantic else "none on the fail-closed path"],
            "ownership": ["SharpEmu HLE-owned implementation"],
            "synchronization": ["existing subsystem synchronization" if semantic else "none"],
        }
        item["implementation"] = {
            "worktree": str(repo),
            "branch": "codex/gta-v-nids",
            "commit": commits["kernel"],
            "files": [
                export["source"],
                "src/SharpEmu.Libs/Kernel/KernelSocketCompatExports.cs",
                "tests/SharpEmu.Libs.Tests/Kernel/GtaVKernelContractExportsTests.cs",
                "docs/gta-v/kernel27-ghidra-contracts.md",
            ],
        }
        item["validation"] = common_validation(args, "27/27 exact kernel/POSIX registrations and focused contracts validated")
        item["blockers"] = [] if semantic else [row["implementation_gate"]]

    for nid, (name, library) in DATA_REGISTRATIONS.items():
        item = by_nid[nid]
        row = data_evidence[nid]
        require(item.get("symbol") == name, f"data manifest name mismatch for {nid}")
        item["status"] = "integrated"
        item["evidence"] = {
            "aerolib_name": name,
            "binary_hash": row["provider_sha256"] or None,
            "reference_functions": [
                f"Ghidra STT_OBJECT {row['provider_symbol_address']}",
                DATA_QUEUE.as_posix(),
                "artifacts/gta-v-nid-evidence/data5-objects-20260718/GHIDRA_OBJECT_EVIDENCE.json",
            ],
            "call_sites": row["runtime_symbol_addresses"].split(";") if row["runtime_symbol_addresses"] else [],
            "confidence": "high",
            "conflicts": ["Ghidra classifies this symbol as an object without a function body"],
        }
        item["contract"] = {
            "signature": f"addressable Gen5 ABI object '{name}' in {library}; never callable",
            "returns": ["not applicable; object import relocations bind an address"],
            "output_writes": ["writes the resolved guest-authoritative object address plus relocation addend"],
            "validation_rules": [row["registration_action"], row["forbidden_action"]],
            "state_transitions": [row["initial_value"] or "guest provider owns object state"],
            "ownership": ["guest provider first; registered HLE fallback only where documented"],
            "synchronization": ["provider-defined object synchronization"],
        }
        item["implementation"] = {
            "worktree": str(repo),
            "branch": "codex/gta-v-nids",
            "commit": commits["data"],
            "hardening_commit": commits["hardening"],
            "files": [
                DATA_SOURCE.as_posix(),
                "src/SharpEmu.Core/Runtime/ImportedDataRebinder.cs",
                "src/SharpEmu.Core/Loader/SelfLoader.cs",
                "tests/SharpEmu.Libs.Tests/Loader/DataSymbolRegistrationTests.cs",
                "docs/gta-v/gen5-object-import-architecture.md",
            ],
        }
        item["validation"] = common_validation(args, "5/5 exact data-only registrations and loader contracts validated")
        item["blockers"] = (
            ["the loaded GTA libc provider is required; no fabricated HLE FILE object exists"]
            if nid in {"H8AprKeZtNg", "2sWzhYqFH4E"}
            else ["the HLE fallback is compatibility state, not a complete provider semantic replacement"]
        )

    counts = Counter(item["status"] for item in manifest["items"])
    require(counts == {"integrated": 911}, f"unexpected final manifest lifecycle: {dict(counts)}")
    manifest["run"].setdefault("completed_at", datetime.now(timezone.utc).isoformat())
    manifest["run"]["pinned_inventory_sha256"] = INVENTORY_SHA256
    manifest["run"]["final_registration_coverage"] = "1432/1432"

    if not args.check_only:
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        with uncovered_path.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=uncovered_header, lineterminator="\n")
            writer.writeheader()

    print(json.dumps({
        "updated": 67,
        "manifest_status_counts": dict(counts),
        "uncovered_rows": 0,
        "inventory_rows": len(inventory_nids),
        "callable_final_wave": len(libc_exports) + len(kernel_exports),
        "data_final_wave": len(DATA_REGISTRATIONS),
        "check_only": args.check_only,
    }, sort_keys=True))


if __name__ == "__main__":
    main()
