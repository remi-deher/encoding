using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Fact that needs a PostgreSQL server. Locally it is skipped when ENCODING_TEST_DATABASE is unset;
/// in CI ENCODING_TEST_REQUIRE_DATABASE=1 turns a missing database into a failure so tests cannot be silently skipped.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!string.IsNullOrWhiteSpace(PostgresFixture.ConnectionString)) return;
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_REQUIRE_DATABASE") == "1") return;
        Skip = "Set ENCODING_TEST_DATABASE to run PostgreSQL tests (see backend/README.md).";
    }
}

/// <summary>Fact that needs ffmpeg with libx265 and ffprobe, skipped locally when they are absent.</summary>
public sealed class MediaToolsFactAttribute : FactAttribute
{
    public MediaToolsFactAttribute()
    {
        if (!string.IsNullOrWhiteSpace(PostgresFixture.ConnectionString) is false
            && Environment.GetEnvironmentVariable("ENCODING_TEST_REQUIRE_DATABASE") != "1")
        { Skip = "Set ENCODING_TEST_DATABASE to run PostgreSQL tests (see backend/README.md)."; return; }
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_REQUIRE_MEDIA_TOOLS") == "1") return;
        if (Encoding.NodeAgent.Runtime.ProcessToolRunner.Find("ffmpeg") is null || Encoding.NodeAgent.Runtime.ProcessToolRunner.Find("ffprobe") is null)
            Skip = "ffmpeg and ffprobe are required (included in backend/tests/Dockerfile).";
    }
}

/// <summary>One isolated schema shared by the PostgreSQL scenarios; they run sequentially in the same collection.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public static string? ConnectionString => TestDatabase.ConfiguredConnection;
    internal Harness.Fixture? Scenarios { get; private set; }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var (registry, validator, eligibility) = Harness.Services();
        Scenarios = await Harness.Fixture.CreateAsync(ConnectionString, registry, validator, eligibility);
    }

    public async Task DisposeAsync()
    {
        if (Scenarios is not null) await Scenarios.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
