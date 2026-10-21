using System.Text.Json;
using ModelContextProtocol.Client;

internal static class Phase3LiveChecks
{
    public static async Task RunAsync(McpClient client, string root, string fixtureDirectory, Dictionary<string,object?> report, CancellationToken token, string? resumeReport = null)
    {
        string fixtures = Path.GetFullPath(fixtureDirectory);
        if (!fixtures.StartsWith(Path.Combine(root,"TestProjects","Phase3Fixtures") + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use phase-three fixtures inside this project.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures,"manifest.json"),token));
        var calls=new List<object>(); var jobs=new Dictionary<string,JsonElement>(); var layers=new Dictionary<string,string>(); var checks=new List<object>();
        JsonElement? previous = null;
        if (resumeReport != null)
        {
            string resumePath=Path.GetFullPath(resumeReport);
            Require(resumePath.StartsWith(Path.Combine(root,"artifacts","logs","live-mcp")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Resume reports must be inside this project's live MCP logs.");
            using var saved=JsonDocument.Parse(await File.ReadAllTextAsync(resumePath,token));
            previous=saved.RootElement.Clone();
            var prior=previous.Value.GetProperty("phase3Live");
            Require(string.Equals(Path.GetFullPath(prior.GetProperty("fixtureDirectory").GetString()!),fixtures,StringComparison.OrdinalIgnoreCase),"Resume fixtures do not match the prior report.");
            foreach(var layer in prior.GetProperty("layers").EnumerateObject())layers[layer.Name]=layer.Value.GetString()!;
            foreach(var job in prior.GetProperty("jobs").EnumerateObject())
                if(job.Value.GetProperty("state").GetString()=="succeeded")jobs[job.Name]=job.Value.Clone();
            report["phase3ResumedFrom"]=resumePath;
        }
        report["phase3Live"]=new { fixtureDirectory=fixtures,nativeCodexTools=false,transport="official C# MCP / stdio / live Pro SDK / Python toolbox / ArcPy",calls,jobs,layers,checks };
        async Task<JsonElement> Call(string tool,Dictionary<string,object?> args,string? expectedError=null)
        {
            var response=await client.CallToolAsync(tool,args,cancellationToken:token);
            var data=response.StructuredContent??throw new IOException("Missing tool result.");
            if(tool!="get_analysis_job" || response.IsError==true || data.GetProperty("state").GetString() is "succeeded" or "failed" or "cancelled")calls.Add(new{tool,input=args,result=data,isError=response.IsError});
            if(expectedError==null && response.IsError==true)throw new IOException(tool+": "+data.GetRawText());
            if(expectedError!=null && (response.IsError!=true || data.GetProperty("error").GetProperty("code").GetString()!=expectedError))throw new IOException("Expected "+expectedError+": "+data.GetRawText());
            return data;
        }
        void Require(bool value,string message){if(!value)throw new IOException(message);}
        var project=await Call("get_current_project",[]);
        string path=Path.Combine(root,"TestProjects","MCPTest","MCPTest.aprx");
        Require(string.Equals(project.GetProperty("path").GetString(),path,StringComparison.OrdinalIgnoreCase),"Open MCPTest before live acceptance.");
        var maps=await Call("list_maps",[]);string map=maps.GetProperty("items").EnumerateArray().Single(m=>m.GetProperty("isActive").GetBoolean()).GetProperty("uri").GetString()!;
        var currentLayers=await Call("list_layers",new(){["mapUri"]=map});
        var baseline=previous?.GetProperty("phase3Baseline")??currentLayers;report["phase3Baseline"]=baseline;
        Require(baseline.GetProperty("mapUri").GetString()==map,"Resume map does not match the active map.");
        if(previous==null)
            Require(!currentLayers.GetProperty("items").EnumerateArray().Any(l=>l.GetProperty("name").GetString()!.StartsWith("MCP_P3_",StringComparison.Ordinal)),"Existing phase-three layers found; inspect earlier reports or use Phase3ResumeReport instead of adding duplicates.");
        else
            foreach(string uri in layers.Values)
                Require(currentLayers.GetProperty("items").EnumerateArray().Any(l=>l.GetProperty("uri").GetString()==uri && l.GetProperty("name").GetString()!.StartsWith("MCP_P3_",StringComparison.Ordinal)),"A resumed test layer is missing; stop and inspect the map.");
        string output=Path.Combine(root,"TestProjects","AnalysisOutputs","live-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        report["phase3OutputDirectory"]=output;
        Dictionary<string,object?> Input()=>new(){["projectPath"]=path,["mapUri"]=map,["operationId"]=Guid.NewGuid().ToString("N")};
        async Task<JsonElement> Wait(string id)
        {
            for(int i=0;i<480;i++)
            {
                var current=await Call("get_analysis_job",new(){["jobId"]=id});
                if(current.GetProperty("state").GetString() is "succeeded" or "failed" or "cancelled")return current;
                await Task.Delay(500,token);
            }
            throw new IOException("Host job exceeded four minute acceptance budget; inspect or cancel it explicitly: "+id);
        }
        async Task<JsonElement> Analyze(string key,string tool,Dictionary<string,object?> input,bool success=true)
        {
            if(success && jobs.TryGetValue(key,out var completed))
            {
                checks.Add(new{name=key,passed=true,reusedFromPriorReport=true});
                Console.WriteLine("PASS live phase3 "+key+" (prior completed job)");
                return completed;
            }
            var start=await Call(tool,input);string id=start.GetProperty("jobId").GetString()!;
            var final=await Wait(id);jobs[key]=final;
            if(success)Require(final.GetProperty("state").GetString()=="succeeded",key+": "+final.GetRawText());
            checks.Add(new{name=key,passed=success?final.GetProperty("state").GetString()=="succeeded":true});Console.WriteLine("PASS live phase3 "+key);
            return final;
        }
        var environment=await Analyze("environment","analysis_environment",Input());
        Require(environment.GetProperty("result").GetProperty("data").GetProperty("arcgisVersion").GetString()=="3.7","Unexpected embedded ArcPy runtime.");
        foreach(string name in new[]{"polygons","mask","points","near","lines"})
        {
            if(layers.ContainsKey(name))continue;
            string source=Path.GetFullPath(manifest.RootElement.GetProperty("sources").GetProperty(name).GetString()!);
            Require(Path.GetFullPath(source).StartsWith(fixtures+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Fixture source escapes its directory.");
            var input=Input();input["source"]=source;input["name"]="MCP_P3_"+name+"_"+DateTimeOffset.UtcNow.ToString("HHmmss");
            var added=await Call("add_layer",input);layers[name]=added.GetProperty("layerUri").GetString()!;
        }
        Dictionary<string,object?> One(string name,string filename)
        {var input=Input();input["layerUri"]=layers[name];input["outputPath"]=Path.Combine(output,"results.gdb",filename);return input;}
        Dictionary<string,object?> Pair(string first,string second,string filename,string firstKey="inputLayerUri",string secondKey="clipLayerUri")
        {var input=Input();input[firstKey]=layers[first];input[secondKey]=layers[second];input["outputPath"]=Path.Combine(output,"results.gdb",filename);return input;}
        var buffer=One("points","buffer");buffer["distance"]=10;buffer["method"]="PLANAR";await Analyze("buffer","buffer_analysis",buffer);
        await Analyze("clip","clip_analysis",Pair("polygons","mask","clip"));
        foreach(string operation in new[]{"intersect","union"})
        {var input=Input();input["inputLayerUris"]=new[]{layers["polygons"],layers["mask"]};input["outputPath"]=Path.Combine(output,"results.gdb",operation);await Analyze(operation,operation+"_analysis",input);}
        var dissolve=One("polygons","dissolve");dissolve["statistics"]=new[]{"SCORE:SUM"};await Analyze("dissolve","dissolve_analysis",dissolve);
        await Analyze("spatial_join","spatial_join_analysis",Pair("points","polygons","join","targetLayerUri","joinLayerUri"));
        var projection=One("points","projected");projection["outputWkid"]=4326;await Analyze("project","project_features",projection);
        var geometry=One("polygons","geometry");geometry["method"]="PLANAR";var measured=await Analyze("geometry","calculate_geometry",geometry);
        Require(measured.GetProperty("result").GetProperty("data").GetProperty("measurements").GetProperty("MCP_AREA").EnumerateArray().All(v=>Math.Abs(v.GetDouble()-100)<1e-5),"SDK area measurements differ from exact square area.");
        var length=One("lines","length");length["method"]="PLANAR";length["geometryProperties"]=new[]{"length"};await Analyze("line_length","calculate_geometry",length);
        var distance=Pair("points","near","distance","inputLayerUri","nearLayerUri");distance["method"]="PLANAR";await Analyze("distance","calculate_distance",distance);
        var query=Input();query["layerUri"]=layers["polygons"];query["fields"]=new[]{"CATEGORY","SCORE"};query["whereClause"]="SCORE >= 2";
        var queried=await Analyze("query","query_attributes",query);
        Require(queried.GetProperty("result").GetProperty("data").GetProperty("rows")[0][1].GetInt32()==2,"SQL query did not return the second feature.");
        foreach(var (format,name) in new[]{("FEATURE_CLASS","results.gdb/converted"),("SHAPEFILE","converted_shape.shp"),("GEOJSON","converted_json.geojson"),("GEOPACKAGE","converted_package.gpkg")})
        {var input=Input();input["layerUri"]=layers["polygons"];input["format"]=format;input["outputPath"]=Path.Combine(output,name);await Analyze("convert_"+format,"convert_features",input);}
        var select=Pair("polygons","points","selected","inputLayerUri","selectingLayerUri");select["relation"]="CONTAINS";
        var selection=await Analyze("select_location","select_by_location",select);
        Require(selection.GetProperty("result").GetProperty("data").GetProperty("selectedCount").GetInt32()==1,"Location selection count mismatch.");
        var selectedQuery=Input();selectedQuery["layerUri"]=layers["polygons"];selectedQuery["fields"]=new[]{"SCORE"};
        var selected=await Analyze("selected_query","query_attributes",selectedQuery);
        Require(selected.GetProperty("result").GetProperty("data").GetProperty("rows").GetArrayLength()==1,"Current map selection was not honored.");
        var denied=One("points","buffer");denied["distance"]=10;denied["method"]="PLANAR";
        denied["outputPath"]=jobs["buffer"].GetProperty("outputPath").GetString();
        await Call("buffer_analysis",denied,"output_reserved");
        denied["operationId"]=Guid.NewGuid().ToString("N");denied["outputPath"]=jobs["convert_SHAPEFILE"].GetProperty("outputPath").GetString();
        await Call("buffer_analysis",denied,"output_exists");
        checks.Add(new{name="existing and reserved output protection",passed=true});
        var badField=Input();badField["layerUri"]=layers["polygons"];badField["fields"]=new[]{"MISSING"};var failure=await Analyze("bad_field_rejected","query_attributes",badField,false);
        Require(failure.GetProperty("state").GetString()=="failed","Invalid field did not fail.");
        var pending=await Call("analysis_environment",Input());string pendingId=pending.GetProperty("jobId").GetString()!;
        await Call("cancel_analysis_job",new(){["jobId"]=pendingId});jobs["cancellation"]=await Wait(pendingId);
        var finalLayers=await Call("list_layers",new(){["mapUri"]=map});report["phase3FinalLayers"]=finalLayers;
        var original=baseline.GetProperty("items").EnumerateArray().ToArray();
        var originalAfter=finalLayers.GetProperty("items").EnumerateArray().Where(l=>original.Any(b=>b.GetProperty("uri").GetString()==l.GetProperty("uri").GetString())).ToArray();
        Require(original.Length==originalAfter.Length && original.Zip(originalAfter).All(pair=>JsonElement.DeepEquals(pair.First,pair.Second)),"An original layer changed during analysis acceptance.");
        report["phase3LivePassed"]=true;
        report["phase3LiveCheckCount"]=checks.Count;
    }
}
