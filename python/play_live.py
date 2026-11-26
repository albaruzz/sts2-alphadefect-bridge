#!/usr/bin/env python3
"""Live Policy & Boss/Elite Tree-Search TCP Server for AlphaDefect-Public + Sts2Bridge.

Listens on TCP (default 127.0.0.1:18765) for real-time human-visible game observations from the
`Sts2Bridge` client mod, runs neural network inference (`PolicyValueNet` with content-prior column
realignment + new-vocab embedding zeroing + equivalence-class greedy merging), optionally runs
closed-loop 32-simulation Boss/Elite information-set tree search (`TreeSearchAdvisor` /
`HeadlessSearchAdvisor`) controlled dynamically via sentinel file `sim_search.on` or `--tree-search`,
and replies with action recommendations + top-k probabilities + value estimates for the in-game HUD
and Auto-Play driver.
"""
from __future__ import annotations

import argparse
import json
import logging
import math
import os
import socket
import struct
import sys
import threading
import time
from pathlib import Path
from typing import Any

import numpy as np

REPO = Path(__file__).resolve().parents[1]
if str(REPO / "python") not in sys.path:
    sys.path.insert(0, str(REPO / "python"))
if str(REPO / "tools") not in sys.path:
    sys.path.insert(0, str(REPO / "tools"))

import torch  # noqa: E402

from sts2env.protocol import Obs, format_for, parse  # noqa: E402
from sts2rl.catalog import Catalog  # noqa: E402
from sts2rl.features import Ragged  # noqa: E402
from sts2rl.greedy import merged_argmax  # noqa: E402
from sts2rl.headless_search import HeadlessSearchAdvisor  # noqa: E402
from sts2rl.infer import InferEngine  # noqa: E402
from sts2rl.model import ModelConfig, PolicyValueNet, load_state, zero_new_vocab  # noqa: E402

VOCAB_N_LEGACY = 1873
MODE_RUN = 1
SENTINEL_POLL_S = 3.0

log = logging.getLogger("play_live")


def load_model(
    ckpt_path: Path,
    device: str = "cpu",
    precision: str = "fp32",
    threads: int = 0,
    prefer_ema: bool = True,
) -> tuple[PolicyValueNet, InferEngine, dict[str, Any]]:
    """Load an AlphaDefect .pt checkpoint with cross-version content-prior alignment & vocab zeroing."""
    t0 = time.perf_counter()
    blob = torch.load(str(ckpt_path), map_location="cpu", weights_only=False)
    model_cfg = ModelConfig(**blob["config"]["model"])
    net = PolicyValueNet(model_cfg).eval()

    use_ema = prefer_ema and bool(blob.get("ema_state"))
    state = blob["ema_state"] if use_ema else blob["state"]
    load_state(net, state, blob.get("content_names"))

    old_n = int(blob.get("vocab_n") or VOCAB_N_LEGACY)
    vocab_n = Catalog().vocab_size
    if old_n < vocab_n:
        with torch.no_grad():
            n_unk = zero_new_vocab(net, old_n, vocab_n)
        log.info("Vocab expanded %d -> %d: zeroed new embedding rows (%d legacy <unk> rows copied)", old_n, vocab_n, n_unk)

    net = net.to(device).eval().infer_ready()
    engine = InferEngine(net, device=device, precision=precision, threads=threads)

    meta = {
        "path": str(ckpt_path),
        "mtime": ckpt_path.stat().st_mtime,
        "version": blob.get("version"),
        "character": blob.get("character"),
        "obs_format": blob.get("obs_format") or 8,
        "vocab_n": old_n,
        "catalog_version": blob.get("catalog_version"),
        "ema": use_ema,
        "load_ms": round((time.perf_counter() - t0) * 1000.0, 1),
    }
    log.info(
        "Loaded checkpoint %s (version=%s, character=%s, obs_format=%s, vocab_n=%d, device=%s, %.1f ms)",
        ckpt_path.name,
        meta["version"],
        meta["character"],
        meta["obs_format"],
        old_n,
        device,
        meta["load_ms"],
    )
    return net, engine, meta


def estimate_win_prob(value: float) -> float:
    """Smoothly map run value return (~[-1.0, +6.1] on standard AlphaDefect reward scale) to [0, 1] win probability."""
    # Defeat return is ~ -1.0..0.0, full Act-3 double-boss victory return is ~ +4.5..+6.1.
    return float(1.0 / (1.0 + math.exp(-0.85 * (value - 1.8))))


class SentinelSearchController:
    """Polls sentinel file `sim_search.on` every 3 seconds (plus optional CLI `--tree-search`) without restarting."""

    def __init__(
        self,
        get_net,
        sentinel_paths: list[Path],
        cli_config: dict[str, Any] | None = None,
        headless_dll: str | None = None,
        device: str = "cpu",
    ):
        self._get_net = get_net
        self._sentinel_paths = sentinel_paths
        self._cli_config = dict(cli_config) if cli_config else None
        self._headless_dll = headless_dll
        self._device = device
        self._last_poll = 0.0
        self._active_spec_json: str | None = None
        self.advisor: HeadlessSearchAdvisor | None = None
        self._last_run_info: dict[str, Any] | None = None
        self.poll(force=True)

    def _desired_config(self) -> dict[str, Any] | None:
        cfg: dict[str, Any] | None = None
        for p in self._sentinel_paths:
            if p.exists():
                try:
                    raw = p.read_text(encoding="utf-8").strip()
                    cfg = json.loads(raw) if raw else {"method": "tree", "scope": "boss_elite"}
                    break
                except Exception as e:
                    log.warning("Failed to parse sentinel %s: %s", p, e)
                    return None
        if cfg is None and self._cli_config is not None:
            cfg = dict(self._cli_config)
        if cfg is None:
            return None
        if cfg.get("enabled") is False:
            return None
        cfg.setdefault("device", self._device)
        if self._headless_dll and not cfg.get("dll"):
            cfg["dll"] = self._headless_dll
        return cfg

    def poll(self, force: bool = False) -> None:
        now = time.monotonic()
        if not force and (now - self._last_poll) < SENTINEL_POLL_S:
            return
        self._last_poll = now

        desired = self._desired_config()
        spec_json = json.dumps(desired, sort_keys=True, ensure_ascii=False) if desired is not None else None
        if spec_json == self._active_spec_json:
            return

        # Deleting the sentinel disables search immediately even mid-combat (matching 直播树搜索.md §62)
        if desired is None:
            if self.advisor is not None:
                log.info("无头引擎搜索已关闭 (sentinel removed)")
                self.advisor.close()
                self.advisor = None
            self._active_spec_json = None
            return

        # Changing parameters while engaged in a tracked combat waits until the current fight ends
        if self.advisor is not None and self.advisor.engaged():
            return

        if self.advisor is not None:
            self.advisor.close()
            self.advisor = None

        try:
            adv = HeadlessSearchAdvisor(desired, get_net=self._get_net)
            if adv.init_error:
                log.warning("Search advisor initialized with warning: %s (will fallback to greedy)", adv.init_error)
            else:
                log.info("无头引擎搜索已开启: %s", spec_json)
            self.advisor = adv
            self._active_spec_json = spec_json
            if self._last_run_info is not None:
                adv.on_run_info(self._last_run_info)
        except Exception as e:
            log.warning("Failed to initialize search advisor (%s); staying on greedy policy", e)
            self.advisor = None
            self._active_spec_json = spec_json

    def on_run_info(self, info: dict[str, Any]) -> None:
        self._last_run_info = dict(info)
        self.poll(force=True)
        if self.advisor is not None:
            self.advisor.on_run_info(info)

    def reset_run(self) -> None:
        self._last_run_info = None
        if self.advisor is not None:
            self.advisor.reset_run()

    def close(self) -> None:
        if self.advisor is not None:
            self.advisor.close()
            self.advisor = None


class LivePolicyServer:
    def __init__(
        self,
        ckpt: Path | None,
        ckpt_dir: Path | None,
        device: str = "cpu",
        precision: str = "fp32",
        threads: int = 0,
        merge_equiv: bool = True,
        strict_format: bool = False,
        sentinel_paths: list[Path] | None = None,
        cli_search_cfg: dict[str, Any] | None = None,
        headless_dll: str | None = None,
    ):
        self.ckpt_dir = ckpt_dir
        self.device = device
        self.precision = precision
        self.threads = threads
        self.merge_equiv = merge_equiv
        self.strict_format = strict_format

        initial_ckpt = ckpt or self._newest_ckpt_in_dir()
        if initial_ckpt is None:
            raise FileNotFoundError("No checkpoint provided (--ckpt or --ckpt-dir with .pt files required)")

        self.net, self.engine, self.meta = load_model(
            initial_ckpt, device=device, precision=precision, threads=threads
        )

        default_sentinels = sentinel_paths or [
            Path("sim_search.on"),
            Path.home() / ".local/share/sls2/live/sim_search.on",
        ]
        self.search_ctl = SentinelSearchController(
            get_net=lambda: self.net,
            sentinel_paths=default_sentinels,
            cli_config=cli_search_cfg,
            headless_dll=headless_dll,
            device=device,
        )

    def _newest_ckpt_in_dir(self) -> Path | None:
        if self.ckpt_dir is None or not self.ckpt_dir.is_dir():
            return None
        pts = sorted(self.ckpt_dir.glob("*.pt"), key=lambda p: p.stat().st_mtime, reverse=True)
        return pts[0] if pts else None

    def maybe_hot_reload_ckpt(self) -> None:
        newest = self._newest_ckpt_in_dir()
        if newest is None:
            return
        try:
            mtime = newest.stat().st_mtime
        except OSError:
            return
        if str(newest) == self.meta.get("path") and mtime <= float(self.meta.get("mtime") or 0.0):
            return
        try:
            net, engine, meta = load_model(
                newest, device=self.device, precision=self.precision, threads=self.threads
            )
            self.net, self.engine, self.meta = net, engine, meta
            log.info("Hot-reloaded checkpoint -> %s (v%s)", newest.name, meta.get("version"))
        except Exception as e:
            log.error("Checkpoint hot-reload of %s failed (keeping previous model): %s", newest, e)

    def handle_run_info(self, msg: dict[str, Any]) -> None:
        self.maybe_hot_reload_ckpt()
        ch = str(msg.get("character") or "")
        if ch:
            expected_fmt = format_for(ch)
            ckpt_fmt = int(self.meta.get("obs_format") or 0)
            if ckpt_fmt >= 20 and ckpt_fmt != expected_fmt:
                warn_msg = f"Checkpoint obs_format={ckpt_fmt} differs from format_for({ch})={expected_fmt}"
                if self.strict_format:
                    raise ValueError(warn_msg)
                log.warning(warn_msg)
        self.search_ctl.on_run_info(msg)

    def handle_run_end(self, msg: dict[str, Any]) -> None:
        log.info("Run ended: victory=%s, act=%s, floor=%s", msg.get("victory"), msg.get("act"), msg.get("floor"))
        self.search_ctl.reset_run()
        self.maybe_hot_reload_ckpt()

    def decide(self, msg: dict[str, Any], raw_frame: bytes) -> dict[str, Any]:
        t0 = time.perf_counter()
        self.search_ctl.poll()

        obs: Obs = parse(raw_frame)
        n = int(obs.num_actions)
        seq = int(msg.get("seq", 0))
        choices = list(msg.get("choices") or obs.labels or [])

        if n <= 0:
            return {
                "type": "action",
                "seq": seq,
                "action": 0,
                "label": "",
                "prob": 1.0,
                "value": 0.0,
                "win_prob": 0.5,
                "top_k": [],
                "searched": False,
                "changed": False,
                "search_info": None,
                "ckpt_version": self.meta.get("version"),
                "infer_ms": round((time.perf_counter() - t0) * 1000.0, 2),
            }

        # Strictly strip any privileged features so live inference only uses human-visible information
        ragged = Ragged.from_obs([obs], np.array([MODE_RUN], dtype=np.int64))
        ragged.tok_priv[:] = 0
        logits_np, values_np = self.engine.run(ragged)

        row_logits = logits_np[0, :n].astype(np.float64)
        finite = np.isfinite(row_logits)
        if finite.any():
            shifted = np.where(finite, row_logits - np.max(row_logits[finite]), -1e30)
            exp_p = np.where(finite, np.exp(shifted), 0.0)
            probs = exp_p / max(float(exp_p.sum()), 1e-12)
        else:
            probs = np.full(n, 1.0 / n, dtype=np.float64)

        value = float(values_np[0])
        greedy_act = int(merged_argmax(obs, probs)) if self.merge_equiv else int(np.argmax(probs))

        act = greedy_act
        searched = False
        changed = False
        search_info: dict[str, Any] | None = None

        adv = self.search_ctl.advisor
        if adv is not None:
            try:
                act, search_info = adv.choose(obs, probs, raw=raw_frame, fallback_action=greedy_act)
                searched = bool(search_info.get("searched"))
                changed = bool(search_info.get("changed"))
                adv.observe(act)
            except Exception as e:
                log.warning("Search advisor choose() failed (%s); falling back to greedy action %d", e, greedy_act)
                act = greedy_act
                searched = False
                changed = False
                search_info = {"backend": "tree", "searched": False, "reason": f"error: {type(e).__name__}", "synced": False}

        order = np.argsort(-probs)[: min(5, n)]
        top_k = [
            {
                "index": int(i),
                "label": choices[int(i)] if int(i) < len(choices) else f"Action #{int(i)}",
                "prob": round(float(probs[int(i)]), 4),
            }
            for i in order
        ]
        chosen_label = choices[act] if 0 <= act < len(choices) else f"Action #{act}"
        infer_ms = round((time.perf_counter() - t0) * 1000.0, 2)

        log.info(
            "seq=%d kind=%s floor=%d act=%d/%d (%s, p=%.1f%%, V=%+.3f, searched=%s, %.1fms)",
            seq,
            obs.decision.name,
            obs.floor,
            act,
            n,
            chosen_label,
            float(probs[act]) * 100.0,
            value,
            searched,
            infer_ms,
        )

        return {
            "type": "action",
            "seq": seq,
            "action": int(act),
            "label": chosen_label,
            "prob": round(float(probs[act]), 4),
            "value": round(value, 4),
            "win_prob": round(estimate_win_prob(value), 4),
            "top_k": top_k,
            "searched": searched,
            "changed": changed,
            "search_info": search_info,
            "ckpt_version": self.meta.get("version"),
            "infer_ms": infer_ms,
        }

    def close(self) -> None:
        self.search_ctl.close()


class _StreamBuffer:
    """Buffered socket reader supporting both '\n'-delimited JSON lines and exact binary frame reads."""

    def __init__(self, sock: socket.socket):
        self.sock = sock
        self.buf = bytearray()

    def readline(self) -> bytes | None:
        while True:
            nl = self.buf.find(b"\n")
            if nl >= 0:
                line = bytes(self.buf[:nl])
                del self.buf[: nl + 1]
                return line
            chunk = self.sock.recv(16384)
            if not chunk:
                if self.buf:
                    line = bytes(self.buf)
                    self.buf.clear()
                    return line
                return None
            self.buf.extend(chunk)

    def read_exact(self, n: int) -> bytes:
        while len(self.buf) < n:
            chunk = self.sock.recv(max(16384, n - len(self.buf)))
            if not chunk:
                raise ConnectionError(f"Socket closed while reading binary frame ({len(self.buf)}/{n} bytes)")
            self.buf.extend(chunk)
        out = bytes(self.buf[:n])
        del self.buf[:n]
        return out


def serve_client(conn: socket.socket, addr, server: LivePolicyServer, lock: threading.Lock) -> None:
    log.info("Sts2Bridge client connected from %s:%s", addr[0], addr[1])
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    reader = _StreamBuffer(conn)
    try:
        while True:
            raw_line = reader.readline()
            if raw_line is None:
                break
            line = raw_line.decode("utf-8").strip()
            if not line:
                continue
            msg = json.loads(line)
            mtype = msg.get("type")

            if mtype == "hello":
                log.info(
                    "Handshake from Sts2Bridge mod_version=%s obs_version=%s",
                    msg.get("mod_version"),
                    msg.get("obs_version"),
                )
            elif mtype == "run_info":
                with lock:
                    server.handle_run_info(msg)
            elif mtype == "run_end":
                with lock:
                    server.handle_run_end(msg)
            elif mtype == "decision":
                (frame_len,) = struct.unpack("<i", reader.read_exact(4))
                if frame_len <= 0 or frame_len > (16 << 20):
                    raise ValueError(f"Invalid binary frame length: {frame_len}")
                raw_frame = reader.read_exact(frame_len)
                with lock:
                    reply = server.decide(msg, raw_frame)
                payload = (json.dumps(reply, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
                conn.sendall(payload)
            elif mtype == "ping":
                conn.sendall(b'{"type":"pong"}\n')
    except (ConnectionError, OSError) as e:
        log.info("Client %s:%s disconnected: %s", addr[0], addr[1], e)
    except Exception:
        log.exception("Error serving client %s:%s", addr[0], addr[1])
    finally:
        try:
            conn.close()
        except OSError:
            pass


def build_arg_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="AlphaDefect Live Policy & Tree-Search TCP Server (play_live.py)")
    p.add_argument("--ckpt", type=Path, default=None, help="Path to AlphaDefect .pt checkpoint (e.g. fool_ema_v24181.pt)")
    p.add_argument("--ckpt-dir", type=Path, default=None, help="Directory watched for newest .pt checkpoint hot-reload between runs")
    p.add_argument("--host", default="127.0.0.1", help="TCP bind address (default: 127.0.0.1)")
    p.add_argument("--port", type=int, default=int(os.environ.get("STS2_BRIDGE_PORT", "18765")), help="TCP port (default: 18765)")
    p.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu", help="Inference device: cpu or cuda (default: auto)")
    p.add_argument("--precision", default="fp32", choices=("fp32", "tf32", "bf16", "fp16"), help="Precision mode (default: fp32)")
    p.add_argument("--threads", type=int, default=2, help="PyTorch CPU intra-op threads (default: 2)")
    p.add_argument("--no-merge-equiv", action="store_true", help="Disable equivalence-class probability merging (use plain argmax)")
    p.add_argument("--strict-format", action="store_true", help="Reject runs whose character observation format differs from checkpoint")
    p.add_argument(
        "--tree-search",
        default=None,
        choices=("act3_boss", "boss", "boss_elite", "all"),
        help="Enable live closed-loop tree search for specified combat scope without needing sim_search.on",
    )
    p.add_argument("--sims", type=int, default=32, help="Simulations per tree search when --tree-search is set (default: 32)")
    p.add_argument("--tree-procs", type=int, default=8, help="Worker processes for live tree search (default: 8)")
    p.add_argument("--headless-dll", default=None, help="Path to Sts2Headless.dll (with state cloning) for live tree search")
    p.add_argument("--sentinel", type=Path, default=None, help="Custom path to sim_search.on sentinel file (polled every 3s)")
    return p


def main(argv: list[str] | None = None) -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )
    args = build_arg_parser().parse_args(argv)
    if args.ckpt is None and args.ckpt_dir is None:
        raise SystemExit("Error: pass --ckpt <path/to/checkpoint.pt> or --ckpt-dir <dir>")

    cli_search_cfg = None
    if args.tree_search:
        cli_search_cfg = {
            "method": "tree",
            "scope": args.tree_search,
            "sims": args.sims,
            "tree_procs": args.tree_procs,
        }

    sentinel_paths = [args.sentinel] if args.sentinel else [
        Path("sim_search.on"),
        Path.home() / ".local/share/sls2/live/sim_search.on",
    ]

    server = LivePolicyServer(
        ckpt=args.ckpt,
        ckpt_dir=args.ckpt_dir,
        device=args.device,
        precision=args.precision,
        threads=args.threads,
        merge_equiv=not args.no_merge_equiv,
        strict_format=args.strict_format,
        sentinel_paths=sentinel_paths,
        cli_search_cfg=cli_search_cfg,
        headless_dll=args.headless_dll,
    )

    lock = threading.Lock()
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((args.host, args.port))
    sock.listen(8)
    actual_port = sock.getsockname()[1]
    log.info("Listening for Sts2Bridge on %s:%d (Ctrl+C to stop)", args.host, actual_port)

    try:
        while True:
            conn, addr = sock.accept()
            t = threading.Thread(target=serve_client, args=(conn, addr, server, lock), daemon=True)
            t.start()
    except KeyboardInterrupt:
        log.info("Shutting down play_live.py...")
    finally:
        sock.close()
        server.close()


if __name__ == "__main__":
    main()
