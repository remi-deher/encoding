using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

public sealed class RuntimeEligibility(FlowComponentRegistry registry, FlowDefinitionValidator validator)
{
    public bool CanRun(FlowDefinition flow,IReadOnlyList<NodeToolCapability> capabilities,Guid workerId,string? nextNodeId=null,bool resumed=false)
    {
        if(!validator.Validate(flow).IsValid) return false;
        var entry=nextNodeId??(resumed?null:flow.Nodes.Single(node=>registry.Resolve(node.Component)?.Kind=="input").Id);
        foreach(var node in entry is null?Array.Empty<FlowNodeDefinition>():RuntimeHandoffPolicy.Segment(flow,entry))
        {
            if(!capabilities.Any(c=>c.ToolId=="plugin/"+node.Component.PluginId && (c.Version==node.Component.PluginVersion
                || node.Component.PluginId=="nodes" && node.Component.PluginVersion=="1.0.0" && c.Version=="1.1.0"))) return false;
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
            if(node.Component.ComponentId=="base-transcode-video" && node.Configuration.TryGetValue("videoCodec",out var codec)
                && MediaCodecPolicy.VideoEncoder(codec.GetString()!,node.Configuration.TryGetValue("hardwareAcceleration",out var hw)?hw.GetString()!:"cpu",encoders) is null) return false;
            if(node.Component.ComponentId=="base-transcode-audio" && node.Configuration.TryGetValue("audioCodec",out var audio)
                && MediaCodecPolicy.AudioEncoder(audio.GetString()!,encoders) is null) return false;
        }
        // Every media execution records a source scan before completion, even if no transformation is selected.
        return capabilities.Any(c=>c.ToolId=="ffprobe");
    }
}
