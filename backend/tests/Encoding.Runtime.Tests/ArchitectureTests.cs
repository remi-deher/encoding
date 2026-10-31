using System.Text.RegularExpressions;
using Xunit;

namespace Encoding.Runtime.Tests;

// Garde-fous de séparation des responsabilités (BACKEND_RULES.md §3 et §8).
// Les plafonds de taille sont un cliquet : ils ne peuvent que baisser.
public sealed class ArchitectureTests
{
    private static readonly string SourceRoot = FindSourceRoot();

    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["Encoding.Contracts"] = [],
        ["Encoding.Core"] = ["Encoding.Contracts"],
        ["Encoding.Api"] = ["Encoding.Contracts", "Encoding.Core"],
        ["Encoding.NodeAgent"] = ["Encoding.Contracts", "Encoding.Core"],
        ["Encoding.Launcher"] = ["Encoding.Core"],
    };

    private const int MaxFileLines = 600;

    // Dette connue : fichiers déjà au-dessus de MaxFileLines. Ne jamais augmenter une valeur.
    private static readonly Dictionary<string, int> OversizedFileCeilings = new()
    {
        ["Encoding.Api/Executions/FlowPlacementPlanner.cs"] = 712,
        ["Encoding.Api/Executions/RuntimeRepository.cs"] = 994,
        ["Encoding.Api/Infrastructure/ClusterEntityReconciliation.cs"] = 988,
        ["Encoding.Api/Infrastructure/ClusterPeerSecurity.cs"] = 622,
        ["Encoding.Api/Mcp/BusinessOperationCatalog.cs"] = 736,
        ["Encoding.Api/Mcp/McpService.cs"] = 1646,
        ["Encoding.Api/Nodes/NodeRepository.cs"] = 624,
        ["Encoding.Api/Tools/AiAssistantService.cs"] = 1552,
        ["Encoding.NodeAgent/Runtime/Media/MediaActions.cs"] = 896,
        ["Encoding.NodeAgent/Runtime/RuntimeFlowRunner.cs"] = 1009,
    };

    private static readonly string[] ProcessLaunchAllowList =
    [
        "Encoding.Api/Flows/FlowMetadataImporter.cs",
        "Encoding.Api/Tools/AiCliRuntime.cs",
        "Encoding.Core/Runtime/ProcessSandbox.cs",
        "Encoding.Launcher/SystemProcess.cs",
        "Encoding.NodeAgent/Runtime/ProcessToolRunner.cs",
    ];

    // Dette connue : endpoints qui touchent encore au stockage. À vider, jamais à étendre.
    private static readonly string[] EndpointStorageDebt =
    [
        "Encoding.Api/Executions/ClusterTaskEndpoints.cs",
        "Encoding.Api/Flows/EncodingProfileEndpoints.cs",
        "Encoding.Api/Flows/FlowComponentPackEndpoints.cs",
        "Encoding.Api/Flows/FlowScriptEndpoints.cs",
        "Encoding.Api/Flows/FlowTemplateEndpoints.cs",
        "Encoding.Api/Libraries/LibraryEndpoints.cs",
        "Encoding.Api/Nodes/NodeGroupEndpoints.cs",
        "Encoding.Api/Resources/ResourceEndpoints.cs",
    ];

    [Fact]
    public void ProjectReferencesFollowTheLayering()
    {
        foreach (var (project, allowed) in AllowedProjectReferences)
        {
            var csproj = Path.Combine(SourceRoot, project, project + ".csproj");
            var references = Regex.Matches(File.ReadAllText(csproj), @"ProjectReference Include=""[^""]*?([\w.]+)\.csproj""")
                .Select(match => match.Groups[1].Value).ToArray();
            Assert.True(references.All(allowed.Contains), $"{project} référence {string.Join(", ", references.Except(allowed))}.");
        }
    }

    [Fact]
    public void SourcesDoNotUseNamespacesOutsideTheirLayer()
    {
        foreach (var (project, allowed) in AllowedProjectReferences)
        {
            var forbidden = AllowedProjectReferences.Keys.Where(other => other != project && !allowed.Contains(other)).ToArray();
            foreach (var file in SourceFiles(project))
            {
                var text = File.ReadAllText(file);
                foreach (var other in forbidden)
                    Assert.False(Regex.IsMatch(text, $@"\b{Regex.Escape(other)}[.;](?!dll)"), $"{Relative(file)} utilise {other}.");
            }
        }
    }

    [Fact]
    public void EndpointsDoNotDependOnDatabaseTypes()
    {
        var offenders = SourceFiles("Encoding.Api").Where(file => file.EndsWith("Endpoints.cs"))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\bDbException\b|\bSql\.|\bCommandText\b|\bDbDataSource\b|System\.Data"))
            .Select(Relative).ToArray();
        var unexpected = offenders.Except(EndpointStorageDebt).ToArray();
        Assert.True(unexpected.Length == 0, "Endpoints dépendant du stockage : " + string.Join(", ", unexpected));
        var stale = EndpointStorageDebt.Except(offenders).ToArray();
        Assert.True(stale.Length == 0, "Retirer de EndpointStorageDebt : " + string.Join(", ", stale));
    }

    [Fact]
    public void ContractsAndCoreAreFreeOfStorageTypes()
    {
        var offenders = SourceFiles("Encoding.Contracts").Concat(SourceFiles("Encoding.Core"))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"Microsoft\.Data|System\.Data\.Common|Microsoft\.EntityFramework"))
            .Select(Relative).ToArray();
        Assert.True(offenders.Length == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void ProcessesAreLaunchedOnlyFromDedicatedAdapters()
    {
        var offenders = AllowedProjectReferences.Keys.SelectMany(SourceFiles)
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"Process\.Start\(|new ProcessStartInfo\("))
            .Select(Relative).Except(ProcessLaunchAllowList).ToArray();
        Assert.True(offenders.Length == 0, "Lancement de processus hors adaptateur : " + string.Join(", ", offenders));
    }

    [Fact]
    public void FilesStayWithinSizeCeilings()
    {
        var offenders = new List<string>();
        foreach (var project in AllowedProjectReferences.Keys)
            foreach (var file in SourceFiles(project))
            {
                var relative = Relative(file);
                var lines = File.ReadLines(file).Count();
                var ceiling = OversizedFileCeilings.GetValueOrDefault(relative, MaxFileLines);
                if (lines > ceiling) offenders.Add($"{relative} : {lines} lignes (plafond {ceiling})");
            }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void SizeCeilingsAreNotStale()
    {
        var stale = OversizedFileCeilings.Where(entry =>
        {
            var path = Path.Combine(SourceRoot, entry.Key);
            return !File.Exists(path) || File.ReadLines(path).Count() <= MaxFileLines;
        }).Select(entry => entry.Key).ToArray();
        Assert.True(stale.Length == 0, "Retirer de OversizedFileCeilings : " + string.Join(", ", stale));
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(SourceRoot, project), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Relative(string file) => Path.GetRelativePath(SourceRoot, file).Replace('\\', '/');

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Encoding.Api");
            if (Directory.Exists(candidate)) return Path.Combine(directory.FullName, "src");
            var backend = Path.Combine(directory.FullName, "backend", "src", "Encoding.Api");
            if (Directory.Exists(backend)) return Path.Combine(directory.FullName, "backend", "src");
        }
        throw new DirectoryNotFoundException("Sources backend introuvables.");
    }
}
