"""Create a fail-closed source distribution; never copy live config or private GIS data."""
import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BLOCKED = {"bin", "obj", "__pycache__", ".vs", ".git"}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def icon(size):
    # Original linked-node mark, no copied SDK imagery or external font.
    pixels = bytearray()
    for y in range(size):
        pixels.append(0)
        for x in range(size):
            fx, fy = x / size, y / size
            node = any((fx - a) ** 2 + (fy - b) ** 2 < 0.013 for a, b in [(0.25, 0.3), (0.75, 0.3), (0.5, 0.75)])
            link = (abs(fy - 0.3) < 0.035 and 0.25 < fx < 0.75) or (abs(fy - (0.3 + 1.8 * (fx - 0.25))) < 0.05 and 0.25 < fx < 0.5) or (abs(fy - (0.3 + 1.8 * (0.75 - fx))) < 0.05 and 0.5 < fx < 0.75)
            pixels.extend((255, 255, 255, 255) if node or link else (28, 99, 166, 255))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">2I5B", size, size, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b"")


def export(destination):
    destination = destination.resolve()
    if not destination.is_relative_to(ROOT / "artifacts" / "publish") or destination.exists():
        raise ValueError("Use a NEW directory inside this project's artifacts/publish.")
    for ancestor in [destination, *destination.parents]:
        if ancestor.exists() and (ancestor.is_symlink() or getattr(ancestor.stat(), "st_file_attributes", 0) & 0x400):
            raise ValueError("Reparse point in export path.")
        if ancestor == ROOT:
            break
    destination.mkdir(parents=True)
    live_config = ROOT / "config" / "workspace-access.json"
    live_paths = [g["path"] for g in json.loads(live_config.read_text(encoding="utf-8-sig")).get("grants", [])] if live_config.exists() else []
    redactions = {str(Path.home()): r"C:\Users\ExampleUser"}
    for path in live_paths:
        redactions[path] = r"D:\GIS\ExampleWorkspace"
        redactions[Path(path).name] = "示例GIS工作区"
    def copy(source, relative):
        if source.is_symlink() or getattr(source.stat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("Reparse point in source: " + str(source))
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        if source.suffix.lower() not in {".png"}:
            text = target.read_text(encoding="utf-8-sig")
            for original, replacement in redactions.items():
                text = text.replace(original, replacement)
            target.write_text(text, encoding="utf-8")
    def write(relative, text):
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text.replace("\r\n", "\n").replace("\n", "\r\n"), encoding="utf-8", newline="")
    for name in [".editorconfig", "ArcGISProMCP.AddIn.slnx", "Directory.Build.props", "nuget.config", "LICENSE"]:
        copy(ROOT / name, name)
    for folder in ["src", "ArcGISProMCP.AddIn", "python", "skills", "tests/McpIntegration", "tests/BridgeSmoke", "tests/Fixtures", "tools/ArcGISProMCP.Probe"]:
        for current, dirs, files in os.walk(ROOT / folder, followlinks=False):
            for name in dirs:
                path = Path(current) / name
                if name not in BLOCKED and (path.is_symlink() or getattr(path.stat(), "st_file_attributes", 0) & 0x400):
                    raise ValueError("Reparse directory in source.")
            dirs[:] = [d for d in dirs if d not in BLOCKED]
            for name in files:
                source = Path(current) / name
                if name in {"launchSettings.json"} or source.suffix in {".pyc", ".pdb", ".dll", ".exe"}:
                    continue
                copy(source, source.relative_to(ROOT))
    for name in ["build.ps1", "publish-server.ps1", "audit-dependencies.ps1", "export-open-source.py"]:
        copy(ROOT / "scripts" / name, "scripts/" + name)
    root_docs = {"README.md", "README.en.md", "CONTRIBUTING.md", "SECURITY.md", "THIRD_PARTY_NOTICES.md"}
    doc_names = [*sorted(root_docs), "INSTALL.zh-CN.md", "INSTALL.en.md", "AGENTS.zh-CN.md", "AGENTS.en.md", "MAINTENANCE.md", "ARCHITECTURE.md", "VALIDATION.md"]
    for name in doc_names:
        relative = name if name in root_docs else "docs/" + name
        authoring = ROOT / "docs" / "public" / name
        source = authoring if authoring.exists() else ROOT / relative
        copy(source, relative)
    for folder in ["Images", "DarkImages"]:
        for size in [16, 32]:
            (destination / "ArcGISProMCP.AddIn" / folder / f"AddInDesktop{size}.png").write_bytes(icon(size))
    # Remove a machine-specific debugger entry without altering the private source.
    project = destination / "ArcGISProMCP.AddIn" / "ArcGISProMCP.AddIn.csproj"
    project.write_text(project.read_text(encoding="utf-8").replace('    <None Include="Properties\\launchSettings.json" />', ''), encoding="utf-8")
    daml = destination / "ArcGISProMCP.AddIn" / "Config.daml"
    daml_text = re.sub(r'<Author>[^<]*</Author>', '<Author>YYYYJJQQQQ and contributors</Author>', daml.read_text(encoding="utf-8"))
    daml.write_text(daml_text.replace('file:///F:/ArcGISpro/bin/ArcGIS.Desktop.Framework.xsd', 'ArcGIS.Desktop.Framework.xsd').replace('<Company>Acme</Company>', '<Company>Community project</Company>'), encoding="utf-8")
    preview = destination / "tests/McpIntegration/WorkspaceSettingsPreview.cs"
    preview.write_text(re.sub(r'content\.SelectDirectory\(@"[^"\r\n]*"\);', '// Preview selects the first configured directory; no private machine path.', preview.read_text(encoding="utf-8")), encoding="utf-8")
    write("config/workspace-access.json", json.dumps({"schemaVersion": 1, "grants": []}, indent=2) + "\n")
    write("config/project-save.json", json.dumps({"schemaVersion": 1, "allowAutonomousSave": False, "allowAutonomousProjectSwitch": False}, indent=2) + "\n")
    write("config/runtime-storage.json", json.dumps({"schemaVersion": 1, "backupKeepPerProject": 10, "backupMaxBytes": 536870912, "autoPrune": False}, indent=2) + "\n")
    write(".gitignore", "artifacts/\n**/bin/\n**/obj/\n**/__pycache__/\n.vs/\n.codex/\nTestProjects/\nWorkspaces/\nconfig/workspace-access.json\nconfig/project-save.json\nconfig/runtime-storage.json\n*.user\n*.suo\n.env\n")
    for name in ["workspace-access", "project-save", "runtime-storage"]:
        copy(destination / "config" / (name + ".json"), "config/" + name + ".example.json")
    write("AGENTS.md", "# Contributor guidance / 协作约定\n\nRead README and docs/VALIDATION.md first. Keep SDK access inside QueuedTask.Run; only DTOs leave MCT. Preserve MCP names/parameters/protocol v1. Never self-authorize, overwrite raw GIS, replay uncertain writes or replace a running add-in/toolbox. Build/test per INSTALL, record limits, and inspect rendered maps.\n\n先读README与验证状态；沿用线程、权限和接口边界，测试使用独立工程。缺少能力时复用可用客户端工具，不绕过拒绝。所有生成文件保存在本项目artifacts内。\n")
    exe = r"D:\Tools\ArcGISProMCP\artifacts\publish\staging\ArcGISProMCP.Server\ArcGISProMCP.Server.exe"
    server = {"command": exe, "args": []}
    for client in ["claude-code", "claude-desktop", "cursor", "gemini"]:
        write("examples/clients/" + client + ".json", json.dumps({"mcpServers": {"arcgispro": server}}, indent=2) + "\n")
    write("examples/clients/vscode.json", json.dumps({"servers": {"arcgispro": {"type": "stdio", **server}}}, indent=2) + "\n")
    write("examples/clients/codex.toml", "[mcp_servers.arcgispro]\ncommand = '" + exe + "'\nargs = []\nstartup_timeout_sec = 20\ntool_timeout_sec = 150\n")
    groups, count = [], 0
    for source in sorted((destination / "src/ArcGISProMCP.Server").glob("ArcGis*Tools.cs")):
        names = re.findall(r'McpServerTool\(Name\s*=\s*"([a-z0-9_]+)"', source.read_text(encoding="utf-8"))
        count += len(names)
        groups.append("## " + source.stem + "\n\n" + ", ".join("`" + n + "`" for n in names) + "\n")
    if count != 78:
        raise ValueError("Unexpected tool count: " + str(count))
    write("docs/TOOLS.md", "# 工具清单 / Tool catalog\n\nGenerated from tool registrations: 78. Parameters and returned schema are discovered through MCP tools/list; descriptions are not an authorization grant. 参数以实际tools/list为准。\n\n" + "\n".join(groups))
    write(".github/workflows/ci.yml", "name: Synthetic MCP checks\non:\n  push:\n    branches: [main]\n  pull_request:\npermissions:\n  contents: read\njobs:\n  synthetic:\n    runs-on: windows-latest\n    steps:\n      - uses: actions/checkout@v4\n      - uses: actions/setup-dotnet@v4\n        with:\n          dotnet-version: '10.0.x'\n      - name: Build SDK-free synthetic test host\n        run: dotnet build tests/McpIntegration/McpIntegration.csproj -c Release\n      - name: Run synthetic MCP checks (no Esri host)\n        run: ./artifacts/bin/McpIntegration/Release/net10.0-windows/McpIntegration.exe ./artifacts/bin/ArcGISProMCP.Server/Release/net10.0-windows/ArcGISProMCP.Server.dll\n")
    # Public source must not contain a developer's real data path or credentials.
    forbidden = [str(Path.home()), *live_paths]
    secrets = re.compile(r"(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)")
    files = []
    for file in sorted(destination.rglob("*")):
        if not file.is_file():
            continue
        if file.suffix not in {".png"}:
            text = file.read_text(encoding="utf-8-sig")
            if any(p in text for p in forbidden) or secrets.search(text):
                raise ValueError("Public source audit needs review: " + str(file.relative_to(destination)))
        files.append({"path": file.relative_to(destination).as_posix(), "sha256": digest(file), "bytes": file.stat().st_size})
    manifest = destination.parent / (destination.name + "-source-manifest.json")
    manifest.write_text(json.dumps({"files": files, "sourceOnly": True, "externalGrants": 0, "autonomousConsent": False, "automaticBackupDeletion": False, "toolCount": count}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Exported {len(files)} reviewed source/resource files; {count} tools; no live consent/config copied. Manifest: {manifest}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--destination", type=Path, required=True)
    export(parser.parse_args().destination)
