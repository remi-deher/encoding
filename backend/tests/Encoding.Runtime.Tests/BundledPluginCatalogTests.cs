using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class BundledPluginCatalogTests
{
    [Fact]
    public void DiscoversShippedManifestAndHonorsExplicitDisable()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-bundled-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "arr", "manifests"));
            File.WriteAllText(Path.Combine(root, "arr", "plugin.py"), "# isolated plugin");
            File.WriteAllText(Path.Combine(root, "arr", "manifests", "sonarr.json"), "{\"id\":\"sonarr\",\"version\":\"1.0.0\"}");
            var values = new Dictionary<string,string?> { ["Runtime:BundledPlugins:Directory"] = root };
            var plugin = Assert.Single(BundledPluginCatalog.Scan(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
            Assert.Equal("sonarr", plugin.Id);
            Assert.Equal("sonarr", plugin.Arguments[1]);
            Assert.Equal("1.0.0", plugin.Version);
            values["Runtime:Plugins:0:Id"] = "sonarr";
            values["Runtime:Plugins:0:Version"] = "1.0.0";
            values["Runtime:Plugins:0:Enabled"] = "false";
            var options = new NodeAgentOptions("test", [], 1, Path.Combine(root, "private"), null, false, TimeSpan.FromSeconds(5), 8090);
            var host = new PluginProcessHost(new ProcessToolRunner(), new ConfigurationBuilder().AddInMemoryCollection(values).Build(), options);
            Assert.DoesNotContain(host.Installed, item => item.Id == "sonarr");
            values.Remove("Runtime:Plugins:0:Id");
            values.Remove("Runtime:Plugins:0:Version");
            values.Remove("Runtime:Plugins:0:Enabled");
            values["Runtime:BundledPlugins:sonarr:Enabled"] = "false";
            Assert.Empty(BundledPluginCatalog.Scan(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
            values["Runtime:BundledPlugins:sonarr:Enabled"] = "true";
            values["Runtime:BundledPlugins:Enabled"] = "false";
            Assert.Empty(BundledPluginCatalog.Scan(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
        }
        finally { Directory.Delete(root, true); }
    }
}
