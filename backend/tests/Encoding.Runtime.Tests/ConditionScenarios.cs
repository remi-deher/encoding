using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Contracts.Nodes;

namespace Encoding.Runtime.Tests;

internal static class ConditionScenarios
{
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void Run(FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility)
    {
        var before=J(new { streams=new object[]{new{codec_type="video",codec_name="h264",width=1920,height=1080}}, format=new{size="1000",duration="12.5"} });
        var after=J(new { streams=new object[]{new{codec_type="video",codec_name="hevc",width=3840,height=2160},new{codec_type="audio",codec_name="aac",channels=6,tags=new{language="fra"}}}, format=new{size="800",duration="12.5"} });
        var source=Guid.NewGuid();var worker=Guid.NewGuid();var library=Guid.NewGuid();
        var runtime=new RuntimeContext(new string('b',64),".mkv",new Dictionary<string,JsonElement>{["score"]=J(42),["enabled"]=J(true)},before,after,[]);
        var data=new ConditionEvaluationContext(runtime,source,worker,library,"folder/source.mkv",new string('a',64));
        ConditionRule Rule(string field,string op,object? value=null,string? variable=null,string scope="current")=>new(field,op,value is null?null:J(value),variable,scope);
        var tree=new ConditionGroup("all",[Rule("video.codec","eq","HEVC"),new ConditionGroup("any",[Rule("video.height","gt",1080),Rule("variable.number","lt",1,"score")]),Rule("audio.languages","contains","FRA")]);
        var document=ConditionTree.Serialize(tree);
        Check(ConditionTree.Validate(document,"video") is null,"Nested AND/OR conditions validate.");
        Check(document.GetProperty("kind").GetString()=="group" && document.GetProperty("children")[0].GetProperty("kind").GetString()=="rule","The portable tree has explicit discriminators.");
        Check(ConditionTree.Evaluate(ConditionTree.Parse(document,"video"),data),"Nested AND/OR conditions evaluate against current metadata.");
        Check(ConditionTree.RequiresMetadata(tree),"Media criteria require a scan.");
        Check(ConditionTree.Evaluate(Rule("video.codec","eq","h264",scope:"source"),data),"The original snapshot remains selectable after conversion.");
        Check(!ConditionTree.Evaluate(Rule("subtitles.present","eq",true),data),"Absent subtitle tracks are an observed absence.");
        Check(ConditionTree.Evaluate(Rule("file.duration","eq",12.5m),data),"ffprobe numeric strings use invariant culture.");
        Check(ConditionTree.Evaluate(Rule("file.size","lt",1000),data),"File sizes compare numerically.");
        Check(ConditionTree.Evaluate(Rule("variable.boolean","eq",true,"enabled"),data),"Boolean variables stay typed.");
        Check(ConditionTree.Evaluate(Rule("source.nodeId","eq",source.ToString()),data) && ConditionTree.Evaluate(Rule("library.id","eq",library.ToString()),data),"Origin criteria use stable execution context.");
        Check(ConditionTree.Evaluate(Rule("processing.modified","eq",true),data),"Modified state uses current and source digests.");
        Check(!ConditionTree.RequiresMetadata(new ConditionGroup("all",[Rule("variable.number","gte",42,"score"),Rule("source.nodeId","eq",source.ToString())])),"Variable and origin rules do not need a scan.");
        Check(ConditionTree.Evaluate(Rule("variable.text","missing",variable:"absent"),data),"Missing values can be tested explicitly.");
        Check(!ConditionTree.Evaluate(new ConditionGroup("all",[Rule("variable.text","exists",variable:"absent"),Rule("variable.text","eq","x","absent")]),data),"AND short-circuits an explicit presence guard.");
        ExpectError(()=>ConditionTree.Evaluate(Rule("variable.text","eq","x","absent"),data),"A missing value is an error, not false.");
        ExpectError(()=>ConditionTree.Evaluate(Rule("variable.text","eq","42","score"),data),"A wrong runtime type is an error.");
        var invalid=new ConditionGroup("all",[Rule("video.height","contains","1080")]);
        Check(ConditionTree.Validate(ConditionTree.Serialize(invalid),"video") is not null,"Incompatible operators are rejected.");
        Check(ConditionTree.Validate(document,"music") is not null,"Media-incompatible fields are rejected.");
        Check(ConditionTree.Validate(ConditionTree.Serialize(new ConditionGroup("all",[])),"video") is not null,"Empty groups cannot execute.");
        Check(ConditionTree.Validate(ConditionTree.Serialize(new ConditionGroup("all",[])),"video",true) is null,"Drafts can retain incomplete groups.");
        Check(ConditionTree.Validate(J(new{kind="group",match="all",children=new[]{new{kind="rule",field="file.extension",@operator="eq",value="mkv",code="arbitrary"}}}),"video") is not null,"Unknown properties cannot introduce executable code.");
        ConditionExpression deep=Rule("file.extension","eq","mkv");
        for(var i=0;i<ConditionTree.MaxDepth;i++) deep=new ConditionGroup("all",[deep]);
        Check(ConditionTree.Validate(ConditionTree.Serialize(deep),"video") is not null,"Excessive nesting is rejected.");
        Check(ConditionTree.Validate(ConditionTree.Serialize(new ConditionGroup("all",Enumerable.Repeat<ConditionExpression>(Rule("file.extension","eq","mkv"),100).ToArray())),"video") is not null,"Excessive node counts are rejected.");

        var manifest=registry.Plugins.Single(plugin=>plugin.Id=="conditions");var component=manifest.Components.Single();
        var presetJson=JsonSerializer.SerializeToElement(manifest,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(presetJson.GetProperty("components")[0].GetProperty("configurationSchema").GetProperty("fields")[0].GetProperty("conditionPresets")[0].GetProperty("expression").GetProperty("kind").GetString()=="group","Catalog presets serialize as portable groups.");
        var input=registry.Plugins.Single(plugin=>plugin.Id=="base").Components.Single(component=>component.Id=="base-input-file");
        var reference=new FlowComponentReference("conditions",manifest.Version,component.Id,component.Version,1);
        var flow=new FlowDefinition(1,Guid.NewGuid(),1,"Native conditions","video",[
            new("input",null,new("base","1.0.0",input.Id,input.Version,1),new Dictionary<string,JsonElement>()),
            new("condition",null,reference,new Dictionary<string,JsonElement>{["rules"]=document})],
            [new("edge","input","success","condition")]);
        Check(validator.Validate(flow).IsValid,"The flow validator accepts the structured tree.");
        IReadOnlyList<NodeToolCapability> caps=[new("plugin/base","1.0.0",["general"],[]),new("plugin/conditions","1.0.0",["general"],[]),new("ffprobe","7.0",["video"],[])];
        Check(eligibility.CanRun(flow,caps,worker),"Native conditions do not require JavaScript or a third-party process.");
        Check(!eligibility.CanRun(flow,caps.Where(cap=>cap.ToolId!="plugin/conditions").ToArray(),worker),"An old agent cannot execute the new plugin.");
        var draft=flow with { Nodes=[flow.Nodes[0],flow.Nodes[1] with { Configuration=new Dictionary<string,JsonElement>{["rules"]=ConditionTree.Serialize(new ConditionGroup("all",[]))} }] };
        Check(validator.ValidateDraft(draft).IsValid && !validator.Validate(draft).IsValid,"An incomplete draft cannot become an execution.");
    }
    private static void ExpectError(Action action,string message)
    {
        try { action(); }
        catch(ConditionEvaluationException) { return; }
        throw new InvalidOperationException(message);
    }
}
