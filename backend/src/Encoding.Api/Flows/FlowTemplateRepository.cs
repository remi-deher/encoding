using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public sealed class FlowTemplateRepository(DbDataSource dataSource)
{
    public async Task<IReadOnlyList<FlowTemplateSummary>> ListAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id,name,description,media_type,source_flow_id,source_version,source_flow_name,
                   jsonb_array_length(definition->'nodes'),created_at
            FROM flow_templates ORDER BY lower(name),id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var templates = new List<FlowTemplateSummary>();
        while (await reader.ReadAsync(ct)) templates.Add(ReadSummary(reader));
        return templates;
    }

    public async Task<FlowTemplateDetails?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id,name,description,media_type,source_flow_id,source_version,source_flow_name,
                   jsonb_array_length(definition->'nodes'),created_at,definition::text
            FROM flow_templates WHERE id=@id
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var summary = ReadSummary(reader);
        var definition = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(9), FlowRepository.JsonOptions)
            ?? throw new InvalidDataException("La définition du modèle est invalide.");
        return new(summary, definition);
    }

    public async Task<FlowTemplateDetails?> CreateFromFlowAsync(
        Guid templateId,
        CreateFlowTemplateFromFlowRequest request,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = Sql.Command("""
            INSERT INTO flow_templates(
                id,name,description,source_flow_id,source_version,source_flow_name,media_type,definition)
            SELECT @templateId,@name,@description,@sourceFlowId,@sourceVersion,
                   definition->>'name',definition->>'mediaType',definition
            FROM flow_versions
            WHERE flow_id=@sourceFlowId AND version=@sourceVersion
            RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue("templateId", templateId);
        insert.Parameters.AddWithValue("name", request.Name.Trim());
        insert.Parameters.AddWithValue("description", request.Description.Trim());
        insert.Parameters.AddWithValue("sourceFlowId", request.SourceFlowId);
        insert.Parameters.AddWithValue("sourceVersion", request.SourceVersion);
        if (await insert.ExecuteScalarAsync(ct) is null) return null;
        await transaction.CommitAsync(ct);
        return await GetAsync(templateId, ct);
    }

    private static FlowTemplateSummary ReadSummary(DbDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetGuid(4), reader.GetInt32(5), reader.GetString(6), reader.GetInt32(7),
        reader.GetFieldValue<DateTimeOffset>(8));
}
