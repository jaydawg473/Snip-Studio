"""Assemble verified data archives without executing their contents."""
import hashlib
import json
import re
import shutil
import stat
import tarfile
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parent
DOWNLOADS = ROOT / "downloads"
PAYLOAD = ROOT / "runtime"
PAYLOAD.mkdir(exist_ok=True)
verified = json.loads((DOWNLOADS / "verified-downloads.json").read_text(encoding="utf-8-sig"))
for item in verified:
    source = DOWNLOADS / item["Name"]
    digest = hashlib.new(item["Algorithm"].lower(), source.read_bytes()).hexdigest()
    if digest != item["Hash"]:
        raise RuntimeError("Asset hash changed: " + item["Name"])

def safe_parts(name):
    value = PurePosixPath(name)
    if value.is_absolute() or ".." in value.parts or "\\" in name or ":" in name:
        raise RuntimeError("Unsafe archive entry")
    return value.parts

def copy_tar(archive, destination, drop_prefix=True):
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, "r:gz") as source:
        members = source.getmembers()
        for member in members:
            parts = safe_parts(member.name)
            if not member.isfile() and not member.isdir():
                raise RuntimeError("Archive contains a special entry")
            if drop_prefix:
                parts = parts[1:]
            if not parts:
                continue
            target = destination.joinpath(*parts)
            target.resolve().relative_to(destination.resolve())
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with source.extractfile(member) as read, target.open("wb") as write:
                    shutil.copyfileobj(read, write)

node_target = PAYLOAD / "runtime"
node_target.mkdir(exist_ok=True)
with zipfile.ZipFile(DOWNLOADS / "node-v24.22.2-win-x64.zip") as source:
    for name in ["node.exe", "LICENSE", "README.md"]:
        entry = source.getinfo("node-v24.22.2-win-x64/" + name)
        safe_parts(entry.filename)
        if stat.S_ISLNK(entry.external_attr >> 16):
            raise RuntimeError("Node archive has a symbolic link")
        with source.open(entry) as read, (node_target / name).open("wb") as write:
            shutil.copyfileobj(read, write)

copy_tar(DOWNLOADS / "bergamot-translator-0.4.9.tgz", PAYLOAD / "bergamot")
models = json.loads((DOWNLOADS / "model-catalog-selected.json").read_text(encoding="utf-8-sig"))

def scalar(value):
    if value == "true": return True
    if value == "false": return False
    if re.fullmatch(r"-?\d+", value): return int(value)
    if re.fullmatch(r"-?\d+\.\d+", value): return float(value)
    if not re.fullmatch(r"[A-Za-z0-9._/-]+", value):
        raise RuntimeError("Unsupported model config scalar")
    return value

def parse_config(text):
    result = {}
    key = None
    for raw in text.splitlines():
        if not raw.strip() or raw.lstrip().startswith("#"): continue
        if raw.startswith((" ", "\t")):
            match = re.fullmatch(r"\s+-\s+(.+)", raw)
            if key is None or match is None or not isinstance(result[key], list):
                raise RuntimeError("Unsupported model config indentation")
            result[key].append(scalar(match.group(1)))
        else:
            match = re.fullmatch(r"([a-z][a-z0-9-]*):(?:\s+(.+))?", raw)
            if match is None or match.group(1) in result:
                raise RuntimeError("Unsupported model config mapping")
            key = match.group(1)
            result[key] = [] if match.group(2) is None else scalar(match.group(2))
    return result

entries = []
for model in models:
    name = model["shortName"]
    destination = PAYLOAD / name
    copy_tar(DOWNLOADS / (name + ".tar.gz"), destination)
    info = json.loads((destination / "model_info.json").read_text(encoding="utf-8"))
    if info["shortName"] != name:
        raise RuntimeError("Model identity differs from the signed catalog")
    config = parse_config((destination / "config.intgemm8bitalpha.yml").read_text(encoding="utf-8"))
    files = {"model": config["models"][0], "shortlist": config["shortlist"][0], "vocabs": config["vocabs"]}
    for filename in [files["model"], files["shortlist"], *files["vocabs"]]:
        if Path(filename).name != filename or not (destination / filename).is_file():
            raise RuntimeError("Required model file missing")
    # The desktop wrapper uses one interactive worker, not the large native CLI
    # batch/workspace values used to benchmark the original model archives.
    runtime_config = {key: value for key, value in config.items() if key not in ["models", "vocabs", "shortlist"]}
    runtime_config.update({"workspace": 128, "mini-batch": 1, "quiet": True, "quiet-translation": True})
    source_code, target_code, _ = name.split("-")
    entries.append({"source": source_code, "target": target_code, "directory": name,
                    "files": files, "config": runtime_config, "archiveUrl": model["url"], "archiveSha256": model["checksum"]})

shutil.copyfile(ROOT / "worker.js", PAYLOAD / "worker.js")
(ROOT / "bundle-draft.json").write_text(json.dumps({"version": 1, "engine": {"name": "Bergamot", "version": "0.4.9", "node": "24.22.2"}, "models": entries}, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"models": len(entries), "payloadFiles": sum(p.is_file() for p in PAYLOAD.rglob("*")), "bytes": sum(p.stat().st_size for p in PAYLOAD.rglob("*") if p.is_file())}))
