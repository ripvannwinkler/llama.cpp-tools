"""Download a model from Hugging Face Hub and recombine sharded safetensors.

Uses the `hf` CLI to download, then (if the model's weights are split into
shards described by a model.safetensors.index.json) merges the shards into a
single model.safetensors file so the local model directory ends up as a clean
single-file model.

All tensors are held in memory at once before writing, matching how
safetensors.torch.save_file works (it has no streaming/incremental API) -
fine for models that fit comfortably in RAM, not suitable for very large
ones.
"""

import argparse
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

from safetensors import safe_open
from safetensors.torch import save_file


def find_hf_cli() -> str:
    # Look next to the running interpreter first (venv's Scripts/bin dir),
    # since subprocess.run doesn't consult a venv's activated PATH.
    candidate = Path(sys.executable).parent / ("hf.exe" if sys.platform == "win32" else "hf")
    if candidate.exists():
        return str(candidate)
    found = shutil.which("hf")
    if found:
        return found
    raise FileNotFoundError("Could not find the `hf` CLI (expected it in the venv or on PATH).")


def download(repo_id: str, out_dir: Path, revision: str | None, token: str | None) -> None:
    cmd = [find_hf_cli(), "download", repo_id, "--local-dir", str(out_dir)]
    if revision:
        cmd += ["--revision", revision]
    if token:
        cmd += ["--token", token]
    print(f"[download] {' '.join(cmd)}")
    subprocess.run(cmd, check=True)


def merge_shards(out_dir: Path) -> None:
    index_path = out_dir / "model.safetensors.index.json"
    if not index_path.exists():
        print("[merge] No model.safetensors.index.json found - nothing to merge.")
        return

    index = json.loads(index_path.read_text(encoding="utf-8"))
    weight_map: dict[str, str] = index["weight_map"]

    shards_to_keys: dict[str, list[str]] = {}
    for tensor_name, shard_name in weight_map.items():
        shards_to_keys.setdefault(shard_name, []).append(tensor_name)

    print(f"[merge] Merging {len(shards_to_keys)} shard(s), {len(weight_map)} tensor(s)...")
    start = time.perf_counter()

    merged: dict[str, "torch.Tensor"] = {}
    for i, shard_name in enumerate(sorted(shards_to_keys), 1):
        shard_path = out_dir / shard_name
        keys = shards_to_keys[shard_name]
        print(f"[merge]   ({i}/{len(shards_to_keys)}) {shard_name}: {len(keys)} tensor(s)")
        with safe_open(shard_path, framework="pt", device="cpu") as f:
            for key in keys:
                merged[key] = f.get_tensor(key)

    if len(merged) != len(weight_map):
        missing = set(weight_map) - set(merged)
        raise RuntimeError(f"Tensor count mismatch after merge; missing: {sorted(missing)}")

    merged_path = out_dir / "model.safetensors"
    tmp_path = out_dir / "model.safetensors.tmp"
    save_file(merged, tmp_path)
    tmp_path.replace(merged_path)

    elapsed = time.perf_counter() - start
    size_gb = merged_path.stat().st_size / (1024 ** 3)
    print(f"[merge] Wrote {merged_path} ({size_gb:.2f} GiB) in {elapsed:.1f}s")

    for shard_name in shards_to_keys:
        (out_dir / shard_name).unlink()
    index_path.unlink()
    print(f"[merge] Removed {len(shards_to_keys)} shard file(s) and the index.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("repo_id", help="Hugging Face repo id, e.g. org/model-name")
    parser.add_argument(
        "--out",
        help="Output directory (default: <repo root>/models/<model-name>)",
    )
    parser.add_argument("--revision", help="Branch/tag/commit to download")
    parser.add_argument("--token", help="Hugging Face token (for gated repos)")
    args = parser.parse_args()

    if args.out:
        out_dir = Path(args.out)
    else:
        model_name = args.repo_id.split("/")[-1]
        out_dir = Path(__file__).resolve().parent.parent / "models" / model_name
    out_dir.mkdir(parents=True, exist_ok=True)

    download(args.repo_id, out_dir, args.revision, args.token)
    merge_shards(out_dir)


if __name__ == "__main__":
    main()
