# Sts2Bridge — AlphaDefect AI Assistant (English Build)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Game: Slay the Spire 2](https://img.shields.io/badge/Slay%20the%20Spire%202-v0.114.0-purple.svg)](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/)
[![English UI](https://img.shields.io/badge/UI-English-green.svg)](#features)

**Full-run neural-network auto-play for Slay the Spire 2**, driven by
[AlphaDefect](https://github.com/hmty-CHEN/AlphaDefect-Public) weights, with an **English UI** and
**v0.114.0 compatibility fixes**. This is a fork of
[Kurisu934/sts2-alphadefect-bridge](https://github.com/Kurisu934/sts2-alphadefect-bridge).

It plays the game for you — combat, map routes, events, Ancient/Neow blessings, campfires, shops,
treasure chests, card rewards, potions, and the Crystal Sphere minigame — using the same observation
encoder as AlphaDefect's headless trainer, so live decisions match training input exactly.

> **Not affiliated with Mega Crit Games.** Slay the Spire 2 is a trademark of Mega Crit Games.
> This is an independent community modding/research tool. See [NOTICE.md](NOTICE.md).

---

## Contents

1. [Features](#features)
2. [Results — win rates by character](#results--win-rates-by-character)
3. [Requirements](#requirements)
4. [Installation (ReadyToUse, no Python)](#installation-readytouse-no-python)
5. [In-game controls and configuration](#in-game-controls-and-configuration)
6. [Building from source](#building-from-source)
7. [Optional: Python tree-search server](#optional-python-tree-search-server)
8. [Troubleshooting](#troubleshooting)
9. [Credits and license](#credits-and-license)

---

## Features

- **All six characters**: FOOL, IRONCLAD, SILENT, DEFECT, NECROBINDER, REGENT.
- **No Python needed to play**: built-in cross-platform neural inference engine (`NativeModel.cs`)
  loads pre-converted `.s2nn` weights in milliseconds.
- **Full decision coverage**: combat card plays and targeting, potions, end turn, map path choice,
  card rewards and bundles, event options, Ancient/Neow blessings, rest-site options, merchant
  purchases, treasure relic picks, and Crystal Sphere cells.
- **Advisory and auto-play**: show the model's ranked candidates, or let the mod execute them.
- **Live HUD** in the top-right corner with candidate values, adjustable Top-K, and mode toggle.
- **Optional external 32-simulation tree search** via `play_live.py` for elite/boss encounters.
- **English UI**: every player-facing string is translated; candidate names use the game's own
  English localization tables.
- **100% human-visible state**: the mod reads the same game state the player sees; no hidden memory.

---

## Results — win rates by character

These are the published AlphaDefect results at **Ascension 10** (headless engine, greedy play), from
the [AlphaDefect paper](https://arxiv.org/abs/2611.31076), Tables 3 and 4. "Search" adds
32-simulation rollout search on the two Act-3 boss fights (v0.111.0) or information-set tree search
in all boss and elite fights (v0.113.1). "Retest" is a fresh-seed run that was not used for
development choices.

| Character | Game build | Network | Greedy | Search | Fresh-seed retest (greedy / search) |
| --- | --- | --- | --- | --- | --- |
| DEFECT | v0.111.0 | distilled student `e` (v165018) | **76.7%** | **80.3%** | 77.8% / — (greedy only; seeds unused for choices) |
| REGENT | v0.111.0 | distilled student `e8` (v54603) | **74.1%** | **77.2%** | 74.9% / 80.5% |
| SILENT | v0.111.0 | EMA v26428 | **71.3%** | **74.0%** | 71.2% / 73.6% |
| IRONCLAD | v0.113.1 | EMA v24782 | **67.9%** | **73.1%** | 66.5% / — (greedy only) |
| NECROBINDER | v0.113.1 | raw v25875 | **63.1%** | **67.6%** | tested on fresh seeds only (RV1) |
| FOOL | v0.113.1 | EMA v24181 | **62.2%** | **68.0%** | 62.5% / 66.7% |

In 866 runs of the released game played through a mod on a public livestream (to 2026-11-22),
the networks won 645 games against 642.5 expected from the headless results.
Source: [AlphaDefect-Public README, Results](https://github.com/hmty-CHEN/AlphaDefect-Public#results).

**Important:** none of these networks was trained on v0.114.0. Defect, Regent, and Silent weights
were trained for v0.111.0; Fool, Ironclad, and Necrobinder for v0.113.1. This mod runs them on
v0.114.0, so treat the table as the networks' *original* strength, not a guarantee for the current
game version. The built-in `.s2nn` weights are greedy (the "Greedy" column); the "Search" column
requires the optional Python tree-search server.

---

## Requirements

- A legitimate local installation of **Slay the Spire 2 v0.114.0** (Steam). This fork was built and
  tested against `v0.114.0` only (`release_info.json`: `branch v0.114.0`).
- The game's native mod loader (STS2 supports a `mods/` folder natively; no third-party mod library
  is required — the mod uses `MegaCrit.Sts2.Core.Modding.ModInitializer` and the game-shipped
  `0Harmony.dll`).
- **Linux users**: the Windows game build runs under Wine/Proton. The mod DLL is managed code and
  works unchanged under Wine. This fork was tested on a Wine prefix with the Windows `v0.114.0`
  build.
- To **build from source** only: **.NET 9 SDK** or newer (`dotnet --version`), Python 3 for the
  helper scripts, and the game's DLLs for compile-time references.

---

## Installation (ReadyToUse, no Python)

1. Download **`Sts2Bridge-v0.114.0-ReadyToUse.zip`** from the
   [upstream releases](https://github.com/Kurisu934/sts2-alphadefect-bridge/releases/latest).
   It contains the six `.s2nn` model files, vocabulary files, config, and the Chinese UI DLL.
2. Extract the `Sts2Bridge/` folder into your game's `mods/` directory:
   - **Windows**: `<Steam>\steamapps\common\Slay the Spire 2\mods\Sts2Bridge\`
   - **Linux (native/Steam Deck)**: `~/.local/share/Steam/steamapps/common/Slay the Spire 2/mods/Sts2Bridge/`
   - **macOS**: `<game>/SlayTheSpire2.app/Contents/MacOS/mods/Sts2Bridge/`
3. Replace `Sts2Bridge.dll` and `Sts2Bridge.json` in that folder with the English build from this
   repository's [Releases](../../releases) (or build them yourself — see below). Keep the `models/`,
   `vocab.txt`, `aliases.txt`, `no_downside_relics.txt`, and `bridge_config.json` files from the
   ReadyToUse zip.
4. Launch the game, open the **Mods** menu, and enable **AlphaDefect AI Assistant (Sts2Bridge)**.

Expected folder layout:

```text
mods/Sts2Bridge/
├── Sts2Bridge.dll          <- this fork's English build
├── Sts2Bridge.json         <- English manifest (name: "AlphaDefect AI Assistant (Sts2Bridge)")
├── bridge_config.json
├── vocab.txt
├── aliases.txt
├── no_downside_relics.txt
├── README_Install.txt
└── models/
    ├── FOOL.s2nn           (v24181, fmt 27)
    ├── IRONCLAD.s2nn       (v24782, fmt 28)
    ├── SILENT.s2nn         (v26428, fmt 23)
    ├── DEFECT.s2nn         (v165018, fmt 19)
    ├── NECROBINDER.s2nn    (v25875, fmt 28)
    └── REGENT.s2nn         (v54603, fmt 20)
```

The `.s2nn` weights are **not** in this source repository (`.gitignore` excludes binaries and
model files). They are converted from upstream AlphaDefect-Public checkpoints and ship in the
upstream ReadyToUse zip.

---

## In-game controls and configuration

The HUD panel in the top-right shows the current mode and top candidates. The text area is
mouse-transparent; only the buttons are clickable.

| Key | Action |
| --- | --- |
| `F7` | Show/hide the HUD panel |
| `F8` | Toggle **Auto-Play** / Advisory mode |
| `F9` | Execute one recommended step (Advisory mode) |
| `F10` | Reconnect to the external TCP policy server |

The same options are available in the **Mods** menu panel and via the `[⚙ Options]` HUD button:

| `bridge_config.json` key | Default | Meaning |
| --- | --- | --- |
| `backend` | `auto` | `auto` (external server if connected, else built-in), `builtin`, or `tcp` |
| `top_k` | `3` | Number of candidate actions shown (1–10) |
| `auto_play` | `false` | Automatically execute the top recommended action |
| `action_delay_ms` | `250` | Delay between auto-executed actions (50–2000 ms) |
| `stabilize_ms` | `120` | Time the UI must be stable before encoding a decision |
| `route_summary` | `true` | Show route advice on the map screen |
| `auto_gold` | `true` | Auto-collect reward gold |
| `auto_free_relics` | `true` | Auto-take chest relics with no downside |
| `show_overlay` | `true` | Show the in-run HUD |
| `host` / `port` | `127.0.0.1` / `18765` | External policy server endpoint |

Changes made through the UI are saved back to `bridge_config.json`.

---

## Building from source

You need the **.NET 9+ SDK** and a local STS2 installation for reference DLLs. No game binary is
copied into this repository; compilation reads `sts2.dll`, `GodotSharp.dll`, and `0Harmony.dll`
from your game directory as private references.

```bash
cd engine/Sts2Bridge
dotnet build -c Release \
  -p:Sts2GameVersion=v0.114.0 \
  -p:Sts2GameDir="/path/to/Slay the Spire 2/data_sts2_windows_x86_64"
```

The output is `engine/Sts2Bridge/bin/Release/net9.0/Sts2Bridge.dll`. Copy it (and
`Sts2Bridge.json`) into `<game>/mods/Sts2Bridge/`.

For a fully automated install (builds, copies the DLL/manifest/config/catalogs, and optionally
installs exported `.s2nn` models):

```bash
python3 tools/install_mod.py \
  --game-dir "/path/to/Slay the Spire 2" \
  --models-dir "/path/to/exported_s2nn_models"
```

To export your own `.s2nn` files from AlphaDefect `.pt` checkpoints (requires PyTorch):

```bash
python3 tools/export_s2nn.py \
  --all-dir /path/to/checkpoints \
  --out-dir "/path/to/Slay the Spire 2/mods/Sts2Bridge/models"
```

The Dockerfile-free build targets `net9.0`. The mod has no external NuGet dependency at runtime.

---

## Optional: Python tree-search server

For 32-simulation closed-loop search on elite and boss fights, start the upstream server:

```bash
python3 python/play_live.py \
  --ckpt /path/to/fool_ema_v24181.pt \
  --tree-search boss_elite \
  --sims 32 \
  --headless-dll /path/to/Sts2Headless.dll
```

The mod detects the server automatically when `backend` is `auto` and uses it instead of the
built-in model. Stopping the server falls back to the built-in `.s2nn` inference.

---

## Troubleshooting

The log is written to two places:

- `<game>/mods/Sts2Bridge/sts2_bridge.log` (copied from the game's user data directory)
- Windows: `%APPDATA%\SlayTheSpire2\sts2_bridge.log`
  Linux/Wine: `<wine-prefix>/drive_c/users/<user>/AppData/Roaming/SlayTheSpire2/sts2_bridge.log`

Useful checks:

- `Initializing Sts2Bridge v1.2.1 ...` with `auto_play=True` confirms the config was loaded.
- `Loaded NativeModel ... (char=..., v..., fmt=...)` confirms a weight file was found.
- `Executing #N [...]` lines confirm actions are being taken.
- Throttled `[diag]` lines appear only when the mod is idle; they name the current room, overlay,
  and UI node so a missing decision can be traced.
- Known upstream limitation: no network was trained on v0.114.0 card changes (notably FOOL), so
  recommendation strength on reworked cards is unverified.

This fork fixes the following v0.114.0-specific issues: Ancient/Neow event handler reflection crash,
combat-end state blocking non-combat decisions, open-map screen shadowing room UI, treasure chest
relics consumed before the chest was opened, and merchant purchases silently doing nothing.

---

## Credits and license

This project stands on the work of many others. All upstream licenses are preserved; see
[LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).

- **[AlphaDefect-Public](https://github.com/hmty-CHEN/AlphaDefect-Public)** — Copyright (c) 2026
  Qiyue Chen. MIT. The observation encoder, action/protocol definitions, compatibility layer,
  model and search adapters in `engine/Sts2Headless/` and `python/` are adapted from this project.
  The included `.s2nn` weights are converted from its public checkpoints.
- **[Kurisu934/sts2-alphadefect-bridge](https://github.com/Kurisu934/sts2-alphadefect-bridge)** —
  Copyright (c) 2026 Kurisu934 and Contributors. MIT. The original Sts2Bridge mod, HUD overlay,
  live TCP policy server, `.s2nn` container format, and multi-version build/test suite.
- **Mega Crit Games** — developer and copyright holder of Slay the Spire 2. Not affiliated with,
  sponsoring, or endorsing this project. No game assets or binaries are redistributed here.
- **Godot Engine / GodotSharp** — LGPL-2.1+, shipped with the game; UI/runtime host.
- **Harmony** — MIT, shipped with the game as `0Harmony.dll`; used by the mod's patch layer.
- **.NET SDK / runtime** — MIT, Microsoft; required to build the mod.
- **Wine / Proton** — LGPL-2.1+, optional, for running the Windows build on Linux.
- **albaruzz and contributors** — English UI localization, v0.114.0 live-decision fixes,
  diagnostic probes, and this documentation.

Licensed under the **MIT License**. Originally based on MIT-licensed code by Qiyue Chen and
Kurisu934 and Contributors. Chinese upstream README: [README.zh-CN.md](README.zh-CN.md).
