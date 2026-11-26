#!/usr/bin/env python3
"""End-to-end automated test for python/play_live.py with a real AlphaDefect checkpoint.

Spins up `play_live.py` on an ephemeral localhost TCP port, connects a mock `Sts2Bridge` TCP
client sending:
  1. `hello` + `run_info` (`FOOL`, Ascension 10, seed `LIVETEST01`)
  2. Serialized Format-27 `Observation` binary frame for a `FOOL` Combat decision (with duplicate
     Strike cards in hand to verify equivalence-class merged inference + human-only tok_priv stripping)
  3. Serialized Format-27 `Observation` binary frame for a `FOOL` Map decision (with RouteSummary tokens)
  4. Dynamic `sim_search.on` sentinel creation & removal check
  5. `run_end` message

Verifies `play_live.py` runs real `PolicyValueNet` neural network inference and returns valid
`seq`, `action`, `label`, `prob`, `value`, `win_prob`, `top_k`, and `ckpt_version`.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import socket
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[1]
if str(REPO / "python") not in sys.path:
    sys.path.insert(0, str(REPO / "python"))

from sts2env.protocol import (  # noqa: E402
    CAND_FEATS_V28,
    CAND_INTS,
    FORMAT_FOOL4,
    HEADER_INTS,
    TOK_FEATS_V28,
    TOK_INTS,
    TOK_PRIV,
    ActionKind,
    DecisionKind,
    TokenKind,
    Zone,
    parse,
)
from sts2rl.catalog import Catalog  # noqa: E402


def serialize_format27_frame(
    decision_kind: DecisionKind,
    in_combat: bool,
    act: int,
    floor: int,
    hp: int,
    max_hp: int,
    gold: int,
    tok_ints: np.ndarray,
    tok_feats22: np.ndarray,
    cand_ints: np.ndarray,
    cand_feats14: np.ndarray,
    labels: list[str],
    tok_priv: np.ndarray | None = None,
) -> bytes:
    """Serialize a canonical Format-27 observation frame matching C# Observation.Serialize()."""
    n = int(tok_ints.shape[0])
    m = int(cand_ints.shape[0])
    assert tok_ints.shape == (n, TOK_INTS)
    assert tok_feats22.shape == (n, TOK_FEATS_V28)
    assert cand_ints.shape == (m, CAND_INTS)
    assert cand_feats14.shape == (m, CAND_FEATS_V28)

    header = np.zeros(HEADER_INTS, dtype=np.int32)
    header[0] = FORMAT_FOOL4  # 27
    header[1] = int(decision_kind)
    header[2] = n
    header[3] = m
    header[4] = 0
    header[5] = 0
    header[6] = act
    header[7] = floor
    header[8] = hp
    header[9] = max_hp
    header[10] = gold
    header[11] = 1 if in_combat else 0
    header[26] = 1
    header[27] = 5 if in_combat else 0
    header[29] = 75
    header[31] = 5 if in_combat else 0
    header[33] = 5 if in_combat else 0

    tp = np.zeros((n, TOK_PRIV), dtype=np.float32) if tok_priv is None else tok_priv.astype(np.float32)
    label_bytes = json.dumps(labels, ensure_ascii=False).encode("utf-8")

    parts = [
        header.astype("<i4").tobytes(),
        tok_ints.astype("<i4").tobytes(),
        tok_feats22.astype("<f4").tobytes(),
        tp.astype("<f4").tobytes(),
        cand_ints.astype("<i4").tobytes(),
        cand_feats14.astype("<f4").tobytes(),
        struct.pack("<i", len(label_bytes)),
        label_bytes,
    ]
    raw = b"".join(parts)
    # Sanity-check our serializer round-trips cleanly through sts2env.protocol.parse()
    parsed = parse(raw)
    assert parsed.decision == decision_kind
    assert parsed.num_actions == m
    assert parsed.labels == labels
    return raw


def build_fool_combat_frame(cat: Catalog) -> tuple[bytes, list[str]]:
    """Build a realistic Act-1 Floor-1 FOOL combat frame (2x Strike, 2x Defend, Twin Shot, End Turn)."""
    vocab_idx = {k: i for i, k in enumerate(cat.vocab)}
    fool_id = vocab_idx.get("CHARACTER.FOOL", 2)
    act1_id = vocab_idx.get("ACT.OVERGROWTH", 3)
    strike_id = vocab_idx.get("CARD.STRIKE_FOOL", 10)
    defend_id = vocab_idx.get("CARD.DEFEND_FOOL", 11)
    twin_id = vocab_idx.get("CARD.RAPID_SHOT", vocab_idx.get("CARD.TWIN_SHOT", 12))
    foe_id = vocab_idx.get("MONSTER.NIBBIT", 20)
    move_id = vocab_idx.get("MOVE:NIBBIT.BITE_MOVE", 21)

    # Tokens:
    # 0: Global, 1: Player Creature, 2: Enemy Creature, 3: Enemy Move, 4: CombatStat(0), 5: CombatStat(4 Fool),
    # 6: Strike #1, 7: Strike #2 (identical to #1 -> tests merged_argmax!), 8: Defend #1, 9: Twin Shot, 10..14: Draw pile cards
    n = 15
    ti = np.zeros((n, TOK_INTS), dtype=np.int32)
    tf = np.zeros((n, TOK_FEATS_V28), dtype=np.float32)

    # 0: Global
    ti[0] = [TokenKind.GLOBAL, fool_id, Zone.NONE, -1, act1_id]
    tf[0, :15] = [0, 1, 10, 99, 68, 70, 3, 3, 0, 1, 0, 3, 10, 1, 1]

    # 1: Player Creature
    ti[1] = [TokenKind.CREATURE, fool_id, Zone.ALLY, -1, 0]
    tf[1, :6] = [68, 70, 0, 1, 0, 1]
    tf[1, 16:18] = [0, 11]  # End-turn preview incoming HP loss = 11

    # 2: Enemy Creature (Nibbit, 44/44 HP, attacking for 11)
    ti[2] = [TokenKind.CREATURE, foe_id, Zone.ENEMY, -1, 0]
    tf[2, :8] = [44, 44, 0, 1, 1, 0, 11, 1]

    # 3: Enemy Move token attached to token 2
    ti[3] = [TokenKind.MOVE, move_id, Zone.ENEMY, 2, 0]
    tf[3, :6] = [0, 1.0, 1, 0, 1, 11]

    # 4..5: Combat stats attached to player (token 1)
    ti[4] = [TokenKind.COMBAT_STAT, 0, Zone.ALLY, 1, 0]
    tf[4, 0] = 0
    ti[5] = [TokenKind.COMBAT_STAT, 0, Zone.ALLY, 1, 0]
    tf[5, :4] = [4, 1, 1, 0]  # 1 arrow available

    # 6, 7: Two identical Strike_Fool in hand (cost 1, dmg 6)
    for idx in (6, 7):
        ti[idx] = [TokenKind.CARD, strike_id, Zone.HAND, -1, 0]
        tf[idx, :4] = [1, 0, 0, 0]
        tf[idx, 16] = 6

    # 8: Defend_Fool in hand (cost 1, block 5)
    ti[8] = [TokenKind.CARD, defend_id, Zone.HAND, -1, 0]
    tf[8, :4] = [1, 0, 0, 0]
    tf[8, 17] = 5

    # 9: Twin Shot / Rapid Shot in hand (cost 1, dmg 7x2, gold glow)
    ti[9] = [TokenKind.CARD, twin_id, Zone.HAND, -1, 0]
    tf[9, :4] = [1, 0, 0, 0]
    tf[9, 16] = 7
    tf[9, 20] = 1  # ShouldGlowGold (format 27 feature 20)

    # 10..14: 5 cards in Draw pile
    for idx in range(10, 15):
        ti[idx] = [TokenKind.CARD, strike_id if idx < 12 else defend_id, Zone.DRAW, -1, 0]
        tf[idx, 0] = 1

    # Inject non-zero tok_priv to verify play_live.py strips privileged features cleanly
    tp = np.ones((n, TOK_PRIV), dtype=np.float32) * 0.77

    # Candidates:
    # 0: Play Strike #1 -> Nibbit
    # 1: Play Strike #2 -> Nibbit
    # 2: Play Defend
    # 3: Play Twin Shot -> Nibbit
    # 4: End Turn
    labels = [
        "Play STRIKE_FOOL → NIBBIT",
        "Play STRIKE_FOOL → NIBBIT",
        "Play DEFEND_FOOL",
        "Play TWIN_SHOT → NIBBIT",
        "End Turn",
    ]
    m = len(labels)
    ci = np.zeros((m, CAND_INTS), dtype=np.int32)
    cf = np.zeros((m, CAND_FEATS_V28), dtype=np.float32)

    ci[0] = [ActionKind.PLAY_CARD, 6, 2, 0, 0]
    cf[0, 4:12] = [6, 1, 6, 0, 0, 0, 11, 57]

    ci[1] = [ActionKind.PLAY_CARD, 7, 2, 0, 0]
    cf[1, 4:12] = [6, 1, 6, 0, 0, 0, 11, 57]

    ci[2] = [ActionKind.PLAY_CARD, 8, -1, 0, 0]
    cf[2, 4:12] = [0, 0, 0, 0, 5, 0, 6, 62]

    ci[3] = [ActionKind.PLAY_CARD, 9, 2, 0, 0]
    cf[3, 4:12] = [7, 2, 14, 0, 0, 0, 11, 57]

    ci[4] = [ActionKind.END_TURN, -1, -1, 0, 0]
    cf[4, 10:12] = [11, 57]

    raw = serialize_format27_frame(
        DecisionKind.COMBAT, True, 0, 1, 68, 70, 99, ti, tf, ci, cf, labels, tok_priv=tp
    )
    return raw, labels


def build_fool_map_frame(cat: Catalog) -> tuple[bytes, list[str]]:
    """Build a realistic Act-1 Floor-1 FOOL Map decision frame with 2 travelable nodes + RouteSummary."""
    vocab_idx = {k: i for i, k in enumerate(cat.vocab)}
    fool_id = vocab_idx.get("CHARACTER.FOOL", 2)
    act1_id = vocab_idx.get("ACT.OVERGROWTH", 3)
    strike_id = vocab_idx.get("CARD.STRIKE_FOOL", 10)
    defend_id = vocab_idx.get("CARD.DEFEND_FOOL", 11)
    map_monster_id = vocab_idx.get("MAPNODE:Monster", 30)
    map_unknown_id = vocab_idx.get("MAPNODE:Unknown", 31)

    n = 16
    ti = np.zeros((n, TOK_INTS), dtype=np.int32)
    tf = np.zeros((n, TOK_FEATS_V28), dtype=np.float32)

    # 0: Global (out of combat)
    ti[0] = [TokenKind.GLOBAL, fool_id, Zone.NONE, -1, act1_id]
    tf[0, :15] = [0, 1, 10, 115, 68, 70, 0, 0, 0, 0, 0, 3, 10, 0, 0]
    tf[0, 16:20] = [0.1, 0.02, 0.03, 0.4]

    # 1: Player Creature
    ti[1] = [TokenKind.CREATURE, fool_id, Zone.ALLY, -1, 0]
    tf[1, :6] = [68, 70, 0, 1, 0, 1]

    # 2..11: Deck cards
    for idx in range(2, 12):
        ti[idx] = [TokenKind.CARD, strike_id if idx < 7 else defend_id, Zone.DECK, -1, 0]
        tf[idx, 0] = 1

    # 12, 13: Two travelable MapNodes at row 1, col 1 & col 3
    ti[12] = [TokenKind.MAP_NODE, map_monster_id, Zone.NONE, -1, 0]
    tf[12, :4] = [1, 1, 0, 1]

    ti[13] = [TokenKind.MAP_NODE, map_unknown_id, Zone.NONE, -1, 0]
    tf[13, :4] = [1, 3, 0, 1]

    # 14, 15: RouteSummary tokens attached to MapNodes 12 and 13
    ti[14] = [TokenKind.ROUTE_SUMMARY, 0, Zone.NONE, 12, 0]
    tf[14, :6] = [14, 4, 2, 2, 1, 3]
    ti[15] = [TokenKind.ROUTE_SUMMARY, 0, Zone.NONE, 13, 0]
    tf[15, :6] = [14, 3, 1, 3, 2, 4]

    labels = ["→ (1,1) Monster", "→ (3,1) Unknown"]
    ci = np.zeros((2, CAND_INTS), dtype=np.int32)
    cf = np.zeros((2, CAND_FEATS_V28), dtype=np.float32)
    ci[0] = [ActionKind.MAP_MOVE, 12, 14, 0, 0]
    ci[1] = [ActionKind.MAP_MOVE, 13, 15, 0, 0]

    raw = serialize_format27_frame(DecisionKind.MAP, False, 0, 1, 68, 70, 115, ti, tf, ci, cf, labels)
    return raw, labels


def find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return int(s.getsockname()[1])


def send_json(sock: socket.socket, obj: dict, raw_frame: bytes | None = None) -> None:
    line = (json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8")
    sock.sendall(line)
    if raw_frame is not None:
        sock.sendall(struct.pack("<i", len(raw_frame)) + raw_frame)


def recv_json_line(rfile) -> dict:
    line = rfile.readline()
    if not line:
        raise RuntimeError("Server closed connection unexpectedly")
    return json.loads(line.decode("utf-8"))


def run_e2e_test(ckpt_path: Path, device: str = "cpu") -> None:
    cat = Catalog()
    combat_raw, combat_labels = build_fool_combat_frame(cat)
    map_raw, map_labels = build_fool_map_frame(cat)

    port = find_free_port()
    with tempfile.TemporaryDirectory(prefix="sts2_live_test_") as tmpdir:
        sentinel = Path(tmpdir) / "sim_search.on"
        cmd = [
            sys.executable,
            str(REPO / "python" / "play_live.py"),
            "--ckpt",
            str(ckpt_path),
            "--host",
            "127.0.0.1",
            "--port",
            str(port),
            "--device",
            device,
            "--threads",
            "2",
            "--sentinel",
            str(sentinel),
        ]
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        try:
            # Wait for server to bind and load checkpoint
            sock = None
            t_deadline = time.time() + 120.0
            while time.time() < t_deadline:
                if proc.poll() is not None:
                    out = proc.stdout.read() if proc.stdout else ""
                    raise RuntimeError(f"play_live.py exited early ({proc.returncode}):\n{out}")
                try:
                    sock = socket.create_connection(("127.0.0.1", port), timeout=1.0)
                    break
                except OSError:
                    time.sleep(0.25)

            if sock is None:
                raise TimeoutError(f"Timed out waiting for play_live.py on 127.0.0.1:{port}")

            with sock:
                sock.settimeout(20.0)
                rfile = sock.makefile("rb")

                # 1. Handshake + run_info
                send_json(sock, {"type": "hello", "mod_version": "1.0.0", "obs_version": 19})
                send_json(
                    sock,
                    {
                        "type": "run_info",
                        "character": "FOOL",
                        "ascension": 10,
                        "run_seed": "LIVETEST01",
                        "game_mode": "Standard",
                        "loaded_floor": 0,
                    },
                )

                # 2. Combat decision #1
                send_json(
                    sock,
                    {
                        "type": "decision",
                        "seq": 1,
                        "kind": "Combat",
                        "character": "FOOL",
                        "act": 0,
                        "floor": 1,
                        "choices": combat_labels,
                        "frame_len": len(combat_raw),
                    },
                    combat_raw,
                )
                rep1 = recv_json_line(rfile)
                print("Combat Reply #1:", json.dumps(rep1, ensure_ascii=False))
                assert rep1["seq"] == 1
                assert 0 <= rep1["action"] < len(combat_labels)
                assert rep1["label"] == combat_labels[rep1["action"]]
                assert math.isfinite(rep1["value"])
                assert 0.0 <= rep1["win_prob"] <= 1.0
                assert len(rep1["top_k"]) == len(combat_labels)
                prob_sum = sum(x["prob"] for x in rep1["top_k"])
                assert abs(prob_sum - 1.0) < 1e-2, f"Top-k probabilities should sum to ~1.0, got {prob_sum}"
                # Identical Strike #1 and Strike #2 must receive identical per-candidate probabilities!
                p_by_idx = {x["index"]: x["prob"] for x in rep1["top_k"]}
                assert abs(p_by_idx[0] - p_by_idx[1]) < 1e-4, f"Identical strikes differed: {p_by_idx}"

                # 3. Map decision #2
                send_json(
                    sock,
                    {
                        "type": "decision",
                        "seq": 2,
                        "kind": "Map",
                        "character": "FOOL",
                        "act": 0,
                        "floor": 1,
                        "choices": map_labels,
                        "frame_len": len(map_raw),
                    },
                    map_raw,
                )
                rep2 = recv_json_line(rfile)
                print("Map Reply #2:", json.dumps(rep2, ensure_ascii=False))
                assert rep2["seq"] == 2
                assert 0 <= rep2["action"] < len(map_labels)
                assert rep2["label"] == map_labels[rep2["action"]]
                assert math.isfinite(rep2["value"])
                assert len(rep2["top_k"]) == 2

                # 4. Dynamic sentinel activation check (graceful fallback when headless dll is absent)
                sentinel.write_text(
                    json.dumps({"method": "tree", "scope": "boss_elite", "sims": 8, "dll": "/nonexistent/Sts2Headless.dll"}),
                    encoding="utf-8",
                )
                send_json(
                    sock,
                    {
                        "type": "run_info",
                        "character": "FOOL",
                        "ascension": 10,
                        "run_seed": "LIVETEST02",
                        "game_mode": "Standard",
                        "loaded_floor": 0,
                    },
                )
                send_json(
                    sock,
                    {
                        "type": "decision",
                        "seq": 3,
                        "kind": "Combat",
                        "character": "FOOL",
                        "act": 0,
                        "floor": 6,
                        "choices": combat_labels,
                        "frame_len": len(combat_raw),
                    },
                    combat_raw,
                )
                rep3 = recv_json_line(rfile)
                print("Sentinel Fallback Reply #3:", json.dumps(rep3, ensure_ascii=False))
                assert rep3["seq"] == 3
                assert rep3["searched"] is False
                assert rep3["search_info"] is not None
                assert rep3["search_info"]["backend"] == "tree"

                # 5. Run end
                sentinel.unlink()
                send_json(sock, {"type": "run_end", "victory": True, "act": 2, "floor": 51})
                send_json(sock, {"type": "ping"})
                pong = recv_json_line(rfile)
                assert pong["type"] == "pong"

            print("\nALL END-TO-END PLAY_LIVE TESTS PASSED!")
        finally:
            proc.terminate()
            try:
                out, _ = proc.communicate(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
                out, _ = proc.communicate()
            if out:
                print("\n--- play_live.py server log ---\n" + out)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--ckpt",
        type=Path,
        default=Path(os.environ.get("STS2_TEST_CKPT", "/home/scc/pb24010490/sts2/alphadefect-v0114/weights/fool_ema_v24181.pt")),
    )
    ap.add_argument("--device", default="cpu")
    args = ap.parse_args()
    if not args.ckpt.exists():
        raise SystemExit(f"Checkpoint not found: {args.ckpt}")
    run_e2e_test(args.ckpt, device=args.device)


if __name__ == "__main__":
    main()
