"""Write the release gate atomically after package and notice review."""
import hashlib
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PAYLOAD = ROOT / "runtime"
draft = json.loads((ROOT / "bundle-draft.json").read_text(encoding="utf-8"))
node = PAYLOAD / "runtime" / "node.exe"
if hashlib.sha256(node.read_bytes()).hexdigest() != "1c331340fa6cac5e4294725959070a143fa0507195a78ad77eed7943f719aac9":
    raise RuntimeError("The verified original Node executable changed")
expected_pairs = {("en", lang) for lang in ("de", "es", "fr")} | {(lang, "en") for lang in ("de", "es", "fr")}
if {(item["source"], item["target"]) for item in draft["models"]} != expected_pairs or len(draft["models"]) != 6:
    raise RuntimeError("Incorrect model pairs")
for item in draft["models"]:
    directory = PAYLOAD / item["directory"]
    info = json.loads((directory / "model_info.json").read_text(encoding="utf-8"))
    if info["shortName"] != item["directory"]:
        raise RuntimeError("Incorrect model metadata")
    for name in [item["files"]["model"], item["files"]["shortlist"], *item["files"]["vocabs"]]:
        if not (directory / name).is_file():
            raise RuntimeError("Missing required model data")

required_notices = [
    "licenses/NOTICES.txt", "licenses/Node-LICENSE.txt", "licenses/Bergamot-MPL-2.0.txt",
    "licenses/Bergamot-model-policy.md", "licenses/CC-BY-SA-4.0.txt",
    "licenses/bergamot-source-8cc5d049.tar.gz", "licenses/components/Marian/LICENSE.md",
    "licenses/components/Intgemm/LICENSE", "licenses/components/SentencePiece/LICENSE",
    "licenses/components/PCRE2/LICENCE", "licenses/components/SSplit/LICENSE.md",
    "licenses/components/SSplit/Apache-2.0.txt", "licenses/components/SSplit/LGPL-2.1.txt",
    "licenses/components/Emscripten/LICENSE", "licenses/components/Emscripten/musl-COPYRIGHT",
    "licenses/components/Emscripten/libcxx-LICENSE.txt", "licenses/components/Boost/LICENSE_1_0.txt",
    "licenses/components/Marian/source-e88c1aa5.tar.gz",
]
for relative in required_notices:
    if not (PAYLOAD / relative).is_file() or (PAYLOAD / relative).stat().st_size < 100:
        raise RuntimeError("Missing notice/source: " + relative)

draft["licenseReview"] = {
    "complete": True,
    "reviewedOn": "2026-11-05",
    "notices": required_notices,
    "basis": "Pinned component license files, corresponding Bergamot source, original Node notices, and the published Bergamot model-license policy; no Qt or MKL binary is redistributed.",
    "modelPolicy": "The six official Bergamot model archives contain no separate license text; attribution and CC-BY-SA-4.0 follow the published Bergamot project model policy included in licenses/Bergamot-model-policy.md.",
}
draft["sources"] = {
    "bergamotNpm": "https://registry.npmjs.org/@browsermt/bergamot-translator/-/bergamot-translator-0.4.9.tgz",
    "bergamotNpmIntegrity": "sha512-bNuuCwM/JnsIYQCKXcYKFT4Qc5vLMoB8Nbvz8ReIgs7xzebK8Sa+R8iEK8mLvNBe/WaZ6zrPaUQilLsRT/ea8Q==",
    "bergamotCommit": "8cc5d0495479c9ec56eafafd6bcd7fb5b929ca98",
    "nodeZip": "https://nodejs.org/dist/v24.22.2/node-v24.22.2-win-x64.zip",
    "nodeZipSha256": "ca1bff5d8ead13c9f2ea404a176db46bd430704d5b5113734049fa365e7799e2",
    "nodeSigner": "OpenJS Foundation (original Authenticode signature verified Valid before use)",
}
draft["files"] = []
total = 0
for item in sorted(PAYLOAD.rglob("*")):
    if not item.is_file() or item.name in {"bundle.json", "bundle.json.tmp"}:
        continue
    if item.is_symlink():
        raise RuntimeError("Symbolic links are not allowed")
    relative = item.relative_to(PAYLOAD).as_posix()
    if "downloads" in item.parts or "translateLocally" in item.name:
        raise RuntimeError("Unreviewed download entered payload")
    total += item.stat().st_size
    draft["files"].append({"path": relative, "sha256": hashlib.sha256(item.read_bytes()).hexdigest()})
temporary = PAYLOAD / "bundle.json.tmp"
temporary.write_text(json.dumps(draft, indent=2) + "\n", encoding="utf-8")
os.replace(temporary, PAYLOAD / "bundle.json")
print(json.dumps({"ready": True, "files": len(draft["files"]), "modelPairs": len(draft["models"]), "bytes": total, "manifestSha256": hashlib.sha256((PAYLOAD / "bundle.json").read_bytes()).hexdigest()}))
