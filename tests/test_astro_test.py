from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


REPO_ROOT = Path(__file__).resolve().parents[1]
HARNESS_PATH = REPO_ROOT / "scripts" / "astro-test.py"
SPEC = importlib.util.spec_from_file_location("astro_test_harness", HARNESS_PATH)
assert SPEC is not None and SPEC.loader is not None
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)


class MacOsWindowCaptureTests(unittest.TestCase):
    def tearDown(self) -> None:
        HARNESS._MACOS_WINDOW_IDS.clear()
        HARNESS._MACOS_CAPTURE_FAILURE_COUNTS.clear()
        HARNESS._MACOS_CAPTURE_LAST_FAILURES.clear()

    def test_exact_pid_offscreen_window_precedes_visible_fallback(self) -> None:
        candidates = [
            {
                "WINDOW_ID": "40",
                "OWNER_PID": "999",
                "ONSCREEN": "1",
                "WIDTH": "2560",
                "HEIGHT": "1440",
            },
            {
                "WINDOW_ID": "41",
                "OWNER_PID": "123",
                "ONSCREEN": "0",
                "WIDTH": "1280",
                "HEIGHT": "720",
            },
        ]

        ranked = sorted(
            candidates,
            key=lambda candidate: HARNESS._macos_window_candidate_rank(123, candidate),
        )

        self.assertEqual("41", ranked[0]["WINDOW_ID"])

    def test_rank_is_deterministic_within_exact_pid_windows(self) -> None:
        candidates = [
            {"WINDOW_ID": "9", "OWNER_PID": "123", "ONSCREEN": "0", "WIDTH": "800", "HEIGHT": "600"},
            {"WINDOW_ID": "8", "OWNER_PID": "123", "ONSCREEN": "1", "WIDTH": "800", "HEIGHT": "600"},
            {"WINDOW_ID": "7", "OWNER_PID": "123", "ONSCREEN": "1", "WIDTH": "800", "HEIGHT": "600"},
        ]

        ranked = sorted(
            candidates,
            key=lambda candidate: HARNESS._macos_window_candidate_rank(123, candidate),
        )

        self.assertEqual(["7", "8", "9"], [candidate["WINDOW_ID"] for candidate in ranked])

    def test_capture_uses_offscreen_exact_pid_candidate(self) -> None:
        candidate = {
            "WINDOW_ID": "41",
            "OWNER_PID": "123",
            "EXACT": "1",
            "ONSCREEN": "0",
            "WIDTH": "1280",
            "HEIGHT": "720",
            "OWNER": "SharpEmu",
        }
        commands: list[list[str]] = []

        def fake_run(command: list[str], **_: object) -> subprocess.CompletedProcess[str]:
            commands.append(command)
            Path(command[-1]).write_bytes(b"\x89PNG\r\n\x1a\nvalid")
            return subprocess.CompletedProcess(command, 0, "", "")

        with tempfile.TemporaryDirectory() as directory, \
             mock.patch.object(HARNESS, "find_macos_window_candidates", return_value=[candidate]), \
             mock.patch.object(HARNESS.subprocess, "run", side_effect=fake_run):
            output = Path(directory) / "capture.png"
            self.assertTrue(HARNESS.capture_macos(123, output))

        self.assertIn("-l41", commands[0])


class TimelineDiagnosticsTests(unittest.TestCase):
    def test_zero_frame_attempt_still_persists_timeline_diagnostics(self) -> None:
        command = [
            sys.executable,
            "-u",
            "-c",
            "import time; print('ready', flush=True); time.sleep(0.08)",
        ]
        with tempfile.TemporaryDirectory() as directory, \
             mock.patch.object(HARNESS, "kill_sharpemu"), \
             mock.patch.object(HARNESS, "capture_window", return_value=False):
            root = Path(directory)
            result = HARNESS.run_attempt(
                command,
                os.environ.copy(),
                root / "attempt.log",
                root / "attempt-window.png",
                [],
                timeout=2,
                stall_timeout=2,
                stability=0,
                screenshot_delay=0,
                screenshot_interval=0.01,
                screenshot_boot_interval=0,
                screenshot_title_tail=0,
                screenshot_max_frames=2,
                screenshot_grid_columns=1,
                take_screenshot=True,
                verbose=False,
                keep_open=False,
                ps_studios_video=None,
                require_ps_studios=False,
            )

            timeline_path = Path(str(result["timeline"]))
            payload = json.loads(timeline_path.read_text(encoding="utf-8"))

        self.assertEqual(0, result["timeline_frames"])
        self.assertGreaterEqual(result["timeline_capture_attempts"], 1)
        self.assertEqual(result["timeline_capture_attempts"], payload["capture_attempts"])
        self.assertEqual([], payload["frames"])
        self.assertEqual(payload["capture_attempts"], payload["capture_failures"])


if __name__ == "__main__":
    unittest.main()
