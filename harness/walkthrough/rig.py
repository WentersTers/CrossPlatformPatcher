"""Rig-side glue for the walkthrough engine.

Runs on the lab host that can reach the guest VM over SSH, and bridges the
Python engine to the guest. All guest interaction goes through an injectable
executor so the module stays testable without a live guest.

Usage on the lab host (addresses below are documentation placeholders):
    python -m harness.walkthrough.rig --guest-ip 192.0.2.10 \
        --guest-user user --audio-state up ...
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Optional

from harness.walkthrough.capture import CapturedFrame, capture_frame
from harness.walkthrough.runner import RunnerConfig, WalkRunner


@dataclass
class GuestBridgeConfig:
    guest_ip: str
    ssh_key: str = ""
    guest_user: str = ""
    display: str = ":77"
    wine_prefix: str = "/home/user/game/.wine-prefix"
    game_dir: str = "/home/user/game"
    connect_timeout: int = 6


class GuestBridge:
    """Injectable guest-VM interaction. executor runs a command on the guest."""

    def __init__(self, cfg: GuestBridgeConfig,
                 executor: Optional[Callable[[str], str]] = None):
        self.cfg = cfg
        self._exec = executor or self._default_exec

    def _default_exec(self, cmd: str) -> str:
        args = ["ssh", "-o", "BatchMode=yes",
                "-o", f"ConnectTimeout={self.cfg.connect_timeout}",
                "-o", "StrictHostKeyChecking=no",
                "-o", "UserKnownHostsFile=/dev/null",
                (f"{self.cfg.guest_user}@{self.cfg.guest_ip}"
                 if self.cfg.guest_user else self.cfg.guest_ip), cmd]
        if self.cfg.ssh_key:
            args = ["ssh", "-i", self.cfg.ssh_key] + args[1:]
        r = subprocess.run(args, capture_output=True, text=True, timeout=60)
        return r.stdout

    def run(self, cmd: str) -> str:
        return self._exec(cmd)

    # --- capture ---------------------------------------------------------
    def capture_frame_bytes(self) -> bytes:
        # import -window root to guest tmp, base64 to stdout
        out = self.run(
            f"export DISPLAY={self.cfg.display}; "
            f"import -window root /tmp/walk_frame.png 2>/dev/null; "
            f"base64 -w0 /tmp/walk_frame.png"
        )
        # strip whitespace/newlines
        b64 = "".join(out.split())
        return base64.b64decode(b64) if b64 else b""

    # --- motion ----------------------------------------------------------
    def motion_sweep(self) -> None:
        self.run(
            f"export DISPLAY={self.cfg.display}; "
            f"xdotool mousemove 100 100 2>/dev/null; sleep 0.2; "
            f"xdotool mousemove 900 600 2>/dev/null"
        )

    # --- input -----------------------------------------------------------
    def click(self, x: int, y: int) -> None:
        self.run(
            f"export DISPLAY={self.cfg.display}; "
            f"xdotool mousemove {x} {y} 2>/dev/null; sleep 0.3; xdotool click 1 2>/dev/null"
        )

    def key(self, k: str) -> None:
        self.run(f"export DISPLAY={self.cfg.display}; xdotool key {k} 2>/dev/null")

    # --- subject ---------------------------------------------------------
    def guest_subject_sha256(self) -> str:
        out = self.run(f"sha256sum {self.cfg.game_dir}/uninstall.exe.patched.exe")
        parts = out.split()
        return parts[0] if parts else ""

    # --- audio -----------------------------------------------------------
    def audio_on(self) -> None:
        self.run(
            "export XDG_RUNTIME_DIR=/run/user/$(id -u); "
            "systemctl --user start pipewire.socket pipewire pipewire-pulse.socket "
            "pipewire-pulse wireplumber 2>/dev/null; sleep 3; true"
        )

    def audio_off(self) -> None:
        self.run(
            "export XDG_RUNTIME_DIR=/run/user/$(id -u); "
            "systemctl --user stop pipewire pipewire.socket pipewire-pulse "
            "pipewire-pulse.socket wireplumber 2>/dev/null; sleep 2; "
            "pkill -x pipewire 2>/dev/null; pkill -x pipewire-pulse 2>/dev/null; "
            "pkill -x wireplumber 2>/dev/null; sleep 2; "
            "rm -f /run/user/$(id -u)/pulse/native /run/user/$(id -u)/pipewire-0; true"
        )

    def audio_state(self) -> str:
        out = self.run("export XDG_RUNTIME_DIR=/run/user/$(id -u); "
                       "pactl info 2>&1 | grep 'Server Name' || echo NO_SERVER")
        return "up" if "Server Name" in out else "off"


def build_default_steps(bridge: GuestBridge, assert_states: bool = True) -> list:
    """Default L02-style walk step layout (credits -> yes wall -> menu -> pet)."""
    from harness.walkthrough.driver import Step

    steps = [
        Step("credits_dismiss", expected_state=("S3_credits" if assert_states else None), settle_frames=1,
             action=lambda: bridge.key("Return"), timeout_s=30.0),
        Step("integrity_dismiss", expected_state=("S4_wall" if assert_states else None), settle_frames=1,
             action=lambda: bridge.key("Return"), timeout_s=30.0),
        Step("wait_yes_wall", expected_state=("S2_yes_wall" if assert_states else None), settle_frames=1,
             timeout_s=60.0),
        Step("top_yes_click", expected_state=("S5_post_click" if assert_states else None), settle_frames=1,
             action=lambda: bridge.click(995, 233), timeout_s=30.0),
        Step("byeee_click", expected_state=("S6_menu" if assert_states else None), settle_frames=2,
             action=lambda: bridge.click(960, 560), timeout_s=30.0),
        Step("menu_verify", expected_state=("S6_menu" if assert_states else None), settle_frames=2,
             timeout_s=30.0),
        Step("start_press", expected_state=("S7_pet" if assert_states else None), settle_frames=1,
             action=lambda: bridge.click(880, 787), timeout_s=30.0),
        Step("lang_dismiss", expected_state=("S7_pet" if assert_states else None), settle_frames=2,
             action=lambda: bridge.key("space"), timeout_s=30.0),
        Step("pet_pair", expected_state=("S7_pet" if assert_states else None), settle_frames=2,
             timeout_s=30.0),
    ]
    return steps


def run_walk(args) -> int:
    cfg = GuestBridgeConfig(
        guest_ip=args.guest_ip,
        ssh_key=args.ssh_key or "",
        guest_user=args.guest_user,
        display=args.display,
    )
    bridge = GuestBridge(cfg)

    # audio state
    if args.audio_state == "up":
        bridge.audio_on()
    elif args.audio_state == "off":
        bridge.audio_off()
    print("audio:", bridge.audio_state())

    subject = bridge.guest_subject_sha256()
    print("subject:", subject)

    steps = build_default_steps(bridge, assert_states=not args.no_assert)

    # classifier: learned kNN model if --model given, else template matcher
    # if reference frames dir provided, else unclassified
    template_matcher = None
    learned = None
    if args.model:
        from harness.walkthrough.learned import KnnWalkClassifier
        learned = KnnWalkClassifier.load(args.model)
        print(f"model: {args.model} ({len(learned.labels)} bank samples)")
    elif args.reference_dir:
        from harness.gates.gate2_5_state.template_matcher import StateTemplateMatcher
        from harness.walkthrough.frames import load_reference_frames
        golden = load_reference_frames(args.reference_dir)
        if golden:
            template_matcher = StateTemplateMatcher(golden)

    from harness.walkthrough.classifier import TierResult, WalkClassifier

    def classify_fn(frame_bytes):
        if template_matcher is None:
            return None
        # template matcher expects np array; decode
        try:
            import cv2
            import numpy as np
            arr = cv2.imdecode(
                __import__("numpy").frombuffer(frame_bytes, dtype=__import__("numpy").uint8),
                cv2.IMREAD_COLOR,
            )
            if arr is None:
                return None
            m = template_matcher.classify(arr, None)
            return m
        except Exception:
            return None

    classifier = WalkClassifier(template_matcher) if template_matcher else None

    def classify_wrapper(frame_bytes):
        if learned is not None:
            # learned-only path: bytes -> PIL -> kNN predict -> TierResult
            try:
                from io import BytesIO
                from PIL import Image
                img = Image.open(BytesIO(bytes(frame_bytes))).convert("RGB")
                pred = learned.predict(img)
                if pred is None:
                    return None
                state, conf = pred
                return TierResult(state, float(conf), "learned", abstain=False)
            except Exception:
                return None
        if classifier is None:
            # fall back to a no-op abstain-ish result is not ideal; return None -> runner abstains
            return None
        return classifier.classify(frame_bytes)

    runner_cfg = RunnerConfig(
        steps=steps,
        evidence_dir=args.evidence_dir,
        guest_subject_sha256=subject,
        model_version=args.model_version,
        motion_before_capture=True,
        poll_interval_s=args.poll_interval,
    )

    def capture_fn():
        return bridge.capture_frame_bytes()

    def motion_fn():
        bridge.motion_sweep()

    runner = WalkRunner(
        runner_cfg,
        capture_fn,
        classify_wrapper,
        motion_fn=motion_fn,
        click_fn=bridge.click,
        key_fn=bridge.key,
        sleep_fn=time.sleep,
        time_fn=time.monotonic,
        poll_interval_s=args.poll_interval,
    )
    outcome = runner.run()
    print("completed:", outcome.drive.completed)
    print("frames:", outcome.frames_captured)
    print("abstained:", outcome.abstained)
    print("reason:", outcome.drive.reason)
    print("evidence:", outcome.evidence_path)
    return 0 if outcome.drive.completed else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="Walkthrough engine rig runner")
    ap.add_argument("--guest-ip", required=True)
    ap.add_argument("--ssh-key", default="")
    ap.add_argument("--guest-user", default="",
                    help="SSH user on the guest; empty uses your SSH default")
    ap.add_argument("--display", default=":77")
    ap.add_argument("--audio-state", choices=["up", "off", "leave"], default="leave")
    ap.add_argument("--evidence-dir", required=True)
    ap.add_argument("--reference-dir", default="")
    ap.add_argument("--model", default="",
                    help="path to trained kNN model .npz (learned tier)")
    ap.add_argument("--model-version", default=None)
    ap.add_argument("--poll-interval", type=float, default=0.5)
    ap.add_argument("--no-assert", action="store_true", default=False,
                   help="drive without state assertions (no reference frames)")
    args = ap.parse_args(argv)
    return run_walk(args)


if __name__ == "__main__":
    raise SystemExit(main())
