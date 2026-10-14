using System.Diagnostics;
using System.Text;

namespace Encoding.NodeAgent.Runtime;

public sealed record ToolResult(int ExitCode,string Output);
public sealed class MediaToolException(string tool,int exitCode) : Exception($"L’outil {tool} a échoué (code {exitCode}).");

public sealed class ProcessToolRunner
{
    public static string? Find(string executable)
    {
        var name=OperatingSystem.IsWindows()?executable+".exe":executable;
        return (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries)
            .Select(directory=>Path.Combine(directory,name)).FirstOrDefault(File.Exists);
    }
    public async Task<ToolResult> RunAsync(string executable,IEnumerable<string> arguments,string workingDirectory,TimeSpan timeout,CancellationToken ct,string? input=null,int outputLimit=2*1024*1024,bool sandbox=false,IReadOnlyList<string>? readOnlyPaths=null)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        if(sandbox)
        {
            if(!ProcessSandbox.IsAvailable) throw new IOException("L’isolation Linux Landlock est requise pour les scripts et plugins.");
            var host=Environment.ProcessPath??throw new IOException("Hôte de processus absent.");
            var prefix=new List<string>();
            if(Path.GetFileNameWithoutExtension(host)=="dotnet") prefix.Add(typeof(ProcessToolRunner).Assembly.Location);
            prefix.AddRange(["--isolated-process",workingDirectory,executable,System.Text.Json.JsonSerializer.Serialize(readOnlyPaths??[])]);
            arguments=prefix.Concat(arguments).ToArray(); executable=host;
        }
        var info=new ProcessStartInfo(executable) { WorkingDirectory=workingDirectory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=input is not null };
        foreach(var arg in arguments) info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("Le processus ne peut pas démarrer.");
        async Task<string> ReadAsync(StreamReader reader,bool collect)
        {
            var result=new StringBuilder(); var buffer=new char[4096]; int count;
            while((count=await reader.ReadAsync(buffer,deadline.Token))>0)
            {
                if(collect)
                {
                    if(result.Length+count>outputLimit) { deadline.Cancel(); throw new IOException("La réponse du processus dépasse la limite du contrat."); }
                    result.Append(buffer,0,count);
                }
            }
            return result.ToString();
        }
        var output=ReadAsync(process.StandardOutput,true); var errors=ReadAsync(process.StandardError,false);
        try
        {
            if(input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(),deadline.Token); process.StandardInput.Close(); }
            await process.WaitForExitAsync(deadline.Token); await errors; return new(process.ExitCode,await output);
        }
        finally
        {
            if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(output,errors); } catch(Exception) when(deadline.IsCancellationRequested) { }
        }
    }
    public async Task<string> CheckedAsync(string executable,IEnumerable<string> args,string directory,CancellationToken ct)
    {
        var result=await RunAsync(Find(executable)??throw new IOException($"Outil indisponible : {executable}."),args,directory,TimeSpan.FromHours(6),ct);
        if(result.ExitCode!=0 && !(result.ExitCode==1 && executable is "mkvmerge" or "mkvextract" or "mkvpropedit")) throw new MediaToolException(executable,result.ExitCode); return result.Output;
    }
}
