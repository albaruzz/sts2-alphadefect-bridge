# Third-Party Notice

## What the MIT License Covers

The [MIT License](LICENSE) covers all source code, build scripts, tests, and documentation in this repository:

- **Sts2Bridge client mod & live policy/search server** (`engine/Sts2Bridge/`, `python/play_live.py`, `tools/install_mod.py`, `tests/`): Copyright (c) 2026 Kurisu934 and Contributors.
- **Shared observation encoder, compatibility layer, model, and search adapters** (`engine/Sts2Headless/Engine/`, `python/sts2env/`, `python/sts2rl/`, `tools/sim_pimc.py`): Adapted from [hmty-CHEN/AlphaDefect-Public](https://github.com/hmty-CHEN/AlphaDefect-Public), Copyright (c) 2026 Qiyue Chen, used under the MIT License. (`PerfPatches.cs` is NOT included in this repository.)

## Third-Party Material Not Covered by the MIT License

- **Slay the Spire 2** is a trademark and copyrighted work of **Mega Crit Games**. This project is an independent community research/modding tool and is **not** affiliated with, sponsored by, or endorsed by Mega Crit Games.
- This repository contains **zero** game binaries (`sts2.dll`, `GodotSharp.dll`, `0Harmony.dll`, `.pck` archives), game art/audio assets, model checkpoint weights (`.pt`), or decompiled game source code.
- Compiling `Sts2Bridge` and running the mod requires your own legitimate local Steam installation of *Slay the Spire 2* (pointed to via `Sts2GameDir`).
- Entity ID vocabularies and static attribute catalogs under `engine/catalog/` (`vocab.txt`, `aliases.txt`, `no_downside_relics.txt`, and `v*/catalog.json`) are numerical/identifier metadata tables derived from `AlphaDefect-Public` so released neural networks receive aligned input indices; see `AlphaDefect-Public/NOTICE.md` for upstream provenance.

## This Fork (English Build, v0.114.0)

`albaruzz/sts2-alphadefect-bridge` is an English-language fork of
[Kurisu934/sts2-alphadefect-bridge](https://github.com/Kurisu934/sts2-alphadefect-bridge).
It keeps the upstream MIT license and all upstream copyright notices unchanged.

Changes in this fork (2026, Copyright (c) 2026 albaruzz and Contributors):

- Translated 167 player-facing UI literals in `engine/Sts2Bridge/Localize.cs`, `Overlay.cs`,
  `ModMenuUi.cs`, `Live.cs`, `NativeModel.cs`, and `Settings.cs` from Simplified Chinese to English.
- Switched in-game candidate text lookup from `localization/zhs/` to `localization/eng/` so card,
  relic, potion, and event-option names follow the game's own English localization.
- Fixed v0.114.0 live-session decision bugs in `engine/Sts2Bridge/Live.cs`:
  - Ancient/Neow event crashes caused by invoking `NCombatEventLayout.HasCombatStarted` on an
    `NAncientEventLayout` instance (wrong-type reflection target).
  - `CombatManager.IsOverOrEnding` lingering outside combat rooms and blocking event, ancient,
    shop, and treasure decisions.
  - The open-map check shadowing decisions when an event/shop/rest UI is on screen.
  - Treasure chest considered already open because the synchronizer pre-populates `CurrentRelics`
    on room entry; the mod must click the chest before taking the relic.
  - Shop purchases emitted `NClickableControl.Released` instead of the `MouseReleased` input event
    the merchant slot listens to; purchases now use `MerchantEntry.OnTryPurchaseUnsynchronized`
    directly (the same path as the game's built-in AutoSlay handler).
- Added throttled `[diag]` log lines for diagnosing idle decision points.
- Manifest renamed to "AlphaDefect AI Assistant (Sts2Bridge)" with an English description and
  version `1.2.1-en`.

### Additional third-party components (build/run time only)

- **Godot Engine / GodotSharp** (LGPL-2.1+, shipped with the game) — the game and mod UI runtime.
- **Harmony** (`0Harmony.dll`, MIT, shipped with the game) — used by `BridgePatches` for game hooks.
- **.NET 9 SDK** (MIT license, Microsoft) — required only to compile from source.
- **Wine / Proton** (LGPL-2.1+, Wine Project / Valve) — optional, for running the Windows game build on Linux.
- **AlphaDefect checkpoints** — the included `.s2nn` files are converted from the public
  AlphaDefect-Public checkpoints listed in the README; they are not stored in this source repository
  (downloaded separately from the upstream ReadyToUse release).
