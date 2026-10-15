using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Architecture 0043 : choix du fournisseur à l'installation.</summary>
public sealed class DatabaseProviderTests
{
    private static IConfiguration Config(string? provider, string? connection)
    {
        var values = new Dictionary<string, string?>();
        if (provider is not null) values["Database:Provider"] = provider;
        if (connection is not null) values["ConnectionStrings:Cluster"] = connection;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData(null, "Host=db;Database=encoding", "postgres")]
    [InlineData(null, "Data Source=/var/lib/encoding-node/cluster.db", "sqlite")]
    [InlineData(null, null, "postgres")]
    [InlineData("sqlite", "Host=db", "sqlite")]
    [InlineData("postgres", "Data Source=x.db", "postgres")]
    public void ProviderIsExplicitOrInferredFromTheConnectionString(string? provider, string? connection, string expected) =>
        Assert.Equal(expected, DatabaseProvider.Name(Config(provider, connection)));

    [Fact]
    public void UnknownProviderIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => DatabaseProvider.Name(Config("mysql", null)));

    [Fact]
    public async Task SqliteCreatesItsDirectoryOnFirstUse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-sqlite-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var source = new SqliteDataSource($"Data Source={Path.Combine(directory, "sub", "cluster.db")};Pooling=False");
            await using var command = source.CreateCommand("SELECT 1");
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            Assert.True(File.Exists(Path.Combine(directory, "sub", "cluster.db")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
