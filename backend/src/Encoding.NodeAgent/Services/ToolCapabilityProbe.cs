using System.Text.RegularExpressions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Runtime;

namespace Encoding.NodeAgent.Services;

public sealed class ToolCapabilityProbe(
    ProcessToolRunner processes,
    PluginProcessHost plugins,
    NodeAgentOptions options) : IToolCapabilityProbe
{
    private static readonly Regex EncoderLine = new(
        @"^\s*(?<flags>[A-Z\.]{6})\s+(?<name>[A-Za-z0-9_-]+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<NodeToolCapability>? cached;
    private DateTimeOffset refreshed;

    public async Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null && refreshed > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5))
                return cached;

            Directory.CreateDirectory(options.DataDirectory);
            var list = new List<NodeToolCapability>
            {
                new("plugin/base", "1.0.0", ["general"], ["cpu"]),
                new("plugin/nodes", "1.1.0", ["general"], []),
                new("plugin/conditions", "1.0.0", ["general"], [])
            };

            foreach (var (id, names, args) in new (string, string[], string[])[]
            {
                ("ffmpeg", ["ffmpeg"], ["-version"]), ("ffprobe", ["ffprobe"], ["-version"]),
                ("mkvtoolnix", ["mkvmerge"], ["--version"]), ("mediainfo", ["mediainfo"], ["--Version"]),
                ("imagemagick", ["magick", "convert"], ["-version"]), ("python", ["python3", "python"], ["--version"]),
                ("javascript", ["node"], ["--version"])
            })
            {
                if (id is "python" or "javascript" && !ProcessSandbox.IsAvailable) continue;
                var executable = names.Select(ProcessToolRunner.Find).FirstOrDefault(path => path is not null);
                if (executable is null) continue;

                try
                {
                    var result = await processes.RunAsync(executable, args, options.DataDirectory, TimeSpan.FromSeconds(10), ct);
                    if (result.ExitCode != 0) continue;
                    var version = Regex.Match(result.Output, @"\d+\.\d+(?:\.\d+)?").Value;
                    if (version.Length == 0) version = "detected";

                    var encoders = new List<string>();
                    var checks = new List<NodeEncoderCapability>();
                    var acceleration = new List<string>();
                    if (id == "ffmpeg")
                    {
                        var encoderOutput = await processes.RunAsync(executable, ["-hide_banner", "-encoders"],
                            options.DataDirectory, TimeSpan.FromSeconds(10), ct);
                        if (encoderOutput.ExitCode != 0) continue;

                        encoders.AddRange(encoderOutput.Output.Split('\n')
                            .Select(line => EncoderLine.Match(line))
                            .Where(match => match.Success)
                            .Select(match => match.Groups["name"].Value)
                            .Distinct(StringComparer.Ordinal));

                        foreach (var encoder in VideoEncoderCatalog.All.Where(item => encoders.Contains(item.Id, StringComparer.Ordinal)))
                        {
                            var usable = await CheckEncoderAsync(executable, encoder, ct);
                            checks.Add(new(encoder.Id, usable ? "usable" : "detected"));
                            if (usable && !acceleration.Contains(encoder.Acceleration, StringComparer.Ordinal))
                                acceleration.Add(encoder.Acceleration);
                        }
                    }

                    list.Add(new(id, version, ["video", "audio", "image", "subtitles", "general"],
                        acceleration, encoders, id == "ffmpeg" ? checks : null));
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }

            foreach (var plugin in plugins.Installed)
            {
                try
                {
                    var manifest = await plugins.DescribeAsync(plugin, ct);
                    list.Add(new("plugin/" + manifest.Id, manifest.Version, ["general"], ["process"]));
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }

            cached = list;
            refreshed = DateTimeOffset.UtcNow;
            return list;
        }
        finally { gate.Release(); }
    }

    private async Task<bool> CheckEncoderAsync(string executable, VideoEncoderDescriptor encoder, CancellationToken ct)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error" };
        if (encoder.Acceleration == "vaapi")
            args.AddRange(["-vaapi_device", "/dev/dri/renderD128"]);
        else if (encoder.Acceleration == "qsv")
            args.AddRange(["-init_hw_device", "qsv=hw,child_device=/dev/dri/renderD128", "-filter_hw_device", "hw"]);

        args.AddRange(["-f", "lavfi", "-i", "color=c=black:s=64x64:r=1", "-frames:v", "1", "-an"]);
        if (encoder.Acceleration == "vaapi") args.AddRange(["-vf", "format=nv12,hwupload"]);
        if (encoder.Acceleration == "qsv") args.AddRange(["-vf", "format=nv12,hwupload=extra_hw_frames=64,format=qsv"]);
        args.AddRange(["-c:v", encoder.Id, "-f", "null", "-"]);

        try
        {
            var result = await processes.RunAsync(executable, args, options.DataDirectory, TimeSpan.FromSeconds(4), ct,
                outputLimit: 64 * 1024);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (IOException) when (!ct.IsCancellationRequested) { return false; }
    }
}
