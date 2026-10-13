using System.Diagnostics;
using Encoding.Contracts.Nodes;

namespace Encoding.NodeAgent.Services;

public sealed class ToolCapabilityProbe : IToolCapabilityProbe
{
    public async Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable("ffmpeg");
        if (executable is null)
            return [];

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-version");

        using var process = Process.Start(startInfo);
        if (process is null)
            return [];

        var firstLine = await process.StandardOutput.ReadLineAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(firstLine))
            return [];

        var version = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith('n') && part.Length > 1)
            ?? "unknown";
        return
        [
            new NodeToolCapability(
                "ffmpeg",
                version,
                ["video", "audio", "image", "subtitles"],
                ["cpu"])
        ];
    }

    private static string? FindExecutable(string executableName)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{executableName}.exe" : executableName;
        var searchPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(searchPath))
            return null;

        return searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
    }
}
