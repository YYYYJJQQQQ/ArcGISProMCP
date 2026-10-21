"""Independent ArcPy-side authorization check; local config is never writable through MCP."""
import json
import os
from pathlib import Path, PureWindowsPath


def canonical(value):
    text = str(value)
    path = PureWindowsPath(text)
    if (not path.is_absolute() or text.startswith("\\") or len(text) > 4096
            or any(ord(c) < 32 for c in text) or ":" in text[2:]):
        raise PermissionError("Absolute local path required; UNC, device and ADS paths are denied.")
    for part in text.replace("/", "\\")[3:].split("\\"):
        stem = part.split(".")[0].upper()
        if (part in {".", ".."} or part.endswith((".", " ")) or any(c in part for c in '<>"|?*')
                or stem in {"CON", "PRN", "AUX", "NUL"} or (len(stem) == 4 and stem[:3] in {"COM", "LPT"} and stem[3].isdigit())):
            raise PermissionError("Traversal and Windows filename aliases are denied.")
    result = Path(os.path.abspath(text))
    check_ancestors(result)
    return result


def check_ancestors(path):
    for item in (path, *path.parents):
        try:
            if getattr(item.lstat(), "st_file_attributes", 0) & 0x400:
                raise PermissionError("Reparse points are denied.")
        except (FileNotFoundError, NotADirectoryError):
            pass


def within(path, directory):
    return os.path.normcase(str(path)) == os.path.normcase(str(directory)) or os.path.normcase(str(path)).startswith(os.path.normcase(str(directory)).rstrip("\\") + "\\")


def require(root, value, permission, use):
    path = canonical(value)
    protected = [root / name for name in ("src", "config", ".codex", "python", "ArcGISProMCP.AddIn", "artifacts", "docs", "tests", "scripts", "tools", ".git", ".vs")]
    protected += [Path(os.environ.get("WINDIR", "C:/Windows")), Path(os.environ.get("ProgramFiles", "C:/Program Files")),
                  Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")), Path("F:/ArcGISpro"), Path(os.environ.get("ProgramData", "C:/ProgramData"))]
    protected += [Path.home() / name for name in (".codex", ".ssh", ".aws", ".azure", ".kube", ".gnupg", "AppData")]
    if any(within(path, item) for item in protected):
        raise PermissionError("Sensitive directory cannot be a GIS working directory.")
    tests = root / "TestProjects"
    grants = [
        {"path": str(tests), "permissions": ["read", "modify"], "uses": ["project"]},
        {"path": str(tests), "permissions": ["read"], "uses": ["data"]},
        {"path": str(tests / "AnalysisOutputs"), "permissions": ["read", "create"], "uses": ["data", "analysis"]},
        {"path": str(tests / "CartographyOutputs"), "permissions": ["read", "create"], "uses": ["cartography"]},
        {"path": str(tests / "MapTemplates"), "permissions": ["read", "create"], "uses": ["template"]},
        {"path": str(tests / "ProjectCopies"), "permissions": ["read", "create", "modify"], "uses": ["project"]},
    ]
    config = root / "config" / "workspace-access.json"
    check_ancestors(config)
    if config.exists():
        if config.stat().st_size > 131072:
            raise PermissionError("Authorization configuration exceeds 128 KiB.")
        data = json.loads(config.read_text(encoding="utf-8-sig"))
        if data.get("schemaVersion") != 1 or not isinstance(data.get("grants"), list) or len(data["grants"]) > 64:
            raise PermissionError("Invalid authorization schema.")
        for grant in data["grants"]:
            directory = canonical(grant["path"])
            if directory == Path(directory.anchor) or any(within(directory, item) for item in protected):
                raise PermissionError("Unsafe authorization root.")
            if (not grant.get("permissions") or not grant.get("uses") or
                    set(grant["permissions"]) - {"read", "create", "modify"} or
                    set(grant["uses"]) - {"project", "data", "analysis", "cartography", "template"}):
                raise PermissionError("Invalid authorization permissions.")
        grants += data["grants"]
    matches = [g for g in grants if use in g["uses"] and within(path, canonical(g["path"]))]
    specificity = max((len(str(canonical(g["path"]))) for g in matches), default=-1)
    if not any(len(str(canonical(g["path"]))) == specificity and permission in g["permissions"] for g in matches):
        raise PermissionError("Path is not authorized for " + use + "/" + permission)
    return path
