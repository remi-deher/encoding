using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Runtime;
namespace Encoding.NodeAgent.Services;

public sealed class ToolCapabilityProbe(ProcessToolRunner processes,PluginProcessHost plugins,NodeAgentOptions options) : IToolCapabilityProbe
{
    private readonly SemaphoreSlim gate=new(1,1);
    private IReadOnlyList<NodeToolCapability>? cached;
    private DateTimeOffset refreshed;
    public async Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(cached is not null && refreshed>DateTimeOffset.UtcNow-TimeSpan.FromMinutes(1)) return cached;
            Directory.CreateDirectory(options.DataDirectory);
            var list=new List<NodeToolCapability> {
                new("plugin/base","1.0.0",["general"],["cpu"]),
                new("plugin/nodes","1.1.0",["general"],[])
            };
            foreach(var (id,names,args) in new (string,string[],string[])[] {
                ("ffmpeg",["ffmpeg"],["-version"]),("ffprobe",["ffprobe"],["-version"]),
                ("mkvtoolnix",["mkvmerge"],["--version"]),("mediainfo",["mediainfo"],["--Version"]),
                ("imagemagick",["magick","convert"],["-version"]),("python",["python3","python"],["--version"]),
                ("javascript",["node"],["--version"])
            })
            {
                if(id is "python" or "javascript" && !ProcessSandbox.IsAvailable) continue;
                var exe=names.Select(ProcessToolRunner.Find).FirstOrDefault(path=>path is not null); if(exe is null) continue;
                try {
                    var result=await processes.RunAsync(exe,args,options.DataDirectory,TimeSpan.FromSeconds(10),ct);
                    if(result.ExitCode!=0) continue;
                    var version=System.Text.RegularExpressions.Regex.Match(result.Output,@"\d+\.\d+(?:\.\d+)?").Value;
                    if(version.Length==0) version="detected";
                    var acceleration=new List<string> { "cpu" };
                    var available=new List<string>();
                    if(id=="ffmpeg") {
                        var encoders=await processes.RunAsync(exe,["-hide_banner","-encoders"],options.DataDirectory,TimeSpan.FromSeconds(10),ct);
                        available.AddRange(encoders.Output.Split('\n').Select(line=>line.Split(' ',StringSplitOptions.RemoveEmptyEntries)).Where(parts=>parts.Length>1 && parts[0].Length==6).Select(parts=>parts[1]));
                        if(File.Exists("/dev/nvidia0") && encoders.Output.Contains("hevc_nvenc")) acceleration.Add("nvenc");
                        if(File.Exists("/dev/dri/renderD128") && encoders.Output.Contains("hevc_qsv")) acceleration.Add("qsv");
                        if(File.Exists("/dev/dri/renderD128") && encoders.Output.Contains("hevc_vaapi")) acceleration.Add("vaapi");
                    }
                    list.Add(new(id,version,["video","audio","image","subtitles","general"],acceleration,available));
                } catch(Exception) when(!ct.IsCancellationRequested) { }
            }
            foreach(var plugin in plugins.Installed) {
                try { var manifest=await plugins.DescribeAsync(plugin,ct); list.Add(new("plugin/"+manifest.Id,manifest.Version,["general"],["process"])); }
                catch(Exception) when(!ct.IsCancellationRequested) { }
            }
            cached=list; refreshed=DateTimeOffset.UtcNow; return list;
        }
        finally { gate.Release(); }
    }
}
