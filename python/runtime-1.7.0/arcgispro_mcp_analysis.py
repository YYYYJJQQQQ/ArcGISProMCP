"""ArcPy implementation shared by the Pro toolbox and real-data acceptance tests.

All algorithms are Esri tools. Inputs are GP feature-layer parameters, preserving
definition queries and existing selections. Only select_location changes selection.
"""
import datetime
import itertools
import json
import math
from pathlib import Path
import re
import sys
import traceback
import arcpy
import importlib.util

_access_spec = importlib.util.spec_from_file_location("arcgispro_mcp_access_170", Path(__file__).with_name("workspace_access.py"))
_access = importlib.util.module_from_spec(_access_spec)
_access_spec.loader.exec_module(_access)

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
RUNTIME_VERSION = "1.7.0"
OPERATIONS = {"environment", "buffer", "clip", "intersect", "union", "dissolve", "spatial_join", "select_location", "project", "geometry", "distance", "query", "convert"}


class AnalysisError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def inside(path, directory):
    _access.check_ancestors(Path(path))
    resolved = Path(path).resolve()
    if not resolved.is_relative_to(directory.resolve()):
        raise AnalysisError("output_scope_denied", "Path is outside the allowed project directory.")
    return resolved


def write_json(path, value):
    with path.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(value, ensure_ascii=False, indent=2, default=json_value, allow_nan=False))
        stream.flush()
        import os
        os.fsync(stream.fileno())


def json_value(value):
    if isinstance(value, (datetime.datetime, datetime.date, datetime.time)):
        return value.isoformat()
    if isinstance(value, bytes):
        return "<binary omitted>"
    return str(value)


def check_cancel():
    if arcpy.env.isCancelled:
        raise AnalysisError("analysis_cancelled", "Analysis cancelled; partial outputs may remain.")


def ensure_output(request):
    value = request.get("outputPath")
    if value is None:
        return None
    try:
        path = _access.require(ROOT, value, "create", "analysis")
    except (PermissionError, ValueError, KeyError, TypeError) as ex:
        raise AnalysisError("output_scope_denied", str(ex)) from ex
    if path == (ROOT / "TestProjects" / "AnalysisOutputs").resolve():
        raise AnalysisError("output_scope_denied", "Specify a new output dataset, not its root directory.")
    if path.exists() or arcpy.Exists(str(path)):
        raise AnalysisError("output_exists", "Output exists; overwriting is never enabled.")
    # Reject existing Shapefile sidecars too, rather than leaving a mixed old/new dataset.
    if path.suffix.lower() == ".shp" and any(path.with_suffix(ext).exists() for ext in (".dbf", ".shx", ".prj", ".cpg")):
        raise AnalysisError("output_exists", "A Shapefile component already exists.")
    parts = list(path.parts)
    gdb_index = next((i for i, part in enumerate(parts) if part.lower().endswith(".gdb")), None)
    if gdb_index is not None:
        if gdb_index != len(parts) - 2 or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,63}", path.name):
            raise AnalysisError("invalid_output", "Use a valid feature/table name directly inside a file geodatabase.")
        gdb = Path(*parts[:gdb_index + 1])
        gdb.parent.mkdir(parents=True, exist_ok=True)
        if not gdb.exists():
            arcpy.management.CreateFileGDB(str(gdb.parent), gdb.name)
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
    return str(path)


def require_fields(layer, names):
    available = {field.name.lower(): field for field in arcpy.ListFields(layer)}
    result = []
    for name in names:
        if not isinstance(name, str) or name.lower() not in available:
            raise AnalysisError("field_not_found", "Unknown field: " + str(name))
        field = available[name.lower()]
        if field.type in {"Geometry", "Blob", "Raster"}:
            raise AnalysisError("unsupported_field", "Geometry, BLOB and Raster fields are not attribute query fields.")
        result.append(field.name)
    return result


def spatial_checks(request, inputs):
    descriptions = [arcpy.Describe(layer) for layer in inputs]
    spatial = [item.spatialReference for item in descriptions]
    for sr in spatial:
        if sr.name == "Unknown" or not sr.exportToString():
            raise AnalysisError("unknown_spatial_reference", "All inputs need a defined spatial reference.")
    # GeoJSON is always WGS84, including its datum-transformation validation.
    target_wkid = 4326 if request["operation"] == "convert" and request.get("format") == "GEOJSON" else request.get("outputWkid")
    target = arcpy.SpatialReference(target_wkid) if target_wkid else (spatial[0] if spatial else None)
    if target is not None and target.name == "Unknown":
        raise AnalysisError("invalid_spatial_reference", "Unknown output WKID.")
    transform = request.get("geographicTransformation")
    for sr in spatial:
        if target is not None and sr.GCS.exportToString() != target.GCS.exportToString():
            choices = arcpy.ListTransformations(sr, target)
            if not transform or transform not in choices:
                raise AnalysisError("transformation_required", "Different geographic datums require an explicit valid geographicTransformation. Candidates: " + ", ".join(choices[:10]))
    if request.get("method", "GEODESIC") == "PLANAR" and request["operation"] in {"buffer", "geometry", "distance"}:
        if any(sr.type != "Projected" for sr in spatial):
            raise AnalysisError("projected_reference_required", "PLANAR measurement requires projected inputs; use GEODESIC or Project first.")
    operation = request["operation"]
    shapes = [item.shapeType for item in descriptions]
    if operation == "union" and any(shape != "Polygon" for shape in shapes):
        raise AnalysisError("geometry_type_mismatch", "Union requires polygon inputs.")
    if operation == "clip" and shapes[1] != "Polygon":
        raise AnalysisError("geometry_type_mismatch", "The clip layer must be polygon.")
    return descriptions, target


def inspect_output(dataset):
    description = arcpy.Describe(dataset)
    fields = [{"name": f.name, "type": f.type} for f in arcpy.ListFields(dataset)]
    result = {"path": str(dataset), "count": int(arcpy.management.GetCount(dataset)[0]), "fields": fields}
    if hasattr(description, "spatialReference"):
        sr = description.spatialReference
        result.update({"geometryType": getattr(description, "shapeType", None), "spatialReference": {"wkid": sr.factoryCode, "name": sr.name}})
    return result


def conversion_preflight(request, source):
    fields = [f for f in arcpy.ListFields(source) if f.type not in {"OID", "Geometry"}]
    by_name = {f.name.casefold(): f for f in fields}
    overrides = {item["source"].casefold(): item for item in (request.get("fieldMappings") or [])}
    if any(name not in by_name for name in overrides):
        raise AnalysisError("field_not_found", "Field mapping references a missing or system field.")
    issues, warnings = [], []
    if request.get("format") == "SHAPEFILE":
        for field in fields:
            mapping = overrides.get(field.name.casefold())
            if not mapping and field.type in {"BigInteger", "Blob", "Raster", "GUID", "GlobalID", "DateOnly", "TimeOnly", "TimestampOffset"}:
                issues.append({"field": field.name, "type": field.type, "reason": "Unsupported Shapefile type; use FileGDB or explicit fieldMappings."})
            if not mapping and len(field.name.encode("utf-8")) > 10:
                warnings.append("Shapefile may rename field: " + field.name)
        names = [(overrides.get(f.name.casefold()) or {}).get("name", f.name) for f in fields]
        if len({n.casefold() for n in names}) != len(names):
            issues.append({"reason": "Mapped output names collide with preserved fields."})
    if overrides:
        checked = list(overrides)
        with arcpy.da.SearchCursor(source, [by_name[n].name for n in checked], where_clause=request.get("whereClause")) as cursor:
            for row_index, row in enumerate(cursor):
                if row_index % 1024 == 0:
                    check_cancel()
                for name, value in zip(checked, row):
                    mapping = overrides[name]
                    kind = mapping["type"]
                    invalid = value is None
                    if value is not None:
                        if kind in {"SHORT", "LONG"}:
                            lower, upper = (-32768, 32767) if kind == "SHORT" else (-2147483648, 2147483647)
                            invalid = not isinstance(value, (int, float)) or not math.isfinite(value) or int(value) != value or not lower <= value <= upper
                        elif kind == "DOUBLE":
                            invalid = not isinstance(value, (int, float)) or not math.isfinite(value) or isinstance(value, int) and abs(value) > 2**53
                        elif kind == "TEXT":
                            invalid = not isinstance(value, str) or len(value.encode("utf-8")) > mapping.get("length", 254)
                        elif kind == "DATE":
                            invalid = not isinstance(value, (datetime.date, datetime.datetime)) or isinstance(value, datetime.datetime) and value.time() != datetime.time()
                        else:
                            invalid = True
                    if invalid:
                        issues.append({"field": by_name[name].name, "reason": "Null, range, precision, time or truncation loss in explicit mapping", "row": row_index})
                        break
                if issues:
                    break
    return {"canExport": not issues, "format": request.get("format"), "issues": issues, "warnings": warnings,
            "fields": [{"name": f.name, "type": f.type} for f in fields], "fieldMappings": list(overrides.values()),
            "unmappedFieldsPreserved": True, "sourceDataModified": False, "outputWritten": False}


def conversion_field_mappings(request, source):
    overrides = request.get("fieldMappings") or []
    if not overrides:
        return None
    types = {"TEXT": "String", "SHORT": "SmallInteger", "LONG": "Integer", "DOUBLE": "Double", "DATE": "Date"}
    mappings = arcpy.FieldMappings()
    mappings.addTable(source)
    for item in overrides:
        index = mappings.findFieldMapIndex(item["source"])
        if index < 0:
            raise AnalysisError("field_not_found", "Field mapping source not found.")
        field_map = mappings.getFieldMap(index)
        field = field_map.outputField
        field.name = item["name"]
        field.type = types[item["type"]]
        if item.get("length") is not None:
            field.length = item["length"]
        field_map.outputField = field
        mappings.replaceFieldMap(index, field_map)
    return mappings


def execute(request, inputs, scratch):
    operation = request["operation"]
    if operation not in OPERATIONS:
        raise AnalysisError("invalid_arguments", "Unknown operation.")
    minimum = 0 if operation == "environment" else 2 if operation in {"clip", "intersect", "union", "spatial_join", "select_location", "distance"} else 1
    maximum = 8 if operation in {"intersect", "union"} else minimum
    if not minimum <= len(inputs) <= maximum:
        raise AnalysisError("invalid_arguments", "Incorrect number of input layers.")
    if operation == "environment":
        return {"arcgisVersion": arcpy.GetInstallInfo().get("Version"), "pythonVersion": sys.version, "toolboxVersion": RUNTIME_VERSION, "product": arcpy.ProductInfo(), "operations": sorted(OPERATIONS)}
    for layer in inputs:
        description = arcpy.Describe(layer)
        catalog = getattr(description, "catalogPath", "")
        if catalog and not catalog.lower().startswith(("http://", "https://")):
            try:
                _access.require(ROOT, catalog, "read", "data")
            except (PermissionError, ValueError) as ex:
                raise AnalysisError("source_scope_denied", str(ex)) from ex
    descriptions, target = spatial_checks(request, inputs)
    if operation == "convert":
        preflight = conversion_preflight(request, inputs[0])
        if request.get("preflightOnly"):
            return preflight
        if not preflight["canExport"]:
            raise AnalysisError("field_type_incompatible", json.dumps(preflight, ensure_ascii=False))
    output = ensure_output(request)
    required_output = operation not in {"query", "select_location"}
    if required_output and not output:
        raise AnalysisError("invalid_arguments", "This operation requires an output path.")
    if required_output and operation != "convert":
        allowed = (".dbf",) if operation == "distance" else (".shp",)
        if not any(part.lower().endswith(".gdb") for part in Path(output).parts[:-1]) and Path(output).suffix.lower() not in allowed:
            raise AnalysisError("invalid_output", "Use a new file-geodatabase dataset or the matching Shapefile/DBF format.")
    distance = request.get("distance")
    if distance is not None and (not isinstance(distance, (int, float)) or not math.isfinite(distance) or distance <= 0):
        raise AnalysisError("invalid_arguments", "Distance must be a finite positive number.")
    unit = request.get("distanceUnit", "Meters")
    if unit not in {"Meters", "Kilometers", "Feet", "Miles"}:
        raise AnalysisError("invalid_arguments", "Unsupported distance unit.")
    linear = f"{distance} {unit}" if distance is not None else None
    fields = require_fields(inputs[0], request.get("fields", []))
    method = request.get("method", "GEODESIC")
    if method not in {"PLANAR", "GEODESIC"}:
        raise AnalysisError("invalid_arguments", "Unsupported measurement method.")
    check_cancel()
    with arcpy.EnvManager(overwriteOutput=False, workspace=str(scratch), scratchWorkspace=str(scratch),
                          outputCoordinateSystem=target, geographicTransformations=request.get("geographicTransformation") or "", addOutputsToMap=False):
        if operation == "buffer":
            if not linear:
                raise AnalysisError("invalid_arguments", "Buffer requires a distance.")
            arcpy.analysis.Buffer(inputs[0], output, linear, "FULL", "ROUND", request.get("dissolveOption", "NONE"), fields, method)
        elif operation == "clip":
            arcpy.analysis.Clip(inputs[0], inputs[1], output)
        elif operation == "intersect":
            arcpy.analysis.Intersect(inputs, output, request.get("joinAttributes", "ALL"), None, request.get("outputType", "INPUT"))
        elif operation == "union":
            arcpy.analysis.Union(inputs, output, request.get("joinAttributes", "ALL"))
        elif operation == "dissolve":
            stats = [value.split(":") for value in request.get("statistics", [])]
            require_fields(inputs[0], [item[0] for item in stats])
            arcpy.management.Dissolve(inputs[0], output, fields, stats, "MULTI_PART" if request.get("multiPart", True) else "SINGLE_PART")
        elif operation == "spatial_join":
            arcpy.analysis.SpatialJoin(inputs[0], inputs[1], output, request.get("joinOperation", "JOIN_ONE_TO_ONE"), "KEEP_ALL" if request.get("keepAll", True) else "KEEP_COMMON", None, request.get("relation", "INTERSECT"), linear)
        elif operation == "select_location":
            result = arcpy.management.SelectLayerByLocation(inputs[0], request.get("relation", "INTERSECT"), inputs[1], linear, request.get("selectionType", "NEW_SELECTION"), "INVERT" if request.get("invert", False) else "NOT_INVERT")
            selected = int(result[2])
            if output:
                if Path(output).suffix.lower() != ".shp" and ".gdb" not in str(output).lower():
                    raise AnalysisError("invalid_output", "Selection export requires a feature-class output.")
                arcpy.management.CopyFeatures(inputs[0], output)
            return {"selectedCount": selected, "selectionType": request.get("selectionType", "NEW_SELECTION"), "output": inspect_output(output) if output else None}
        elif operation == "project":
            if request.get("outputWkid", 0) <= 0:
                raise AnalysisError("invalid_arguments", "Project requires outputWkid.")
            arcpy.management.Project(inputs[0], output, target, request.get("geographicTransformation"))
        elif operation == "geometry":
            shape = descriptions[0].shapeType
            requested = request.get("geometryProperties", ["area", "length"])
            if shape not in {"Polygon", "Polyline"} or ("area" in requested and shape != "Polygon"):
                raise AnalysisError("geometry_type_mismatch", "Area requires polygons; length requires lines or polygons.")
            arcpy.management.CopyFeatures(inputs[0], output)
            suffix = "_GEODESIC" if method == "GEODESIC" else ""
            properties = []
            if "area" in requested:
                properties.append(["MCP_AREA", "AREA" + suffix])
            if "length" in requested:
                properties.append(["MCP_LEN", ("PERIMETER_LENGTH" if shape == "Polygon" else "LENGTH") + suffix])
            arcpy.management.CalculateGeometryAttributes(output, properties, unit.upper(), request.get("areaUnit", "SQUARE_METERS"))
        elif operation == "distance":
            arcpy.analysis.GenerateNearTable(inputs[0], inputs[1], output, linear, "LOCATION", "NO_ANGLE", "ALL", request.get("closestCount", 1), method, unit)
        elif operation == "query":
            if not fields:
                fields = [f.name for f in arcpy.ListFields(inputs[0]) if f.type not in {"Geometry", "Blob", "Raster"}][:30]
            where = request.get("whereClause")
            if where and (len(where) > 2048 or any(item in where for item in (";", "--", "/*", "*/"))):
                raise AnalysisError("invalid_arguments", "Only a single WHERE predicate is accepted.")
            limit, offset = request.get("limit", 100), request.get("offset", 0)
            if not 1 <= limit <= 200 or not 0 <= offset <= 1000000:
                raise AnalysisError("invalid_arguments", "Invalid query pagination.")
            bounded_rows, byte_count, truncated = [], 0, False
            has_more = False
            with arcpy.da.SearchCursor(inputs[0], fields, where_clause=where) as cursor:
                for skipped in range(offset):
                    if skipped % 256 == 0:
                        check_cancel()
                    if next(cursor, None) is None:
                        break
                for row in itertools.islice(cursor, limit + 1):
                    check_cancel()
                    if len(bounded_rows) >= limit:
                        has_more = True
                        break
                    bounded = []
                    for cell in row:
                        if isinstance(cell, float) and not math.isfinite(cell):
                            cell = None
                        if isinstance(cell, str) and len(cell) > 512:
                            cell = cell[:512]; truncated = True
                        bounded.append(cell)
                    size = len(json.dumps(bounded, default=json_value, ensure_ascii=False).encode("utf-8"))
                    if byte_count + size > 128000:
                        has_more = True
                        break
                    bounded_rows.append(bounded); byte_count += size
            data = {"fields": fields, "rows": bounded_rows, "offset": offset, "limit": limit, "hasMore": has_more,
                    "returnedCount": len(bounded_rows), "nextOffset": offset + len(bounded_rows), "truncatedValues": truncated,
                    "ordering": "provider order; rerun pagination if the input changes"}
            if output:
                if Path(output).suffix.lower() == ".json":
                    write_json(Path(output), data)
                elif Path(output).suffix.lower() == ".csv":
                    import csv
                    with open(output, "x", newline="", encoding="utf-8-sig") as stream:
                        writer = csv.writer(stream); writer.writerow(fields); writer.writerows(data["rows"])
                else:
                    raise AnalysisError("invalid_output", "Attribute results support new .json or .csv files.")
            return data
        elif operation == "convert":
            format_name = request.get("format", "FEATURE_CLASS")
            if format_name == "GEOJSON" and Path(output).suffix.lower() in {".json", ".geojson"}:
                temporary = None
                try:
                    source = inputs[0]
                    if request.get("whereClause"):
                        # MakeFeatureLayer preserves the input selection and combines definition queries with AND.
                        temporary = arcpy.management.MakeFeatureLayer(source, "mcp_geojson_" + request["operationId"], request["whereClause"])[0]
                        source = temporary
                    arcpy.conversion.FeaturesToJSON(source, output, geoJSON="GEOJSON", outputToWGS84="WGS84")
                    return {"path": output, "format": "GEOJSON", "outputWkid": 4326, "sourceCount": int(arcpy.management.GetCount(source)[0])}
                finally:
                    if temporary is not None:
                        # This is only our temporary GP layer object, never its source dataset or a map layer.
                        try:
                            arcpy.management.Delete(temporary)
                        except Exception:
                            arcpy.AddWarning("Could not release the temporary GeoJSON filter layer; source data is unchanged.")
            if format_name == "GEOPACKAGE" and Path(output).suffix.lower() == ".gpkg":
                arcpy.management.CreateSQLiteDatabase(output, "GEOPACKAGE")
                dataset = output + "/features"
                arcpy.conversion.ExportFeatures(inputs[0], dataset, request.get("whereClause"))
                return {"path": output, "format": "GEOPACKAGE", "dataset": inspect_output(dataset)}
            if format_name == "SHAPEFILE" and Path(output).suffix.lower() != ".shp" or format_name == "FEATURE_CLASS" and not any(p.lower().endswith(".gdb") for p in Path(output).parts[:-1]):
                raise AnalysisError("invalid_output", "Output path does not match format.")
            if format_name not in {"SHAPEFILE", "FEATURE_CLASS"}:
                raise AnalysisError("invalid_arguments", "Unsupported conversion format/path.")
            arcpy.conversion.ExportFeatures(inputs[0], output, request.get("whereClause"), field_mapping=conversion_field_mappings(request, inputs[0]))
    # Do not turn an already completed GP write into a cancellation after commit.
    result = inspect_output(output)
    if operation == "convert" and request.get("format") == "SHAPEFILE":
        result["formatLimitations"] = ["Shapefile field names are limited to 10 characters; inspect returned fields for ArcPy mappings.",
                                       "Shapefile cannot faithfully represent all null/date/64-bit field values. Use FileGDB or GeoPackage when exact schema/null preservation is required."]
    if operation == "geometry":
        result["measurements"] = {}
        for field, _ in properties:
            with arcpy.da.SearchCursor(output, [field]) as cursor:
                result["measurements"][field] = [row[0] for row in itertools.islice(cursor, 20)]
    return result


def validated_request_path(request_file):
    try:
        return _validated_request_path(request_file)
    except (PermissionError, ValueError, OSError) as ex:
        raise AnalysisError("storage_scope_denied", str(ex)) from ex


def _validated_request_path(request_file):
    request_path = _access.canonical(request_file)
    job_id = request_path.parent.name
    if request_path.name != "request.json" or not re.fullmatch(r"[0-9a-fA-F]{32}", job_id):
        raise AnalysisError("invalid_job_path", "Expected <32-hex job>/request.json.")
    default = ROOT / "artifacts" / "logs" / "analysis"
    if request_path.parent.parent == default:
        return request_path
    # The host registers its chosen log location before scheduling GP. This index
    # remains authoritative if configuration changes while a queued job waits.
    index = ROOT / "artifacts" / "logs" / "history-index" / "analysis" / (job_id + ".json")
    _access.check_ancestors(index)
    if not index.exists() or index.stat().st_size > 8192:
        raise AnalysisError("invalid_job_path", "Custom log path requires a bounded host-owned history index.")
    location = json.loads(index.read_text(encoding="utf-8-sig"))
    if location.get("schemaVersion") != 1 or location.get("kind") != "analysis" or location.get("id") != job_id:
        raise AnalysisError("invalid_job_path", "History index identity mismatch.")
    directory = _access.canonical(location.get("root", ""))
    _access.require(ROOT, directory, "read", "project")
    _access.require(ROOT, directory, "create", "project")
    if (directory.name != "analysis" or request_path.parent.parent != directory or
            any(part.lower().endswith((".gdb", ".sde", ".gpkg", ".aprx", ".shp")) for part in directory.parts)):
        raise AnalysisError("invalid_job_path", "Request and authorized storage location differ.")
    return request_path


def run_job(request_file, inputs, result_file):
    request_path = validated_request_path(request_file)
    result_path = inside(result_file, request_path.parent)
    if result_path.name != "result.json" or result_path.parent != request_path.parent:
        raise AnalysisError("invalid_job_path", "The result must be result.json in its job directory.")
    if result_path.exists():
        raise AnalysisError("operation_history_exists", "This job already has a result; do not overwrite its history.")
    if request_path.stat().st_size > 65536:
        raise AnalysisError("invalid_job_path", "Request exceeds 64 KiB.")
    request = json.loads(request_path.read_text(encoding="utf-8-sig"))
    if request.get("operationId") != request_path.parent.name or not re.fullmatch(r"[0-9a-fA-F]{32}", request_path.parent.name):
        raise AnalysisError("invalid_job_path", "Job ID does not match the request directory.")
    try:
        project = _access.require(ROOT, request["projectPath"], "modify" if request["operation"] == "select_location" else "read", "project")
    except (PermissionError, ValueError) as ex:
        raise AnalysisError("write_scope_denied", str(ex)) from ex
    if project.suffix.lower() != ".aprx":
        raise AnalysisError("invalid_project", "A test .aprx is required.")
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    outcome = {"jobId": request["operationId"], "operation": request["operation"], "startedAtUtc": started, "engine": "Esri ArcPy 3.7 via official GP tools", "sourceDataModified": False, "automaticProjectSave": False}
    try:
        scratch_dir = ROOT / "artifacts" / "cache" / "analysis" / request["operationId"]
        scratch_dir.mkdir(parents=True, exist_ok=True)
        scratch = scratch_dir / "scratch.gdb"
        if not scratch.exists():
            arcpy.management.CreateFileGDB(str(scratch_dir), scratch.name)
        with arcpy.EnvManager(autoCancelling=False):
            data = execute(request, inputs, scratch)
        outcome.update(status="succeeded", data=data)
    except (KeyboardInterrupt, SystemExit):
        outcome.update(status="cancelled", error={"code": "analysis_cancelled", "message": "Cancelled; partial outputs may remain."})
    except Exception as ex:
        code = ex.code if isinstance(ex, AnalysisError) else "geoprocessing_failed" if isinstance(ex, arcpy.ExecuteError) else "analysis_error"
        outcome.update(status="cancelled" if arcpy.env.isCancelled else "failed", error={"code": code, "message": str(ex), "gpMessages": arcpy.GetMessages(2)[-12000:]}, diagnostic=traceback.format_exc()[-12000:])
    outcome["completedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    write_json(result_path, outcome)
    return outcome
