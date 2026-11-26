#!/usr/bin/env python3
"""Export AlphaDefect PyTorch .pt checkpoints to compact .s2nn binary weights for Sts2Bridge NativeModel.

Pre-aligns `content_proj` columns against the v0.114.0 catalog (1,196 cols), zeroes newly added
vocabulary rows (`zero_new_vocab`), bakes `content_embed = net._content()` directly into the model
so runtime C# inference does not require `catalog.json`, and serializes inference-active tensors in
compact IEEE-754 float16 (2D matrices/embeddings) + float32 (biases, norms, gates, small projections).

Optionally (`--verify-out <json>`) synthesizes combat & macro observation frames per character and
dumps PyTorch `forward_infer` + `merged_argmax` ground-truth vectors for C# parity verification.
"""
from __future__ import annotations

import argparse
import base64
import json
import struct
import sys
import time
from pathlib import Path
from typing import Any

import numpy as np

REPO = Path(__file__).resolve().parents[1]
if str(REPO / "python") not in sys.path:
    sys.path.insert(0, str(REPO / "python"))

import torch  # noqa: E402

from sts2env.protocol import (  # noqa: E402
    CAND_FEATS,
    CAND_FEATS_V25,
    CAND_FEATS_V28,
    CAND_INTS,
    HEADER_INTS,
    TOK_FEATS,
    TOK_FEATS_V26,
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
from sts2rl.features import Ragged, collate_ragged  # noqa: E402
from sts2rl.greedy import merged_argmax  # noqa: E402
from sts2rl.model import ModelConfig, PolicyValueNet, load_state, zero_new_vocab  # noqa: E402

MAGIC = b"S2NN"
FORMAT_VER = 1
VOCAB_N_LEGACY = 1873
VOCAB_ROWS_EXPORT = 2560  # Covers all 2,559 v0.114.0 vocab entries (0..2558) with alignment margin

DEFAULT_BEST_MAP = {
    "FOOL": "fool_ema_v24181_v0114_aligned.pt",
    "IRONCLAD": "ironclad_ema_v24782.pt",
    "NECROBINDER": "necrobinder_raw_v25875.pt",
    "SILENT": "silent_ema_v26428.pt",
    "REGENT": "regent_student_e8.pt",
    "DEFECT": "student_e_v165018.pt",
}


def load_aligned_net(ckpt_path: Path, default_char: str | None = None) -> tuple[PolicyValueNet, dict[str, Any]]:
    blob = torch.load(str(ckpt_path), map_location="cpu", weights_only=False)
    cfg = ModelConfig(**blob["config"]["model"])
    net = PolicyValueNet(cfg).eval()

    use_ema = bool(blob.get("ema_state"))
    state = blob["ema_state"] if use_ema else blob["state"]
    load_state(net, state, blob.get("content_names"))

    old_n = int(blob.get("vocab_n") or VOCAB_N_LEGACY)
    cat_n = Catalog().vocab_size
    with torch.no_grad():
        if old_n < cat_n:
            zero_new_vocab(net, old_n, cat_n)
        if cat_n < cfg.vocab_size:
            # Ensure unused tail rows [cat_n .. vocab_size) are exact zero
            zero_new_vocab(net, cat_n, cfg.vocab_size, was_unk=[])

    net = net.eval().infer_ready()
    character = str(blob.get("character") or default_char or "DEFECT").upper()
    obs_format = int(blob.get("obs_format") or 19)
    hl_beta = float(net.hl_beta.item()) if cfg.value_hl else 0.0

    meta = {
        "character": character,
        "ckpt_version": int(blob.get("version") or 0),
        "obs_format": obs_format,
        "vocab_rows": VOCAB_ROWS_EXPORT,
        "d": cfg.d,
        "layers": cfg.layers,
        "heads": cfg.heads,
        "ff_mult": cfg.ff_mult,
        "n_kinds": cfg.n_kinds,
        "n_zones": cfg.n_zones,
        "n_actions": cfg.n_actions,
        "n_decisions": cfg.n_decisions,
        "n_modes": cfg.n_modes,
        "obs_v16": bool(cfg.obs_v16),
        "obs_v17": bool(cfg.obs_v17),
        "obs_v18": bool(cfg.obs_v18),
        "obs_v19": bool(cfg.obs_v19),
        "obs_v26": bool(cfg.obs_v26),
        "obs_v27": bool(cfg.obs_v27),
        "value_hl": bool(cfg.value_hl),
        "hl_bins": int(cfg.hl_bins),
        "hl_min": float(cfg.hl_min),
        "hl_max": float(cfg.hl_max),
        "hl_beta": hl_beta,
        "future_head": bool(cfg.future_head),
        "fh_dims": int(cfg.fh_dims),
        "recycle": bool(cfg.recycle),
    }
    return net, meta


def collect_tensors(net: PolicyValueNet, meta: dict[str, Any]) -> list[tuple[str, str, np.ndarray]]:
    """Collect ordered (name, dtype_str, ndarray) tensors needed by forward_infer."""
    sd = net.state_dict()
    cfg = net.cfg
    vr = int(meta["vocab_rows"])
    out: list[tuple[str, str, np.ndarray]] = []

    def add(name: str, t: torch.Tensor, dtype: str) -> None:
        arr = t.detach().cpu().contiguous()
        if dtype == "f16":
            np_arr = arr.to(torch.float16).numpy()
        elif dtype == "f32":
            np_arr = arr.to(torch.float32).numpy()
        else:
            raise ValueError(f"Unknown dtype {dtype}")
        out.append((name, dtype, np.ascontiguousarray(np_arr)))

    # Vocabulary-indexed embeddings (sliced cleanly to vocab_rows; rows >= vocab_rows are 0)
    for emb_name in (
        "id_emb.weight",
        "aux_emb.weight",
        "cand_id_emb.weight",
        "boss_emb.weight",
        "boss2_emb.weight",
        "hist_n_emb.weight",
        "hist_e_emb.weight",
        "enc_next.weight",
        "enc_bag.weight",
        "enc_seen.weight",
    ):
        add(emb_name, sd[emb_name][:vr], "f16")

    with torch.no_grad():
        content_embed = net._content().detach().cpu()[:vr]
        priv_base = net.priv_enc(net.id_emb(torch.zeros(1, 2, dtype=torch.long)).reshape(1, -1)).squeeze(0)
    add("content_embed", content_embed, "f16")
    add("priv_base", priv_base, "f32")

    # Small categorical embeddings & base linear projections (exact fp32)
    for f32_name in (
        "kind_emb.weight",
        "zone_emb.weight",
        "dec_emb.weight",
        "mode_emb.weight",
        "act_emb.weight",
        "feat.weight",
        "feat.bias",
        "feat_x.weight",
        "feat_c.weight",
        "feat_v11.weight",
        "feat_v13.weight",
        "feat_v14.weight",
        "feat_v15.weight",
        "hist_lin.weight",
        "stage_lin.weight",
        "pool_emb",
        "pool_n",
        "norm.weight",
        "norm.bias",
        "cand_feat.weight",
        "cand_feat.bias",
        "cand_feat_x.weight",
        "cand_feat_v15.weight",
        "none_ref",
        "cand_mlp.2.weight",
        "cand_mlp.2.bias",
        "cand_mlp_m.2.weight",
        "cand_mlp_m.2.bias",
        "value_mlp.2.weight",
        "value_mlp.2.bias",
        "value_mlp_m.2.weight",
        "value_mlp_m.2.bias",
    ):
        add(f32_name, sd[f32_name], "f32")

    # Larger 2D MLP / binding / projection matrices in fp16 + biases in fp32
    for prefix in (
        "move_in",
        "move_out",
        "bind_pw_in",
        "bind_pw_out",
        "bind_pool_in",
        "bind_pool_out",
        "cand_mlp.0",
        "cand_mlp_m.0",
        "priv_proj",
        "value_mlp.0",
        "value_mlp_m.0",
    ):
        add(f"{prefix}.weight", sd[f"{prefix}.weight"], "f16")
        add(f"{prefix}.bias", sd[f"{prefix}.bias"], "f32")

    # Transformer encoder layers
    for i in range(cfg.layers):
        lp = f"encoder.layers.{i}"
        add(f"{lp}.norm1.weight", sd[f"{lp}.norm1.weight"], "f32")
        add(f"{lp}.norm1.bias", sd[f"{lp}.norm1.bias"], "f32")
        add(f"{lp}.self_attn.in_proj_weight", sd[f"{lp}.self_attn.in_proj_weight"], "f16")
        add(f"{lp}.self_attn.in_proj_bias", sd[f"{lp}.self_attn.in_proj_bias"], "f32")
        add(f"{lp}.self_attn.out_proj.weight", sd[f"{lp}.self_attn.out_proj.weight"], "f16")
        add(f"{lp}.self_attn.out_proj.bias", sd[f"{lp}.self_attn.out_proj.bias"], "f32")
        add(f"{lp}.norm2.weight", sd[f"{lp}.norm2.weight"], "f32")
        add(f"{lp}.norm2.bias", sd[f"{lp}.norm2.bias"], "f32")
        add(f"{lp}.linear1.weight", sd[f"{lp}.linear1.weight"], "f16")
        add(f"{lp}.linear1.bias", sd[f"{lp}.linear1.bias"], "f32")
        add(f"{lp}.linear2.weight", sd[f"{lp}.linear2.weight"], "f16")
        add(f"{lp}.linear2.bias", sd[f"{lp}.linear2.bias"], "f32")

    # Optional versioned feature layers
    if cfg.obs_v16:
        for k in ("draw_lin.weight", "feat_v16.weight", "cand_feat_v16.weight", "gain_gate"):
            add(k, sd[k], "f32")
    if cfg.obs_v17:
        for k in ("shop_lin.weight", "pw_order.weight", "ret_gate"):
            add(k, sd[k], "f32")
    if cfg.obs_v18:
        for k in ("v18_lin.weight", "map_v18.weight", "bridge_gate"):
            add(k, sd[k], "f32")
    if cfg.obs_v19:
        for k in ("cand_v19.weight", "v19_lin.weight", "shop_gate", "feat_v19.weight"):
            add(k, sd[k], "f32")
    if cfg.obs_v26:
        add("cand_v26.weight", sd["cand_v26.weight"], "f32")
    if cfg.obs_v27:
        add("feat_v27.weight", sd["feat_v27.weight"], "f32")

    if cfg.value_hl and float(meta["hl_beta"]) != 0.0:
        for prefix in ("value_hl.0", "value_hl.2", "value_hl_m.0", "value_hl_m.2"):
            add(f"{prefix}.weight", sd[f"{prefix}.weight"], "f16")
            add(f"{prefix}.bias", sd[f"{prefix}.bias"], "f32")
        add("hl_centers", net.hl_centers, "f32")

    if cfg.future_head:
        add("fh_pred.0.weight", sd["fh_pred.0.weight"], "f16")
        add("fh_pred.0.bias", sd["fh_pred.0.bias"], "f32")
        for k in (
            "fh_pred.2.weight",
            "fh_pred.2.bias",
            "fh_res.0.weight",
            "fh_res.0.bias",
            "fh_res.2.weight",
            "fh_res.2.bias",
        ):
            add(k, sd[k], "f32")

    if cfg.recycle:
        for prefix in ("rcy_a", "rcy_b", "rcy_c", "rcy_p"):
            add(f"{prefix}.weight", sd[f"{prefix}.weight"], "f32")
            add(f"{prefix}.bias", sd[f"{prefix}.bias"], "f32")

    return out


def write_s2nn(out_path: Path, meta: dict[str, Any], tensors: list[tuple[str, str, np.ndarray]]) -> int:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_tensors = []
    offset = 0
    blobs: list[bytes] = []
    for name, dtype, arr in tensors:
        raw = arr.tobytes(order="C")
        manifest_tensors.append(
            {
                "name": name,
                "dtype": dtype,
                "shape": list(arr.shape),
                "offset": offset,
                "nbytes": len(raw),
            }
        )
        blobs.append(raw)
        offset += len(raw)

    header_obj = dict(meta)
    header_obj["tensors"] = manifest_tensors
    header_bytes = json.dumps(header_obj, ensure_ascii=False, separators=(",", ":")).encode("utf-8")

    prefix_len = 4 + 4 + 4 + len(header_bytes)
    pad_len = (8 - (prefix_len % 8)) % 8

    with out_path.open("wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<II", FORMAT_VER, len(header_bytes)))
        f.write(header_bytes)
        if pad_len:
            f.write(b"\x00" * pad_len)
        for raw in blobs:
            f.write(raw)

    return out_path.stat().st_size


# ---------------------------------------------------------------------------
# Synthetic Parity Frame Builder (for PyTorch vs C# NativeModel verification)
# ---------------------------------------------------------------------------

def _serialize_frame(
    fmt_ver: int,
    header: np.ndarray,
    tok_ints: np.ndarray,
    tok_feats: np.ndarray,
    cand_ints: np.ndarray,
    cand_feats: np.ndarray,
    labels: list[str],
) -> bytes:
    ntf = TOK_FEATS if fmt_ver >= 29 else TOK_FEATS_V28 if fmt_ver >= 27 else TOK_FEATS_V26
    ncf = CAND_FEATS if fmt_ver >= 29 else CAND_FEATS_V28 if fmt_ver >= 26 else CAND_FEATS_V25
    n = tok_ints.shape[0]
    m = cand_ints.shape[0]
    h = header.astype(np.int32).copy()
    h[0] = fmt_ver
    h[2] = n
    h[3] = m
    tf = tok_feats[:, :ntf].astype(np.float32)
    tp = np.zeros((n, TOK_PRIV), dtype=np.float32)
    cf = cand_feats[:, :ncf].astype(np.float32)
    lbl = json.dumps(labels, ensure_ascii=False).encode("utf-8")

    parts = [
        h.tobytes(),
        tok_ints.astype(np.int32).tobytes(),
        tf.tobytes(),
        tp.tobytes(),
        cand_ints.astype(np.int32).tobytes(),
        cf.tobytes(),
        struct.pack("<i", len(lbl)),
        lbl,
    ]
    return b"".join(parts)


def make_verification_cases(net: PolicyValueNet, meta: dict[str, Any]) -> list[dict[str, Any]]:
    """Build 2 rich test frames (1 Combat + 1 Macro Event/Shop/Map mix) exercising every encoder path."""
    cat = Catalog()
    v2i = {k: i for i, k in enumerate(cat.vocab)}
    fmt = int(meta["obs_format"])
    ch = meta["character"]

    char_vid = v2i.get(f"CHARACTER.{ch}", 2)
    strike_vid = v2i.get(f"CARD.STRIKE_{ch}", v2i.get("CARD.STRIKE_IRONCLAD", 10))
    defend_vid = v2i.get(f"CARD.DEFEND_{ch}", v2i.get("CARD.DEFEND_IRONCLAD", 11))
    enemy_vid = v2i.get("MONSTER.NIBBIT", 20)
    boss_vid = v2i.get("ENCOUNTER.VANTOM_BOSS", 30)
    str_pow = v2i.get("POWER.STRENGTH_POWER", 40)
    vuln_pow = v2i.get("POWER.VULNERABLE_POWER", 41)
    relic_vid = v2i.get("RELIC.BURNING_BLOOD", 50)
    pot_vid = v2i.get("POTION.FIRE_POTION", 60)

    cases = []

    # Case 1: Rich Combat Frame (player, enemy, powers w/ ret_gate/bridge_gate, move, combat_stat,
    # duplicate strikes for equivalence merging, draw/discard/exhaust/relic pools, orb, glow)
    h1 = np.zeros(HEADER_INTS, dtype=np.int32)
    h1[1] = int(DecisionKind.COMBAT)
    h1[6] = 1
    h1[7] = 22
    h1[8] = 54
    h1[9] = 75
    h1[10] = 148
    h1[11] = 1  # in_combat
    h1[12] = 9
    h1[16] = boss_vid
    h1[18:21] = [4, 2, 0]
    h1[21] = enemy_vid
    h1[25] = 1
    h1[26] = 1
    h1[27] = 6
    h1[28:30] = [1, 100]
    h1[30:32] = [0, 5]
    h1[32:34] = [1, 5]

    ti1 = np.array(
        [
            [TokenKind.GLOBAL, char_vid, Zone.NONE, -1, 0],          # 0 global
            [TokenKind.CREATURE, char_vid, Zone.ALLY, -1, 0],        # 1 player
            [TokenKind.CREATURE, enemy_vid, Zone.ENEMY, -1, 0],      # 2 enemy
            [TokenKind.POWER, str_pow, Zone.ALLY, 1, 0],             # 3 player power
            [TokenKind.POWER, vuln_pow, Zone.ENEMY, 2, 0],           # 4 enemy power
            [TokenKind.MOVE, enemy_vid, Zone.ENEMY, 2, 0],           # 5 enemy move
            [TokenKind.COMBAT_STAT, 0, Zone.ALLY, 1, 0],             # 6 combat stat
            [TokenKind.CARD, strike_vid, Zone.HAND, -1, 0],          # 7 strike #1
            [TokenKind.CARD, strike_vid, Zone.HAND, -1, 0],          # 8 strike #2 (identical!)
            [TokenKind.CARD, defend_vid, Zone.HAND, -1, 0],          # 9 defend
            [TokenKind.CARD, strike_vid, Zone.DRAW, -1, 0],          # 10 draw card
            [TokenKind.CARD, defend_vid, Zone.DISCARD, -1, 0],       # 11 discard card
            [TokenKind.RELIC, relic_vid, Zone.ALLY, -1, 0],          # 12 relic
            [TokenKind.POTION, pot_vid, Zone.ALLY, -1, 0],           # 13 potion
        ],
        dtype=np.int32,
    )
    rng = np.random.default_rng(42 + fmt)
    tf1 = np.zeros((len(ti1), TOK_FEATS), dtype=np.float32)
    tf1[0, :16] = [1, 22, 10, 148, 54, 75, 3, 3, 0, 2, 5, 3, 2, 1, 1, 0]
    tf1[1, :20] = [54, 75, 5, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 4, 0, 5]
    tf1[2, :20] = [38, 46, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 38, 12, 0]
    tf1[3, :10] = [2, 1, 2, 0, 2, 0, 0, 0, 0, 0]
    tf1[4, :10] = [2, 0, 2, 0, 2, 0, 0, 0, 0, 0]
    tf1[5, :18] = [0, 1.0, 1, 0, 1, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
    tf1[6, :20] = rng.uniform(0.0, 4.0, size=20).astype(np.float32)
    tf1[7, :22] = [1, 1, 1, 0, 9, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9, 0, 0, 0, 1, 0]
    tf1[8] = tf1[7].copy()  # exact duplicate Strike for equivalence merging
    tf1[9, :22] = [1, 1, 2, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0]
    tf1[10, :8] = [1, 0, 1, 0, 6, 0, 1, 0]
    tf1[11, :8] = [1, 0, 2, 0, 0, 5, 0, 0]
    tf1[12, :8] = [1, 0, 0, 0, 0, 1, 0, 0]

    ci1 = np.array(
        [
            [ActionKind.PLAY_CARD, 7, 2, strike_vid, 0],
            [ActionKind.PLAY_CARD, 8, 2, strike_vid, 0],
            [ActionKind.PLAY_CARD, 9, -1, defend_vid, 0],
            [ActionKind.USE_POTION, 13, 2, pot_vid, 0],
            [ActionKind.END_TURN, -1, -1, 0, 0],
        ],
        dtype=np.int32,
    )
    cf1 = np.zeros((len(ci1), CAND_FEATS), dtype=np.float32)
    cf1[0, :14] = [1, 0, 0, 0, 9, 1, 9, 0, 0, 0, 6, 48, 0, 0]
    cf1[1] = cf1[0].copy()
    cf1[2, :14] = [1, 0, 0, 0, 0, 0, 0, 0, 5, 0, 1, 53, 0, 0]
    cf1[3, :14] = [0, 0, 0, 0, 20, 1, 20, 0, 0, 0, 6, 48, 0, 0]
    cf1[4, :14] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6, 48, 0, 0]
    labels1 = ["Play STRIKE → NIBBIT", "Play STRIKE → NIBBIT", "Play DEFEND", "Use FIRE_POTION → NIBBIT", "End Turn"]
    raw1 = _serialize_frame(fmt, h1, ti1, tf1, ci1, cf1, labels1)

    # Case 2: Macro Out-of-Combat Frame (Event / Shop features, gain_id, encounter_pool, route_summary, deck pool)
    h2 = np.zeros(HEADER_INTS, dtype=np.int32)
    h2[1] = int(DecisionKind.SHOP)
    h2[6] = 1
    h2[7] = 24
    h2[8] = 50
    h2[9] = 75
    h2[10] = 210
    h2[11] = 0  # out of combat (macro = True!)
    h2[12] = 10
    h2[16] = boss_vid
    h2[25] = 1
    h2[28:30] = [1, 100]

    ti2 = np.array(
        [
            [TokenKind.GLOBAL, char_vid, Zone.NONE, -1, 0],
            [TokenKind.CREATURE, char_vid, Zone.ALLY, -1, 0],
            [TokenKind.CARD, strike_vid, Zone.DECK, -1, 0],
            [TokenKind.CARD, defend_vid, Zone.DECK, -1, 0],
            [TokenKind.RELIC, relic_vid, Zone.ALLY, -1, 0],
            [TokenKind.CARD, strike_vid, Zone.OFFER, -1, 0],
            [TokenKind.RELIC, relic_vid, Zone.OFFER, -1, 0],
            [TokenKind.OPTION, 1, Zone.OFFER, -1, 0],                # option with gain_id
            [TokenKind.POWER, defend_vid, Zone.OFFER, 0, 0],         # locked shop item (tf[17]=1)
            [TokenKind.ENCOUNTER_POOL, boss_vid, Zone.NONE, -1, 0],  # encounter pool
            [TokenKind.ROUTE_SUMMARY, 0, Zone.NONE, -1, 0],          # route summary
        ],
        dtype=np.int32,
    )
    tf2 = np.zeros((len(ti2), TOK_FEATS), dtype=np.float32)
    tf2[0, :16] = [1, 24, 10, 210, 50, 75, 0, 3, 0, 0, 0, 0, 0, 0, 5, 0]
    tf2[1, :6] = [50, 75, 0, 1, 0, 1]
    tf2[5, :8] = [1, 0, 1, 1, 9, 0, 1, 0]
    tf2[7, :20] = [0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 0, 0, 1, 2, 10, float(relic_vid), 7, 0, 0, 0]
    tf2[8, 17] = 1.0  # locked shop item
    tf2[9, 3:6] = [1.0, 1.0, 0.25]
    tf2[10, :20] = [1, 3, 2, 1, 1, 0, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 1, 2]

    ci2 = np.array(
        [
            [ActionKind.BUY, 5, -1, strike_vid, 0],
            [ActionKind.BUY, 6, -1, relic_vid, 0],
            [ActionKind.LEAVE, -1, -1, 0, 0],
        ],
        dtype=np.int32,
    )
    cf2 = np.zeros((len(ci2), CAND_FEATS), dtype=np.float32)
    cf2[0, :8] = [55, 0, 0, 0, 55, 0, 0, 0]
    cf2[1, :8] = [165, 0, 0, 0, 165, 0, 0, 0]
    labels2 = ["Buy STRIKE (55)", "Buy BURNING_BLOOD (165)", "Leave Shop"]
    raw2 = _serialize_frame(fmt, h2, ti2, tf2, ci2, cf2, labels2)

    for case_name, raw in (("combat", raw1), ("macro_shop", raw2)):
        obs = parse(raw)
        ragged = Ragged.from_obs([obs], np.array([1], dtype=np.int64))
        ragged.tok_priv[:] = 0
        batch = collate_ragged(ragged, attend=net.cfg.macro_attend).to("cpu")
        with torch.no_grad():
            logits_t, val_t = net.forward_infer(batch)
        n = obs.num_actions
        lg = logits_t[0, :n].double().numpy()
        shifted = lg - np.max(lg)
        probs = np.exp(shifted) / np.sum(np.exp(shifted))
        act = int(merged_argmax(obs, probs))
        cases.append(
            {
                "character": ch,
                "case": case_name,
                "obs_format": fmt,
                "raw_b64": base64.b64encode(raw).decode("ascii"),
                "logits": [float(x) for x in lg],
                "probs": [float(x) for x in probs],
                "value": float(val_t[0].item()),
                "action": act,
            }
        )
    return cases


def main() -> None:
    p = argparse.ArgumentParser(description="Export AlphaDefect .pt checkpoints to .s2nn for Sts2Bridge")
    p.add_argument("--ckpt", type=Path, default=None, help="Single .pt checkpoint to export")
    p.add_argument("--out", type=Path, default=None, help="Output .s2nn path for --ckpt")
    p.add_argument("--character", type=str, default=None, help="Override character name (e.g. DEFECT)")
    p.add_argument("--all-dir", type=Path, default=None, help="Directory containing all 6 best .pt checkpoints")
    p.add_argument("--out-dir", type=Path, default=None, help="Output directory for <CHARACTER>.s2nn files")
    p.add_argument("--verify-out", type=Path, default=None, help="Optional JSON path to dump PyTorch ground-truth parity vectors")
    args = p.parse_args()

    jobs: list[tuple[str, Path, Path]] = []
    if args.all_dir:
        if not args.out_dir:
            raise SystemExit("--out-dir is required when using --all-dir")
        for ch, fname in DEFAULT_BEST_MAP.items():
            pt = args.all_dir / fname
            if not pt.exists():
                raise FileNotFoundError(f"Missing {pt}")
            jobs.append((ch, pt, args.out_dir / f"{ch}.s2nn"))
    elif args.ckpt and args.out:
        jobs.append((args.character or "", args.ckpt, args.out))
    else:
        raise SystemExit("Pass either (--ckpt <pt> --out <s2nn>) or (--all-dir <dir> --out-dir <dir>)")

    all_cases: list[dict[str, Any]] = []
    for ch_hint, pt_path, out_path in jobs:
        t0 = time.perf_counter()
        net, meta = load_aligned_net(pt_path, default_char=ch_hint or None)
        tensors = collect_tensors(net, meta)
        nbytes = write_s2nn(out_path, meta, tensors)
        ms = (time.perf_counter() - t0) * 1000.0
        print(
            f"[OK] {meta['character']:11s} <- {pt_path.name:34s} -> {out_path.name:16s} "
            f"({nbytes / (1024 * 1024):5.2f} MB, v{meta['ckpt_version']}, fmt={meta['obs_format']}, "
            f"zones={meta['n_zones']}, hl_beta={meta['hl_beta']}, fh={meta['future_head']}, rcy={meta['recycle']}, {ms:.0f}ms)",
            flush=True,
        )
        if args.verify_out:
            all_cases.extend(make_verification_cases(net, meta))

    if args.verify_out:
        args.verify_out.parent.mkdir(parents=True, exist_ok=True)
        args.verify_out.write_text(json.dumps(all_cases, indent=2), encoding="utf-8")
        print(f"[OK] Wrote {len(all_cases)} parity verification cases to {args.verify_out}", flush=True)


if __name__ == "__main__":
    main()
