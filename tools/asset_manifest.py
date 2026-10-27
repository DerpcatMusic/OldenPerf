"""Install/restore one compressed asset only when its exact source build matches."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def sha256(path):
    with open(path, "rb") as handle:
        return hashlib.file_digest(handle, "sha256").hexdigest()


def require_hash(path, expected):
    if not path.is_file() or sha256(path) != expected:
        raise ValueError(f"Version/hash mismatch: {path}. Refusing to overwrite it.")


def copy_reflink(source, target):
    subprocess.run(["cp", "--reflink=auto", "--", str(source), str(target)], check=True)


def apply(action, manifest_path, game_data, backup_dir, check_running=True):
    manifest_path, game_data, backup_dir = map(Path, (manifest_path, game_data, backup_dir))
    manifest = json.loads(manifest_path.read_text())
    name = manifest["asset"]
    if manifest.get("schema") != 1 or Path(name).name != name:
        raise ValueError("Invalid asset manifest")
    if check_running:
        for comm in Path("/proc").glob("[0-9]*/comm"):
            try:
                if comm.read_text().strip().startswith("HeroesOldenEra"):
                    raise ValueError("Close Olden Era before changing its assets")
            except (FileNotFoundError, PermissionError, ProcessLookupError):
                continue
    for external in manifest.get("externals", []):
        if Path(external["name"]).name != external["name"]:
            raise ValueError("Invalid external resource path")
        require_hash(game_data / external["name"], external["sha256"])
    if manifest.get("gameassembly_sha256"):
        require_hash(game_data.parent / "GameAssembly.dll", manifest["gameassembly_sha256"])
    target, backup = game_data / name, backup_dir / name
    wanted = manifest["source_sha256"] if action == "install" else manifest["patched_sha256"]
    require_hash(target, wanted)
    if action == "install":
        source = Path(str(manifest_path).removesuffix(".manifest.json"))
        require_hash(source, manifest["patched_sha256"])
        backup_dir.mkdir(parents=True, exist_ok=True)
        if backup.exists():
            require_hash(backup, manifest["source_sha256"])
        else:
            copy_reflink(target, backup)
            require_hash(backup, manifest["source_sha256"])
        expected_output = manifest["patched_sha256"]
    elif action == "uninstall":
        require_hash(backup, manifest["source_sha256"])
        source, expected_output = backup, manifest["source_sha256"]
    else:
        raise ValueError("Expected install or uninstall")
    temporary = game_data / (".oldenperf-" + name + ".partial")
    if temporary.exists():
        raise ValueError(f"Stale staging file exists: {temporary}")
    try:
        copy_reflink(source, temporary)
        require_hash(temporary, expected_output)
        # Detect a concurrent asset update before replacing the target.
        require_hash(target, wanted)
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)
    print(f"{action}: {name}; verified exact asset and external-resource hashes")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["install", "uninstall"])
    parser.add_argument("manifest", type=Path)
    parser.add_argument("game_data", type=Path)
    parser.add_argument("--backup-dir", type=Path, required=True)
    args = parser.parse_args()
    apply(args.action, args.manifest, args.game_data, args.backup_dir)
