using System.Diagnostics;
using System.Text;

namespace Encoding.NodeAgent.Runtime;

public sealed record ToolResult(int ExitCode, string Output);
public sealed class MediaToolException(string tool, int exitCode) : Exception($"L’outil {tool} a échoué (code {exitCode}).");

public sealed class ProcessToolRunner
{
    private const int CapturedLogLimit = 32 * 1024;

    public static string? Find(string executable)
    {
        var name = OperatingSystem.IsWindows() ? executable + ".exe" : executable;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
    }

    public async Task<ToolResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken ct, string? input = null, int outputLimit = 2 * 1024 * 1024,
        bool sandbox = false, IReadOnlyList<string>? readOnlyPaths = null)
    {
        var argumentList = arguments.ToList();
        if (sandbox)
        {
            if (!ProcessSandbox.IsAvailable) throw new IOException("L’isolation Linux Landlock est requise pour les scripts et plugins.");
            var host = Environment.ProcessPath ?? throw new IOException("Hôte de processus absent.");
            var prefix = new List<string>();
            if (Path.GetFileNameWithoutExtension(host) == "dotnet") prefix.Add(typeof(ProcessToolRunner).Assembly.Location);
            prefix.AddRange(["--isolated-process", workingDirectory, executable, System.Text.Json.JsonSerializer.Serialize(readOnlyPaths ?? [])]);
            argumentList = prefix.Concat(argumentList).ToList();
            executable = host;
        }

        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
        };
        foreach (var arg in argumentList) info.ArgumentList.Add(arg);
        // MKVToolNix lit les arguments et écrit son JSON selon la locale : en locale « C » (image sans LANG) les noms accentués sont tronqués.
        if (Path.GetFileName(executable).StartsWith("mkv", StringComparison.Ordinal)) info.Environment["LC_ALL"] = "C.UTF-8";
        var startedAt = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(info) ?? throw new IOException("Le processus ne peut pas démarrer.");
        var outputLog = new StringBuilder();
        var errorLog = new StringBuilder();
        var truncated = false;
        int? exitCode = null;
        Task<string>? output = null;
        Task<string>? errors = null;

        async Task<string> ReadAsync(StreamReader reader, bool collectOutput, StringBuilder captured)
        {
            var result = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, deadline.Token)) > 0)
            {
                var take = Math.Min(count, Math.Max(0, CapturedLogLimit - captured.Length));
                if (take > 0) captured.Append(buffer, 0, take);
                if (take < count) truncated = true;
                if (!collectOutput) continue;
                if (result.Length + count > outputLimit)
                {
                    deadline.Cancel();
                    throw new IOException("La réponse du processus dépasse la limite du contrat.");
                }
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }

        try
        {
            output = ReadAsync(process.StandardOutput, true, outputLog);
            errors = ReadAsync(process.StandardError, false, errorLog);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token);
            await errors;
            exitCode = process.ExitCode;
            return new(process.ExitCode, await output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            if (process.HasExited) exitCode ??= process.ExitCode;
            try { await Task.WhenAll(new[] { output, errors }.Where(task => task is not null).Select(task => task!)); }
            catch (Exception) when (deadline.IsCancellationRequested) { }
            RuntimeCommandCapture.Record(executable, argumentList, outputLog.ToString(), errorLog.ToString(),
                exitCode, startedAt, process.HasExited ? DateTimeOffset.UtcNow : null, truncated, input is not null);
        }
    }

    public async Task<string> CheckedAsync(string executable, IEnumerable<string> args, string directory, CancellationToken ct)
    {
        var result = await RunAsync(Find(executable) ?? throw new IOException($"Outil indisponible : {executable}."),
            args, directory, TimeSpan.FromHours(6), ct);
        if (result.ExitCode != 0 && !(result.ExitCode == 1 && executable is "mkvmerge" or "mkvextract" or "mkvpropedit"))
            throw new MediaToolException(executable, result.ExitCode);
        return result.Output;
    }
}
