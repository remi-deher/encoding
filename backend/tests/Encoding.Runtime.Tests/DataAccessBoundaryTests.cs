using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Architecture 0043 : seul le fournisseur de données connaît Npgsql.</summary>
public sealed class DataAccessBoundaryTests
{
    private static readonly string[] Allowed = ["Infrastructure/Data/Sql.cs", "Program.cs"];

    [Fact]
    public void OnlyTheDataProviderReferencesNpgsql()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Encoding.Api"))) root = root.Parent;
        Assert.NotNull(root);
        var api = Path.Combine(root!.FullName, "src", "Encoding.Api");
        var offenders = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => Path.GetRelativePath(api, path).Replace('\\', '/'))
            .Where(relative => !Allowed.Contains(relative) && File.ReadAllText(Path.Combine(api, relative)).Contains("Npgsql"))
            .ToList();
        Assert.True(offenders.Count == 0, "Npgsql référencé hors du fournisseur : " + string.Join(", ", offenders));
    }
}
