"""Download a model from Hugging Face Hub and produce llama-server-ready GGUFs.

Pipeline:
  1. Download the repo via the `hf` CLI.
  2. If the weights are sharded (model.safetensors.index.json present), merge
     the shards into a single model.safetensors.
  3. Convert to a bf16 GGUF via llama.cpp's convert_hf_to_gguf.py.
  4. Export an mmproj GGUF too, if the model has a vision tower.
  5. Quantize the bf16 GGUF (llama-quantize) to the requested type.
  6. Delete everything except the final *.gguf file(s), so the output
     directory holds only what llama-server needs.

All tensors are held in memory at once during the shard-merge step, matching
how safetensors.torch.save_file works (it has no streaming/incremental API) -
fine for models that fit comfortably in RAM, not suitable for very large ones.
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

REPO_ROOT = Path(__file__).resolve().parent.parent
CONVERT_SCRIPT = REPO_ROOT / "src" / "convert_hf_to_gguf.py"
LLAMA_QUANTIZE = REPO_ROOT / "src" / "build" / "bin" / "llama-quantize.exe"


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


def convert_to_gguf(out_dir: Path, model_name: str) -> Path:
    if not CONVERT_SCRIPT.exists():
        raise FileNotFoundError(f"convert_hf_to_gguf.py not found at {CONVERT_SCRIPT}")

    bf16_path = out_dir / f"{model_name}-BF16.gguf"
    cmd = [sys.executable, str(CONVERT_SCRIPT), str(out_dir), "--outtype", "bf16", "--outfile", str(bf16_path)]
    print(f"[convert] {' '.join(cmd)}")
    subprocess.run(cmd, check=True)
    return bf16_path


def export_mmproj(out_dir: Path, model_name: str) -> Path | None:
    mmproj_path = out_dir / f"mmproj-{model_name}-F16.gguf"
    cmd = [
        sys.executable, str(CONVERT_SCRIPT), str(out_dir),
        "--mmproj", "--outtype", "f16", "--outfile", str(mmproj_path),
    ]
    print(f"[mmproj] {' '.join(cmd)}")
    result = subprocess.run(cmd)
    if result.returncode != 0:
        mmproj_path.unlink(missing_ok=True)
        print("[mmproj] Model has no exportable vision tower (or export failed) - skipping.")
        return None
    return mmproj_path


def quantize(bf16_path: Path, out_dir: Path, model_name: str, quant: str) -> Path:
    if not LLAMA_QUANTIZE.exists():
        raise FileNotFoundError(
            f"llama-quantize not found at {LLAMA_QUANTIZE} - build llama.cpp first."
        )
    quant_path = out_dir / f"{model_name}-{quant}.gguf"
    cmd = [str(LLAMA_QUANTIZE), str(bf16_path), str(quant_path), quant]
    print(f"[quantize] {' '.join(cmd)}")
    subprocess.run(cmd, check=True)
    return quant_path


def cleanup(out_dir: Path, keep: set[Path]) -> None:
    for entry in out_dir.iterdir():
        if entry in keep:
            continue
        if entry.is_dir():
            shutil.rmtree(entry)
        else:
            entry.unlink()
    print(f"[cleanup] {out_dir} now contains only: {', '.join(p.name for p in keep)}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("repo_id", help="Hugging Face repo id, e.g. org/model-name")
    parser.add_argument(
        "--out",
        help="Output directory (default: <repo root>/models/<model-name>)",
    )
    parser.add_argument("--revision", help="Branch/tag/commit to download")
    parser.add_argument("--token", help="Hugging Face token (for gated repos)")
    parser.add_argument(
        "--quant",
        default="Q4_K_M",
        help="llama-quantize target type for the final GGUF (default: Q4_K_M)",
    )
    args = parser.parse_args()

    model_name = args.repo_id.split("/")[-1]
    out_dir = Path(args.out) if args.out else REPO_ROOT / "models" / model_name
    out_dir.mkdir(parents=True, exist_ok=True)

    download(args.repo_id, out_dir, args.revision, args.token)
    merge_shards(out_dir)

    bf16_path = convert_to_gguf(out_dir, model_name)
    mmproj_path = export_mmproj(out_dir, model_name)
    quant_path = quantize(bf16_path, out_dir, model_name, args.quant)
    bf16_path.unlink()

    keep = {quant_path}
    if mmproj_path:
        keep.add(mmproj_path)
    cleanup(out_dir, keep)

    print(f"[done] {quant_path}")
    if mmproj_path:
        print(f"[done] {mmproj_path}")


if __name__ == "__main__":
    main()
