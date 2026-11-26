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
