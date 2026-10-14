using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.NodeAgent.Runtime;

public sealed class PluginLaunch
{
    public string Id { get; set; }="";
    public string Version { get; set; }="";
    public string Executable { get; set; }="";
    public string[] Arguments { get; set; }=[];
    public string[] ReadOnlyPaths { get; set; }=[];
}

/// <summary>Third party implementations run as separate child processes with a versioned JSON envelope.</summary>
public sealed class PluginProcessHost(ProcessToolRunner processes,IConfiguration configuration,NodeAgentOptions options)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public IReadOnlyList<PluginLaunch> Installed=>configuration.GetSection("Runtime:Plugins").Get<PluginLaunch[]>()??[];
    public async Task<PluginManifest> DescribeAsync(PluginLaunch plugin,CancellationToken ct)
    {
        var directory=Path.Combine(options.DataDirectory,"plugin-describe",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plugin.Id+"@"+plugin.Version))));
        Directory.CreateDirectory(directory);
        ValidatePaths(plugin);
        var result=await processes.RunAsync(plugin.Executable,plugin.Arguments,directory,TimeSpan.FromSeconds(20),ct,"{\"type\":\"describe\",\"contractVersion\":1}\n",sandbox:true,readOnlyPaths:plugin.ReadOnlyPaths);
        var manifest=JsonSerializer.Deserialize<PluginManifest>(result.Output,Json)??throw new IOException("Manifeste plugin absent.");
        if(result.ExitCode!=0 || manifest.ContractVersion!=1 || manifest.Id!=plugin.Id || manifest.Version!=plugin.Version || !manifest.SupportedHostContractVersions.Contains(1)) throw new IOException("Handshake plugin incompatible.");
        return manifest;
    }
    private void ValidatePaths(PluginLaunch plugin)
    {
        var protectedRoot=Path.GetFullPath(options.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach(var candidate in plugin.ReadOnlyPaths.Append(plugin.Executable))
        {
            var path=Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            if(protectedRoot==path || protectedRoot.StartsWith(path+Path.DirectorySeparatorChar,StringComparison.Ordinal)
                || path.StartsWith(protectedRoot+Path.DirectorySeparatorChar,StringComparison.Ordinal))
                throw new IOException("Les fichiers du plugin doivent être installés en dehors des données privées du nœud.");
        }
    }
    public async Task<FlowStepResult> ExecuteAsync(FlowStepInvocation invocation,string workspace,CancellationToken ct)
    {
        var plugin=Installed.SingleOrDefault(p=>p.Id==invocation.Component.PluginId && p.Version==invocation.Component.PluginVersion)
            ??throw new IOException("Cette version du plugin n’est pas installée sur le worker.");
        var manifest=await DescribeAsync(plugin,ct);
        if(!manifest.Components.Any(component=>component.Id==invocation.Component.ComponentId && component.Version==invocation.Component.ComponentVersion && component.ConfigurationSchema.Version==invocation.Component.ConfigurationSchemaVersion)) throw new IOException("Version de composant absente du processus plugin.");
        var input=JsonSerializer.Serialize(new { type="execute",contractVersion=1,invocation },Json)+"\n";
        var result=await processes.RunAsync(plugin.Executable,plugin.Arguments,workspace,invocation.Deadline-DateTimeOffset.UtcNow,ct,input,1048576,true,plugin.ReadOnlyPaths);
        if(result.ExitCode!=0) throw new IOException("Le processus du plugin a échoué ; son effet doit être considéré comme incertain.");
        var response=JsonSerializer.Deserialize<FlowStepResult>(result.Output,Json)??throw new IOException("Réponse plugin absente.");
        if(response.ContractVersion!=1 || response.InvocationId!=invocation.InvocationId || response.LeaseEpoch!=invocation.LeaseEpoch
            || (response.OutputId is null)==(response.TerminalResult is null)) throw new IOException("Réponse plugin incompatible avec l’invocation.");
        return response;
    }
}
