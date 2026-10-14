using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

public sealed class RuntimeEligibility(FlowComponentRegistry registry, FlowDefinitionValidator validator)
{
    /// <summary>Une sonde n'exécute qu'une condition de plugin : seule la capacité de ce plugin est requise.</summary>
    public bool CanProbe(FlowDefinition flow,IReadOnlyList<NodeToolCapability> capabilities,string blockNodeId)
    {
        var node=flow.Nodes.SingleOrDefault(item=>item.Id==blockNodeId);
        return node is not null && registry.Resolve(node.Component) is not null && capabilities.Any(c=>c.ToolId=="plugin/"+node.Component.PluginId
            && (c.Version==node.Component.PluginVersion || c.Version==registry.RuntimePluginVersion(node.Component)));
    }

    public bool CanRun(FlowDefinition flow,IReadOnlyList<NodeToolCapability> capabilities,Guid workerId,string? nextNodeId=null,bool resumed=false,RuntimeContext? context=null)
    {
        if(!validator.Validate(flow).IsValid) return false;
        var entry=nextNodeId??(resumed?null:flow.Nodes.Single(node=>registry.Resolve(node.Component)?.Kind=="input").Id);
        var segment=entry is null?Array.Empty<FlowNodeDefinition>():RuntimeHandoffPolicy.Segment(flow,entry);
        var planned=(context?.Plan??[]).Select((step,index)=>new FlowNodeDefinition("plan-"+index,null,
            new("base","1.0.0",step.ComponentId,"1.0.0",1),step.Configuration));
        foreach(var node in segment.Concat(planned))
        {
            if(!capabilities.Any(c=>c.ToolId=="plugin/"+node.Component.PluginId && (c.Version==node.Component.PluginVersion
                || c.Version==registry.RuntimePluginVersion(node.Component)))) return false;
            if(node.Component.PluginId=="nodes" && node.Component.ComponentId=="nodes-run-on-node"
                && (!node.Configuration.TryGetValue("targetNodeId",out var target) || target.ValueKind!=System.Text.Json.JsonValueKind.String
                    || !Guid.TryParse(target.GetString(),out var targetId) || targetId!=workerId)) return false;
            if(node.Component.PluginId=="nodes" && node.Component.ComponentId=="nodes-require-capability")
            {
                var toolId=node.Configuration.TryGetValue("toolId",out var requestedTool)?requestedTool.GetString():null;
                var minimum=node.Configuration.TryGetValue("minimumVersion",out var requestedVersion)?requestedVersion.GetString():null;
                var required=node.Configuration.TryGetValue("requiredCapability",out var requestedCapability)?requestedCapability.GetString():null;
                if(toolId is null || !capabilities.Any(capability=>capability.ToolId==toolId
                    && (string.IsNullOrWhiteSpace(minimum) || Version.TryParse(capability.Version,out var actual) && Version.TryParse(minimum,out var min) && actual>=min)
                    && (string.IsNullOrWhiteSpace(required) || capability.AccelerationTypes.Contains(required,StringComparer.Ordinal)))) return false;
            }
            var component=registry.Resolve(node.Component)!;
            if(component is null) return false;
            if(node.Component.PluginId=="base" && node.Component.ComponentId=="base-prepare-processing"
                && !capabilities.Any(c=>c.ToolId==(node.Configuration.TryGetValue("tool",out var chosen)?chosen.GetString():"ffmpeg"))) return false;
            if(node.Component.PluginId=="base" && node.Component.ComponentId=="base-execute-processing"
                && !capabilities.Any(c=>c.ToolId=="ffmpeg" || c.ToolId=="mkvtoolnix")) return false;
            foreach(var tool in component.RequiredTools)
                if(!capabilities.Any(c=>c.ToolId==tool.ToolId && (tool.MinimumVersion is null || Version.TryParse(c.Version,out var actual) && Version.TryParse(tool.MinimumVersion,out var minimum) && actual>=minimum)
                    && tool.RequiredCapabilities.All(feature=>c.AccelerationTypes.Contains(feature)))) return false;
            if(node.Configuration.TryGetValue("hardwareAcceleration",out var acceleration) && acceleration.GetString() is { } name && name!="cpu"
                && !capabilities.Any(c=>c.ToolId=="ffmpeg" && c.AccelerationTypes.Contains(name))) return false;
            if(node.Component.ComponentId=="base-script" && node.Configuration.TryGetValue("language",out var language)
                && !capabilities.Any(c=>c.ToolId==(language.GetString()=="python"?"python":"javascript"))) return false;
            if(node.Component.ComponentId=="base-condition" && !capabilities.Any(c=>c.ToolId=="javascript")) return false;
            var encoders=capabilities.FirstOrDefault(c=>c.ToolId=="ffmpeg")?.Encoders??[];
            if(node.Component.ComponentId=="base-transcode-video")
            {
                var codec=node.Configuration.TryGetValue("videoCodec",out var codecValue)?codecValue.GetString()!:"hevc";
                if(node.Configuration.TryGetValue("encodingProfile",out var profileValue)
                    && profileValue.ValueKind==System.Text.Json.JsonValueKind.Object)
                {
                    var profile=profileValue.Deserialize<EncodingProfileSnapshot>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                    var mode=node.Configuration.TryGetValue("encoderMode",out var modeValue)?modeValue.GetString()!:"automatic";
                    var selected=node.Configuration.TryGetValue("encoderId",out var encoderValue)?encoderValue.GetString():null;
                    var ffmpeg=capabilities.FirstOrDefault(capability=>capability.ToolId=="ffmpeg");
                    if(profile is null || ffmpeg is null
                        || MediaCodecPolicy.ResolveVideoEncoder(codec,mode,selected,profile,ffmpeg.Encoders??[],ffmpeg.AccelerationTypes,ffmpeg.EncoderChecks) is null) return false;
                }
                else if(MediaCodecPolicy.VideoEncoder(codec,node.Configuration.TryGetValue("hardwareAcceleration",out var hw)?hw.GetString()!:"cpu",encoders) is null) return false;
            }
            if(node.Component.ComponentId=="base-transcode-audio" && node.Configuration.TryGetValue("audioCodec",out var audio)
                && MediaCodecPolicy.AudioEncoder(audio.GetString()!,encoders) is null) return false;
        }
        // Every media execution records a source scan before completion, even if no transformation is selected.
        return capabilities.Any(c=>c.ToolId=="ffprobe");
    }
}
