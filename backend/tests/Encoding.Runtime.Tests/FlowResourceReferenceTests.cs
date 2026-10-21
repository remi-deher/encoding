using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowResourceReferenceTests
{
    [Fact]
    public void AProducedFileMustComeFromAnEarlierProducerInTheGraph()
    {
        var (registry, validator, _) = Harness.Services();
        var reference = new FlowArtifactReference("producer", 0).ToString();
        var values = new Dictionary<string, JsonElement> { ["sourceFile"] = JsonSerializer.SerializeToElement(reference) };
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Fichier produit", "video", [
            new("input", null, registry.FindReference("base-input-file")!, new Dictionary<string, JsonElement>()),
            new("producer", null, registry.FindReference("mkv-extract-chapters")!, new Dictionary<string, JsonElement>()),
            new("consumer", null, registry.FindReference("mkv-replace-chapters")!, values)
        ], [new("first", "input", "success", "producer"), new("second", "producer", "success", "consumer")]);
        Assert.True(validator.Validate(flow).IsValid);
        values["sourceFile"] = JsonSerializer.SerializeToElement(new FlowArtifactReference("input", 0).ToString());
        Assert.Contains(validator.Validate(flow).Issues, issue => issue.Code == "artifact.reference.invalid");
        values["sourceFile"] = JsonSerializer.SerializeToElement(reference);
        var reversed = flow with { Connections = [new("first", "input", "success", "consumer"), new("second", "consumer", "success", "producer")] };
        Assert.Contains(validator.Validate(reversed).Issues, issue => issue.Code == "artifact.reference.invalid");
    }

    [Fact]
    public void ProducedFilesHaveStableCheckpointReferencesAndMissingBranchesFailExplicitly()
    {
        var reference = new FlowArtifactReference("Extraire / chapitres", 0);
        Assert.True(FlowArtifactReference.TryParse(reference.ToString(), out var parsed));
        Assert.Equal(reference, parsed);
        var variables = new Dictionary<string, JsonElement>();
        Assert.Throws<IOException>(() => reference.Resolve(variables));
        variables[reference.VariableKey] = JsonSerializer.SerializeToElement(new { sha256 = new string('a', 64), name = "chapters.xml" });
        var restored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(variables))!;
        Assert.Equal("chapters.xml", reference.Resolve(restored).GetProperty("name").GetString());
        restored[reference.VariableKey] = JsonSerializer.SerializeToElement(new { sha256 = "../../outside", name = "chapters.xml" });
        Assert.Throws<IOException>(() => reference.Resolve(restored));
        Assert.False(FlowArtifactReference.TryParse("artifact:producer/../outside", out _));
        Assert.False(FlowArtifactReference.TryParse("artifact:producer/64", out _));
    }

    [Fact]
    public void FontInventoryIsBoundedAndContainsNamesRatherThanPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-font-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var index = 0; index < 70; index++) Directory.CreateDirectory(Path.Combine(directory, "Set " + index.ToString("D2")));
            Directory.CreateDirectory(Path.Combine(directory, "invalid..set"));
            var sets = FontInventory.Read(directory)!;
            Assert.Equal(64, sets.Count);
            Assert.Equal("Set 00", sets[0]);
            Assert.DoesNotContain("invalid..set", sets);
            Assert.All(sets, name => Assert.DoesNotContain(Path.DirectorySeparatorChar, name));
        }
        finally { Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory)); Directory.Delete(directory, true); }
    }
}
