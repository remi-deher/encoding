using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowPlacementIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    private static FlowNodeDefinition Node(FlowComponentRegistry registry, string id, string componentId, Dictionary<string, JsonElement>? config = null)
    {
        var reference = registry.FindReference(componentId) ?? throw new InvalidOperationException($"Component {componentId} not found.");
        return new FlowNodeDefinition(id, id, reference, config ?? []);
    }

    private static FlowDefinition CreateChainFlow(string name, params FlowNodeDefinition[] nodes)
    {
        var connections = new List<FlowConnectionDefinition>();
        for (var i = 0; i < nodes.Length - 1; i++)
        {
            connections.Add(new FlowConnectionDefinition($"conn_{i}", nodes[i].Id, "success", nodes[i + 1].Id));
        }
        return new FlowDefinition(1, Guid.NewGuid(), 1, name, "video", nodes, connections);
    }

    private static NodeToolCapability[] StandardCapabilities(params string[] encoders) =>
    [
        new("plugin/base", "1.9.0", ["general"], []),
        new("plugin/conditions", "1.1.0", ["general"], []),
        new("ffmpeg", "7.0.0", ["video", "audio"], ["cpu"], encoders.Length > 0 ? encoders : ["libx264", "aac"]),
        new("ffprobe", "7.0.0", ["probe"], []),
        new("mkvmerge", "85.0.0", ["mkv"], [])
    ];

    [Fact]
    public async Task DirectUnifiedClaim_DirectAccessNodeClaims_AndTransferNodeSkips()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("placement-direct-unified");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var (registry, validator, eligibility) = Harness.Services();
        var presence = new NodePresenceOptions(TimeSpan.FromHours(1));
        var configs = new PluginConfigurationRepository(database.Db, new EphemeralDataProtectionProvider());
        var groups = new NodeGroupRepository(database.Db);
        var planner = new FlowPlacementPlanner(registry);
        var runtime = new RuntimeRepository(database.Db, eligibility, registry, presence, configs, groups, placementPlanner: planner);

        var libId = Guid.NewGuid();
        var nodeA = Guid.NewGuid(); // Direct node
        var nodeB = Guid.NewGuid(); // Transfer node

        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await using var cmd = Sql.Command("""
                INSERT INTO libraries(id, name, is_active) VALUES(@lib, 'TestLib', true);
                INSERT INTO cluster_nodes(id, name, roles, max_concurrent_tasks, capabilities, last_seen_at)
                VALUES(@nodeA, 'Node-A-Direct', ARRAY['worker'], 4, @capsA, clock_timestamp()),
                      (@nodeB, 'Node-B-Transfer', ARRAY['worker'], 4, @capsB, clock_timestamp());
                INSERT INTO library_node_accesses(library_id, node_id, mode, local_path, can_read, can_write, is_enabled, transfer_source_node_id)
                VALUES(@lib, @nodeA, 'direct', '/media/library', true, true, true, NULL),
                      (@lib, @nodeB, 'transfer', NULL, true, true, true, @nodeA);
                """, c, tx);
            cmd.Parameters.AddWithValue("lib", libId);
            cmd.Parameters.AddWithValue("nodeA", nodeA);
            cmd.Parameters.AddWithValue("nodeB", nodeB);
            cmd.Parameters.AddWithValue("capsA", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264", "aac"), Json));
            cmd.Parameters.AddWithValue("capsB", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264", "aac"), Json));
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        var flow = CreateChainFlow("Direct Unified Test",
            Node(registry, "input", "base-input-file"),
            Node(registry, "remux", "base-remux-media"),
            Node(registry, "complete", "base-complete-flow"));

        var execId = Guid.NewGuid();
        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var enqueued = await RuntimeRepository.EnqueueInTransactionAsync(
                new EnqueueMediaRequest(execId, libId, "movie.mkv", flow.Id, flow.Version),
                flow, c, tx, CancellationToken.None);
            Assert.True(enqueued);
            await tx.CommitAsync();
        }

        // Node B (transfer) tries to claim -> must be skipped because Node A covers everything directly
        var claimB = await runtime.ClaimAsync(nodeB, CancellationToken.None);
        Assert.Null(claimB);

        // Node A (direct) tries to claim -> must succeed
        var claimA = await runtime.ClaimAsync(nodeA, CancellationToken.None);
        Assert.NotNull(claimA);
        Assert.Equal(execId, claimA.ExecutionId);

        // Verify stored placement plan
        await using (var c = await database.Db.OpenConnectionAsync())
        {
            await using var verifyCmd = Sql.Command("SELECT placement_plan::text FROM execution_work WHERE execution_id=@id", c);
            verifyCmd.Parameters.AddWithValue("id", execId);
            var jsonText = await verifyCmd.ExecuteScalarAsync() as string;
            Assert.NotNull(jsonText);
            var plan = JsonSerializer.Deserialize<FlowPlacementPlan>(jsonText, Json);
            Assert.NotNull(plan);
            Assert.Equal(FlowPlacementPlan.StrategyDirectUnified, plan.Strategy);
            Assert.Equal(nodeA, plan.AssignedNodeId);
        }
    }

    [Fact]
    public async Task ImpossibleFlow_FailsEarly_WithoutLease()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("placement-impossible");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var (registry, validator, eligibility) = Harness.Services();
        var presence = new NodePresenceOptions(TimeSpan.FromHours(1));
        var configs = new PluginConfigurationRepository(database.Db, new EphemeralDataProtectionProvider());
        var groups = new NodeGroupRepository(database.Db);
        var planner = new FlowPlacementPlanner(registry);
        var runtime = new RuntimeRepository(database.Db, eligibility, registry, presence, configs, groups, placementPlanner: planner);

        var libId = Guid.NewGuid();
        var nodeA = Guid.NewGuid();

        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await using var cmd = Sql.Command("""
                INSERT INTO libraries(id, name, is_active) VALUES(@lib, 'TestLib', true);
                INSERT INTO cluster_nodes(id, name, roles, max_concurrent_tasks, capabilities, last_seen_at)
                VALUES(@nodeA, 'Node-A', ARRAY['worker'], 4, @capsA, clock_timestamp());
                INSERT INTO library_node_accesses(library_id, node_id, mode, local_path, can_read, can_write, is_enabled)
                VALUES(@lib, @nodeA, 'direct', '/media/library', true, true, true);
                """, c, tx);
            cmd.Parameters.AddWithValue("lib", libId);
            cmd.Parameters.AddWithValue("nodeA", nodeA);
            cmd.Parameters.AddWithValue("capsA", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264"), Json));
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        // Flow requiring a nonexistent capability
        var flow = CreateChainFlow("Impossible Flow",
            Node(registry, "input", "base-input-file"),
            Node(registry, "req", "nodes-require-capability", new()
            {
                ["toolId"] = J("nonexistent-tool"),
                ["requiredCapability"] = J("custom-hw")
            }),
            Node(registry, "complete", "base-complete-flow"));

        var execId = Guid.NewGuid();
        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var enqueued = await RuntimeRepository.EnqueueInTransactionAsync(
                new EnqueueMediaRequest(execId, libId, "movie.mkv", flow.Id, flow.Version),
                flow, c, tx, CancellationToken.None);
            Assert.True(enqueued);
            await tx.CommitAsync();
        }

        // Node A attempts to claim -> planner fails early, returning null
        var claim = await runtime.ClaimAsync(nodeA, CancellationToken.None);
        Assert.Null(claim);

        // Verify execution state was marked 'failed' with diagnostic
        await using (var c = await database.Db.OpenConnectionAsync())
        {
            await using var verifyCmd = Sql.Command("SELECT state, detail FROM executions WHERE id=@id", c);
            verifyCmd.Parameters.AddWithValue("id", execId);
            await using var r = await verifyCmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal("failed", r.GetString(0));
            Assert.Contains("Placement impossible", r.GetString(1));
            Assert.Contains("nonexistent-tool", r.GetString(1));
        }
    }

    [Fact]
    public async Task ConditionEvaluation_ElidesTranscode_AndKeepsTaskOnDirectNode()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("placement-condition-pruning");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var (registry, validator, eligibility) = Harness.Services();
        var presence = new NodePresenceOptions(TimeSpan.FromHours(1));
        var configs = new PluginConfigurationRepository(database.Db, new EphemeralDataProtectionProvider());
        var groups = new NodeGroupRepository(database.Db);
        var planner = new FlowPlacementPlanner(registry);
        var runtime = new RuntimeRepository(database.Db, eligibility, registry, presence, configs, groups, placementPlanner: planner);

        var libId = Guid.NewGuid();
        var nodeA = Guid.NewGuid(); // Direct node (CPU only)
        var nodeB = Guid.NewGuid(); // Transfer node (has hevc_nvenc)

        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await using var cmd = Sql.Command("""
                INSERT INTO libraries(id, name, is_active) VALUES(@lib, 'TestLib', true);
                INSERT INTO cluster_nodes(id, name, roles, max_concurrent_tasks, capabilities, last_seen_at)
                VALUES(@nodeA, 'Node-A-Direct', ARRAY['worker'], 4, @capsA, clock_timestamp()),
                      (@nodeB, 'Node-B-Transfer', ARRAY['worker'], 4, @capsB, clock_timestamp());
                INSERT INTO library_node_accesses(library_id, node_id, mode, local_path, can_read, can_write, is_enabled, transfer_source_node_id)
                VALUES(@lib, @nodeA, 'direct', '/media/library', true, true, true, NULL),
                      (@lib, @nodeB, 'transfer', NULL, true, true, true, @nodeA);
                """, c, tx);
            cmd.Parameters.AddWithValue("lib", libId);
            cmd.Parameters.AddWithValue("nodeA", nodeA);
            cmd.Parameters.AddWithValue("nodeB", nodeB);
            cmd.Parameters.AddWithValue("capsA", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264", "aac"), Json));
            cmd.Parameters.AddWithValue("capsB", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264", "hevc_nvenc", "aac"), Json));
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        // Flow with condition: if video is hevc, copy (base-remux-media); else transcode with hevc_nvenc
        var inputNode = Node(registry, "input", "base-input-file");
        var condNode = Node(registry, "cond", "conditions-video-codec", new()
        {
            ["matchMode"] = J("is"),
            ["codec"] = J("hevc")
        });
        var copyNode = Node(registry, "copy", "base-remux-media");
        var encodeNode = Node(registry, "encode", "video-transcode", new()
        {
            ["encoder"] = J("hevc_nvenc")
        });
        var completeNode = Node(registry, "complete", "base-complete-flow");

        var connections = new FlowConnectionDefinition[]
        {
            new("c1", "input", "success", "cond"),
            new("c2", "cond", "yes", "copy"),
            new("c3", "cond", "no", "encode"),
            new("c4", "copy", "success", "complete"),
            new("c5", "encode", "success", "complete")
        };

        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Conditional Transcode Flow", "video",
            [inputNode, condNode, copyNode, encodeNode, completeNode], connections);

        var execId = Guid.NewGuid();
        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var enqueued = await RuntimeRepository.EnqueueInTransactionAsync(
                new EnqueueMediaRequest(execId, libId, "already_hevc.mkv", flow.Id, flow.Version),
                flow, c, tx, CancellationToken.None);
            Assert.True(enqueued);

            // Simulate probe step having stored before_metadata with video.codec = "hevc"
            var beforeMeta = J(new
            {
                streams = new object[]
                {
                    new
                    {
                        codec_type = "video",
                        codec_name = "hevc",
                        pix_fmt = "yuv420p10le",
                        bits_per_raw_sample = "10",
                        width = 1920,
                        height = 1080
                    }
                }
            });
            await using var metaCmd = Sql.Command("UPDATE execution_work SET before_metadata=@meta WHERE execution_id=@id", c, tx);
            metaCmd.Parameters.AddWithValue("id", execId);
            metaCmd.Parameters.AddWithValue("meta", SqlKind.Json, beforeMeta.GetRawText());
            await metaCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }

        // Because the media is already HEVC, the transcode branch is pruned!
        // Node B (transfer) must skip:
        var claimB = await runtime.ClaimAsync(nodeB, CancellationToken.None);
        Assert.Null(claimB);

        // Node A (direct access) claims and executes locally:
        var claimA = await runtime.ClaimAsync(nodeA, CancellationToken.None);
        Assert.NotNull(claimA);
        Assert.Equal(execId, claimA.ExecutionId);

        // Verify the plan is DirectUnified
        await using (var c = await database.Db.OpenConnectionAsync())
        {
            await using var verifyCmd = Sql.Command("SELECT placement_plan::text FROM execution_work WHERE execution_id=@id", c);
            verifyCmd.Parameters.AddWithValue("id", execId);
            var jsonText = await verifyCmd.ExecuteScalarAsync() as string;
            Assert.NotNull(jsonText);
            var plan = JsonSerializer.Deserialize<FlowPlacementPlan>(jsonText, Json);
            Assert.NotNull(plan);
            Assert.Equal(FlowPlacementPlan.StrategyDirectUnified, plan.Strategy);
            Assert.Equal(nodeA, plan.AssignedNodeId);
        }
    }
}
