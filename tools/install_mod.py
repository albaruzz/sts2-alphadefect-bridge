#!/usr/bin/env python3
"""Cross-platform installer for Sts2Bridge v1.2.0 into <Slay the Spire 2>/mods/Sts2Bridge/."""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
BRIDGE_PROJ = REPO / "engine" / "Sts2Bridge" / "Sts2Bridge.csproj"
CATALOG_DIR = REPO / "engine" / "catalog"

MOD_FILES = (
    "Sts2Bridge.dll",
    "Sts2Bridge.json",
    "bridge_config.json",
    "vocab.txt",
    "aliases.txt",
    "no_downside_relics.txt",
)


def find_data_and_mods_dir(game_root: Path) -> tuple[Path, Path]:
    # macOS .app bundle: SlayTheSpire2.app/Contents/MacOS/mods/Sts2Bridge/
    if game_root.suffix == ".app" or (game_root / "Contents" / "MacOS").is_dir():
        app_root = game_root
        mods_root = app_root / "Contents" / "MacOS" / "mods" / "Sts2Bridge"
        for sub in (
            "Contents/Resources/data_sts2_macos_arm64",
            "Contents/Resources/data_sts2_macos_x86_64",
            "Contents/Resources",
        ):
            cand = app_root / sub
            if (cand / "sts2.dll").exists():
                return cand, mods_root
    elif (game_root / "SlayTheSpire2.app").is_dir():
        return find_data_and_mods_dir(game_root / "SlayTheSpire2.app")

    for sub in ("data_sts2_windows_x86_64", "data_sts2_linux_x86_64", ""):
        cand = game_root / sub if sub else game_root
        if (cand / "sts2.dll").exists():
            return cand, game_root / "mods" / "Sts2Bridge"
    raise FileNotFoundError(f"Could not locate sts2.dll inside {game_root}")


def main() -> None:
    ap = argparse.ArgumentParser(description="Build and install Sts2Bridge into Slay the Spire 2/mods/Sts2Bridge/")
    ap.add_argument("--game-dir", type=Path, required=True, help="Path to Slay the Spire 2 Steam root directory or SlayTheSpire2.app")
    ap.add_argument("--game-version", default=None, help="Target STS2 version (e.g. v0.114.0, v0.113.1, v0.111.0)")
    ap.add_argument("--models-dir", type=Path, default=None, help="Optional directory containing exported <CHARACTER>.s2nn files to bundle into mods/Sts2Bridge/models/")
    ap.add_argument("--skip-build", action="store_true", help="Copy pre-built Release output without invoking dotnet build")
    args = ap.parse_args()

    game_root = args.game_dir.expanduser().resolve()
    data_dir, dest_dir = find_data_and_mods_dir(game_root)

    if not args.skip_build:
        cmd = ["dotnet", "build", str(BRIDGE_PROJ), "-c", "Release", f"-p:Sts2GameDir={data_dir}"]
        if args.game_version:
            cmd.append(f"-p:Sts2GameVersion={args.game_version}")
        print("Running:", " ".join(cmd))
        subprocess.run(cmd, check=True)

    out_dir = REPO / "engine" / "Sts2Bridge" / "bin" / "Release" / "net9.0"
    dest_dir.mkdir(parents=True, exist_ok=True)

    for fname in MOD_FILES:
        src = out_dir / fname
        if not src.exists() and (CATALOG_DIR / fname).exists():
            src = CATALOG_DIR / fname
        if not src.exists():
            print(f"Error: missing build artifact {src}", file=sys.stderr)
            sys.exit(1)
        dst = dest_dir / fname
        if fname == "bridge_config.json" and dst.exists():
            print(f"Keeping existing {dst}")
            continue
        shutil.copy2(src, dst)
        print(f"Installed {dst}")

    if args.models_dir and args.models_dir.is_dir():
        dst_models = dest_dir / "models"
        dst_models.mkdir(parents=True, exist_ok=True)
        for s2nn in sorted(args.models_dir.glob("*.s2nn")):
            shutil.copy2(s2nn, dst_models / s2nn.name)
            print(f"Installed {dst_models / s2nn.name}")

    print(f"\nSts2Bridge installed successfully into: {dest_dir}")


if __name__ == "__main__":
    main()
