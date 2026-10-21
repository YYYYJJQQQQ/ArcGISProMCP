"""Build a location-independent binary payload with no personal configuration or Esri binaries."""
import argparse
import hashlib
import importlib.util
import json
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def prepare(destination):
    destination = destination.resolve()
    if not destination.is_relative_to(ROOT / "artifacts" / "publish"):
        raise ValueError("Installer output must stay in this project's artifacts/publish.")
    payload = destination / "payload"
    def document(name):
        authoring = ROOT / "docs/public" / name
        return authoring if authoring.exists() else ROOT / (name if name == "THIRD_PARTY_NOTICES.md" else "docs/" + name)
    bundle = "69c80ed9-ab3b-4aa6-9ea2-6577baed7019"
    spec = importlib.util.spec_from_file_location("exporter", ROOT / "scripts" / "export-open-source.py")
    exporter = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(exporter)

    def write(name, data):
        path = payload / name
        if path.exists():
            raise ValueError("Payload output already exists: " + name)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data if isinstance(data, bytes) else data.encode("utf-8"))

    def js(name, value):
        write(name, json.dumps(value, indent=2, ensure_ascii=False) + "\n")

    addin = ROOT / "artifacts/portable/bin/ArcGISProMCP.AddIn/Release/net10.0-windows/ArcGISProMCP.AddIn.esriAddinX"
    output = payload / "addin/ArcGISProMCP.AddIn.esriAddinX"
    output.parent.mkdir(parents=True)
    with zipfile.ZipFile(addin) as source, zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        for entry in source.infolist():
            if entry.filename.endswith(".pdb"):
                continue
            if entry.filename.startswith("Install/") and Path(entry.filename).name not in {"ArcGISProMCP.AddIn.dll", "ArcGISProMCP.Contracts.dll", "ArcGISProMCP.Transport.dll", "ArcGISProMCP.AddIn.deps.json"}:
                raise ValueError("Unexpected add-in runtime binary: " + entry.filename)
            data = source.read(entry)
            if entry.filename == "Config.daml":
                import re
                text = data.decode("utf-8-sig")
                text = re.sub(r"<Author>.*?</Author>", "<Author>YYYYJJQQQQ and contributors</Author>", text)
                data = text.replace("file:///F:/ArcGISpro/bin/ArcGIS.Desktop.Framework.xsd", "ArcGIS.Desktop.Framework.xsd").replace("<Company>Acme</Company>", "<Company>Community project</Company>").encode("utf-8")
            elif "/AddInDesktop" in entry.filename:
                data = exporter.icon(16 if "16.png" in entry.filename else 32)
            archive.writestr(entry.filename, data)
    js("ArcGISProMCP.install.json", {"schemaVersion": 1, "product": "ArcGISProMCP", "version": "1.9.0-preview.1", "bundleId": bundle})
    js("server/ArcGISProMCP.runtime.json", {"schemaVersion": 1, "root": "..", "bundleId": bundle})
    js("config/workspace-access.json", {"schemaVersion": 1, "grants": []})
    js("config/project-save.json", {"schemaVersion": 1, "allowAutonomousSave": False, "allowAutonomousProjectSwitch": False})
    js("config/runtime-storage.json", {"schemaVersion": 1, "backupKeepPerProject": 10, "backupMaxBytes": 536870912, "autoPrune": False})
    runtime = ROOT / "python/runtime-1.7.0"
    for path in runtime.iterdir():
        if path.is_file() and path.suffix in {".py", ".pyt", ".json"}:
            write("python/runtime-1.7.0/" + path.name, path.read_bytes())
    for name in ["LICENSE", "THIRD_PARTY_NOTICES.md"]:
        source = document(name) if name != "LICENSE" else ROOT / name
        write(name, source.read_bytes())
    license_root = ROOT / "docs/public/licenses" if (ROOT / "docs/public/licenses").exists() else ROOT / "docs/licenses"
    for path in license_root.glob("*.txt"):
        write("licenses/" + path.name, path.read_bytes())
    for name in ["INSTALL.zh-CN.md", "INSTALL.en.md", "AGENTS.zh-CN.md", "AGENTS.en.md"]:
        write("docs/" + name, document(name).read_bytes())
    # License texts shipped in the exact restored packages; dependencies are not relicensed by MIT.
    notices = []
    assets = json.loads((ROOT / "artifacts/portable/obj/ArcGISProMCP.Server/project.assets.json").read_text(encoding="utf-8-sig"))
    for key, lib in assets["libraries"].items():
        if lib["type"] != "package":
            continue
        base = ROOT / "artifacts/cache/nuget/packages" / lib["path"]
        names = [p for p in base.iterdir() if p.is_file() and ("license" in p.name.lower() or "notice" in p.name.lower())]
        notices.append(key)
        for file in names:
            write("licenses/" + key.replace("/", "-") + "/" + file.name, file.read_bytes())
    write("licenses/DEPENDENCIES.txt", "\n".join(notices) + "\n")
    files = []
    for path in sorted(payload.rglob("*")):
        if not path.is_file():
            continue
        if path.is_symlink() or getattr(path.stat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("Reparse payload entry.")
        if path.suffix.lower() == ".pdb" or path.name.startswith(("ArcGIS.", "Esri.", "ESRI.")):
            raise ValueError("Unexpected Esri/debug binary: " + str(path))
        files.append({"path": path.relative_to(payload).as_posix(), "bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest().upper()})
    js("payload.manifest.json", {"schemaVersion": 1, "files": files})
    with zipfile.ZipFile(destination / "payload.zip", "x", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(payload.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(payload).as_posix())
    print("Prepared binary payload:", len(files), "files; no developer consent or Esri DLLs.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--destination", type=Path, required=True)
    prepare(parser.parse_args().destination)
