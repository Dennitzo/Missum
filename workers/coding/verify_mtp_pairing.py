"""Read-only probe for multi-token prediction pairing decisions.

Opens only GGUF headers and tensor name directories. Never loads model weights,
never writes, never touches the network. The point is to record what a real
prediction head actually declares and contains, so the catalog classification can
be based on the measured tensor layout instead of an arithmetic guess.

Usage:
    python verify_mtp_pairing.py                      # default catalog roots
    python verify_mtp_pairing.py --root <dir>         # one explicit root
    python verify_mtp_pairing.py --only Qwen3.8       # name substring filter
    python verify_mtp_pairing.py --preset-check       # installed router and presets
"""
from __future__ import annotations

import argparse
import configparser
import os
import re
import struct
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import catalog  # noqa: E402  (local supervisor module, read-only use)

SCALAR_SIZES = {0: 1, 1: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 7: 1, 10: 8, 11: 8, 12: 8}
BLOCK_TENSOR = re.compile(r"^blk\.(\d+)\.(.+)$")
PREDICTION_MARK = "nextn"
INTERESTING = (
    "general.architecture", "general.name", "general.type",
    ".block_count", ".nextn_predict_layers", ".embedding_length", ".vocab_size",
    ".context_length",
)


def read_string(stream, limit=16 * 1024 * 1024):
    raw = stream.read(8)
    if len(raw) != 8:
        raise ValueError("truncated GGUF string length")
    size = struct.unpack("<Q", raw)[0]
    if size > limit:
        raise ValueError("GGUF string exceeds bound")
    return stream.read(size).decode("utf-8", errors="replace")


def skip_value(stream, kind):
    if kind == 8:
        read_string(stream)
    elif kind == 9:
        element = struct.unpack("<I", stream.read(4))[0]
        count = struct.unpack("<Q", stream.read(8))[0]
        if count > 10_000_000 or element == 9:
            raise ValueError("invalid GGUF array")
        if element in SCALAR_SIZES:
            stream.read(SCALAR_SIZES[element] * count)
        else:
            for _ in range(count):
                skip_value(stream, element)
    elif kind in SCALAR_SIZES:
        stream.read(SCALAR_SIZES[kind])
    else:
        raise ValueError("unknown GGUF value kind")


def read_gguf_header(path):
    """Return (metadata, tensor_names) from a GGUF header only."""
    with path.open("rb", buffering=256 * 1024) as stream:
        magic, version, tensors, count = struct.unpack("<4sIQQ", stream.read(24))
        if magic != b"GGUF" or version not in (2, 3):
            raise ValueError("not a GGUF file")
        metadata = {"tensor_count": tensors, "gguf_version": version}
        for _ in range(count):
            key = read_string(stream)
            kind = struct.unpack("<I", stream.read(4))[0]
            if key.endswith((".rope.freq_base", ".rope.scaling.factor")) and kind == 6:
                metadata[key] = struct.unpack("<f", stream.read(4))[0]
            elif kind == 8:
                metadata[key] = read_string(stream)
            elif kind == 4:
                metadata[key] = struct.unpack("<I", stream.read(4))[0]
            else:
                skip_value(stream, kind)
        names = []
        for _ in range(tensors):
            name = read_string(stream)
            dimensions = struct.unpack("<I", stream.read(4))[0]
            if not 1 <= dimensions <= 4:
                raise ValueError("invalid tensor dimension count")
            stream.read(dimensions * 8 + 12)
            names.append(name)
    return metadata, names


def describe(path, root):
    metadata, names = read_gguf_header(path)
    architecture = str(metadata.get("general.architecture", "")).lower()

    def get(suffix):
        return metadata.get(architecture + suffix)

    # A prediction layer is a full decoder layer that additionally carries the
    # nextn.* tensors. Block indices that contain any prediction tensor are head
    # blocks and are therefore NOT base layers, even though they hold ordinary
    # attention and feed-forward weights of their own.
    all_blocks, prediction_blocks = set(), set()
    prediction_tensors, other_tensors = [], []
    for name in names:
        match = BLOCK_TENSOR.fullmatch(name)
        if not match:
            other_tensors.append(name)
            continue
        index, tail = int(match[1]), match[2]
        all_blocks.add(index)
        if tail.startswith(PREDICTION_MARK):
            prediction_blocks.add(index)
            prediction_tensors.append(name)
    base_blocks = all_blocks - prediction_blocks
    projection = [name for name in prediction_tensors if name.endswith("eh_proj.weight")]
    head = bool(prediction_tensors)

    info = {
        "path": path,
        "relative": path.relative_to(root).as_posix() if path.is_relative_to(root) else str(path),
        "size": path.stat().st_size,
        "architecture": architecture,
        "general_name": metadata.get("general.name"),
        "block_count": get(".block_count"),
        "nextn_predict_layers": get(".nextn_predict_layers"),
        "embedding_length": get(".embedding_length"),
        "vocab_size": get(".vocab_size"),
        "context_length": get(".context_length"),
        "tensor_count": metadata.get("tensor_count"),
        "base_block_count": len(base_blocks),
        "base_blocks": base_blocks,
        "all_block_count": len(all_blocks),
        "all_blocks": all_blocks,
        "prediction_blocks": prediction_blocks,
        "prediction_tensor_count": len(prediction_tensors),
        "projection_tensors": projection,
        "non_block_tensors": other_tensors,
        "head": head,
        "raw_metadata": metadata,
    }
    info["kind"] = classify(info)
    return info


def classify(info):
    """Layout based classification, independent of any arithmetic relation."""
    if not info["head"]:
        return None
    if not info["projection_tensors"]:
        return None
    if info["base_block_count"] == 0:
        return "sidecar"
    return "embedded"


def current_catalog_verdict(info):
    """Ask the shipped catalog rule itself, so the comparison is not a re-implementation."""
    blocks = info["block_count"] or 0
    nextn = info["nextn_predict_layers"] or 0
    try:
        kind = catalog.mtp_kind(dict(info["raw_metadata"]), [info["path"]])
    except Exception as error:  # noqa: BLE001  (the probe only reports)
        return f"catalog.mtp_kind raised {type(error).__name__}"
    if kind:
        return f"catalog.mtp_kind -> {kind}"
    detail = f"nextn={nextn or 'absent'}, block_count={blocks or 'absent'} fails '0 < nextn < block_count'"
    return f"catalog.mtp_kind -> None ({detail})"


def base_reason(base, head):
    """Return why this file cannot serve as the base model of a sidecar head."""
    if base["base_block_count"] == 0:
        return "no own transformer blocks"
    if base["prediction_tensor_count"]:
        return "already carries its own head"
    if base["embedding_length"] and head["embedding_length"] and base["embedding_length"] != head["embedding_length"]:
        return f"embedding length {base['embedding_length']} != {head['embedding_length']}"
    if base["vocab_size"] and head["vocab_size"] and base["vocab_size"] != head["vocab_size"]:
        return f"vocab size {base['vocab_size']} != {head['vocab_size']}"
    return ""


def report(info):
    print(f"\n{info['relative']}")
    print(f"  size              {info['size']:,} bytes")
    print(f"  architecture      {info['architecture'] or '(none)'}  tensors={info['tensor_count']}")
    print(f"  block_count       {info['block_count']}")
    print(f"  nextn_predict     {info['nextn_predict_layers']}")
    print(f"  embedding_length  {info['embedding_length']}  vocab_size={info['vocab_size']}")
    print(f"  context_length    {info['context_length']}")
    print(f"  all block indices       {summarise(info['all_blocks'])}")
    print(f"  base block indices      {summarise(info['base_blocks'])}")
    print(f"  prediction block indices {sorted(info['prediction_blocks'])}")
    print(f"  prediction tensors  {info['prediction_tensor_count']}  projections={[Path(n).name for n in info['projection_tensors']]}")
    print(f"  non-block tensors   {len(info['non_block_tensors'])} {[Path(n).name for n in info['non_block_tensors'][:8]]}")
    print(f"  layout classification   {info['kind'] or 'no head -> not a predictor'}")
    print(f"  old catalog rule        {current_catalog_verdict(info)}")


def summarise(indices):
    if not indices:
        return "none"
    ordered = sorted(indices)
    if len(ordered) > 12:
        return f"{len(ordered)} blocks, {ordered[0]}..{ordered[-1]}"
    return str(ordered)


def installed_binary():
    """The router binary this installation actually runs, or None."""
    hint = os.environ.get("MISSUM_NATIVE_LLAMA_SERVER", "").strip()
    candidates = [Path(hint)] if hint else []
    candidates.append(Path.home() / ".unsloth" / "llama.cpp" / "build" / "bin" / "Release" / "llama-server.exe")
    return next((item for item in candidates if item.is_file()), None)


def preset_check(roots):
    """Prove the pairing against the installed router without loading weights.

    Reads only: catalog discovery, the router help text and real file sizes. The
    preset itself is written into a throwaway directory, never into the runtime
    state, so this cannot disturb a running supervisor.
    """
    binary = installed_binary()
    print(f"router binary        {binary or '(not found)'}")
    supported, reason = catalog.native_mtp_support(str(binary)) if binary else (False, "no binary configured")
    print(f"capability probe     supported={supported} ({reason})")
    with tempfile.TemporaryDirectory() as temporary:
        target = Path(temporary) / "models.ini"
        for root in roots:
            if not root.is_dir():
                continue
            entries = catalog.discover_models(root)
            paired = [entry for entry in entries if entry.get("mtp") or entry.get("mtp_embedded")]
            print(f"\nroot {root}\n  catalog entries {len(entries)}, entries with a companion {len(paired)}")
            for entry in paired:
                draft = Path(entry["mtp"]) if entry.get("mtp") else None
                offered = bool(draft) and any(Path(item["path"]).samefile(draft) for item in entries)
                lean = {key: value for key, value in entry.items()
                        if key not in ("mtp", "mtp_shards", "mtp_alternatives")}
                with_draft = catalog.model_file_bytes(entry)
                plain = catalog.model_file_bytes(lean)
                print(f"  {entry['id']}")
                print(f"    companion            {draft.name if draft else '(embedded in target)'}")
                print(f"    offered as own model {offered}  alternatives {len(entry.get('mtp_alternatives') or ())}")
                print(f"    admission            {plain:,} bytes plain, {with_draft:,} with draft "
                      f"(+{with_draft - plain:,})")
            if not paired:
                continue
            catalog.write_presets(root, target, binary=str(binary) if binary else None)
            parser = configparser.ConfigParser(interpolation=None)
            parser.read_string("[preset-header]\n" + target.read_text(encoding="utf-8"))
            for entry in paired:
                block = dict(parser[entry["id"]])
                print(f"    preset {entry['id']}")
                print(f"      spec-type         {block.get('spec-type')}")
                print(f"      spec-draft-model  {block.get('spec-draft-model')}")
                print(f"      spec-draft-n-max  {block.get('spec-draft-n-max')}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", action="append", dest="roots",
                        help="catalog root to inspect; repeatable")
    parser.add_argument("--only", default="", help="substring filter on the file name")
    parser.add_argument("--discover", action="store_true",
                        help="also run the shipped catalog discovery and show the pairing result")
    parser.add_argument("--preset-check", action="store_true",
                        help="prove pairing against the installed router and written presets")
    arguments = parser.parse_args(argv)
    roots = [Path(item).expanduser().resolve() for item in (arguments.roots or [])]
    if not roots:
        roots = [Path.home() / ".cache" / "huggingface" / "hub"]
        alternative = Path("E:/Huggingface/hub")
        if alternative.exists():
            roots.append(alternative)
    selected = []
    for root in roots:
        if not root.is_dir():
            print(f"skipping missing root {root}")
            continue
        for path in sorted(root.rglob("*.gguf")):
            if path.is_file() and (not arguments.only or arguments.only.casefold() in path.name.casefold()):
                selected.append((root, path))
    print(f"inspecting {len(selected)} GGUF file(s) in {len(roots)} root(s)")
    inspected = []
    for root, path in selected:
        try:
            info = describe(path, root)
        except Exception as error:  # noqa: BLE001  (probe must keep going)
            print(f"\n{path}\n  unreadable header: {type(error).__name__}: {error}")
            continue
        report(info)
        inspected.append((root, info))
    print("\n== pairing decisions ==")
    heads = [(root, info) for root, info in inspected if info["kind"] == "sidecar"]
    for root, head in heads:
        print(f"{head['relative']}  [{head['architecture']}]")
        print(f"  old rule says    {current_catalog_verdict(head)}")
        for base_root, base in inspected:
            if base_root != root or base is head or base["architecture"] != head["architecture"]:
                continue
            reason = base_reason(base, head)
            if reason:
                print(f"  base rejected    {base['relative']} -> {reason}")
            else:
                print(f"  base accepted    {base['relative']}")
    if not heads:
        print("no sidecar prediction head found among the inspected files")
    if arguments.discover:
        print("\n== shipped catalog discovery ==")
        for root in roots:
            if not root.is_dir():
                continue
            for entry in catalog.discover_models(root):
                marker = []
                if entry.get("mtp_embedded"):
                    marker.append("mtp=embedded")
                elif entry.get("mtp"):
                    marker.append(f"mtp={Path(entry['mtp']).name}")
                else:
                    marker.append("mtp=none")
                print(f"  {entry['id']}  role={entry['role']}  {' '.join(marker)}")
    if arguments.preset_check:
        print("\n== installed router and preset proof ==")
        preset_check(roots)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
