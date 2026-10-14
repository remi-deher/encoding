using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Executions;

public sealed class RuntimeEligibility(FlowComponentRegistry registry, FlowDefinitionValidator validator)
{
    public bool CanRun(FlowDefinition flow,IReadOnlyList<NodeToolCapability> capabilities)
    {
        if(!validator.Validate(flow).IsValid) return false;
        foreach(var node in flow.Nodes)
        {
            if(!capabilities.Any(c=>c.ToolId=="plugin/"+node.Component.PluginId && c.Version==node.Component.PluginVersion)) return false;
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
