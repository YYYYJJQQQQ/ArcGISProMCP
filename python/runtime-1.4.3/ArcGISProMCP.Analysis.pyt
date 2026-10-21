"""Fixed ArcGIS Pro GP entry point, including filtered GeoJSON; no arbitrary script execution."""
import importlib.util
from pathlib import Path
import sys
import arcpy

sys.dont_write_bytecode = True
_module_path = Path(__file__).with_name("arcgispro_mcp_analysis.py")
_spec = importlib.util.spec_from_file_location("arcgispro_mcp_analysis_143", _module_path)
_worker = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_worker)


class Toolbox:
    def __init__(self):
        self.label = "ArcGIS Pro MCP Analysis"
        self.alias = "arcgispro_mcp_analysis_143"
        self.tools = [RunAnalysis]


class RunAnalysis:
    def __init__(self):
        self.label = "Run validated MCP analysis"
        self.description = "Execute a fixed official ArcPy operation using a validated workspace job."
        self.canRunInBackground = True

    def getParameterInfo(self):
        return [
            arcpy.Parameter(displayName="Request JSON", name="request_file", datatype="DEFile", parameterType="Required", direction="Input"),
            arcpy.Parameter(displayName="Input layers", name="input_layers", datatype="GPFeatureLayer", parameterType="Optional", direction="Input", multiValue=True),
            arcpy.Parameter(displayName="Result JSON", name="result_file", datatype="DEFile", parameterType="Required", direction="Output"),
        ]

    def isLicensed(self):
        return True  # Individual Esri tools enforce their actual license requirements.

    def execute(self, parameters, messages):
        inputs = list(parameters[1].values or [])
        result = _worker.run_job(parameters[0].valueAsText, inputs, parameters[2].valueAsText)
        if result["status"] != "succeeded":
            arcpy.AddError(result["error"]["message"])
            raise arcpy.ExecuteError
