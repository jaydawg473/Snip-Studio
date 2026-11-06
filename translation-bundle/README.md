# Translation source and release provenance

The complete signed Snip Studio release contains the local Bergamot/Node runtime, six English/German/Spanish/French model directions, license notices and corresponding-source archives. Keep its entire `translation` directory beside the application. The source checkout excludes runtime executables, WebAssembly, model weights/vocabularies and all archives.

This folder retains Snip Studio's `worker.js` bridge, the modified licensed Bergamot `runtime/bergamot/worker/translator-worker.js`, CommonJS metadata, pinned upstream metadata, preparation scripts, the release bundle manifest and license texts. The modified worker handles Windows file URLs and rejects non-file translation fetches. Upstream source locations and hashes are recorded in the manifest and notices.

The `runtime/bundle.json` and notices describe the **complete release payload**. Referenced archives and model files are supplied in that release, rather than this checkout. They are not claims that those files exist here. Component licenses remain attached to their respective components; no application license grant is added.

These scripts are retained as preparation source, not a fully automatic, reproducible release builder. `Prepare-Downloads.ps1` needs PowerShell 7 for parallel downloads; `assemble_payload.py` and `finalize_manifest.py` use Python 3 for maintainer preparation. Assembly still needs the reviewed license/corresponding-source inputs and the checked-in worker modifications. Finalization records those inputs only after they are present and reviewed. The installed offline translator itself requires neither Python nor PowerShell 7, and performs no model download.

Building the C# application does not assemble or copy a translation runtime. A local application build remains unsigned. For the included offline translator, use the complete signed portable/Setup release. Alternatively, the separately configured LibreTranslate integration requires a trusted compatible server; it is not started by Snip Studio.
