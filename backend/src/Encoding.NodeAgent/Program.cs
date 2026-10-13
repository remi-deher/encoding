using Encoding.NodeAgent;
using Encoding.NodeAgent.Services;

var builder = Host.CreateApplicationBuilder(args);
var options = NodeAgentOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IToolCapabilityProbe, ToolCapabilityProbe>();
builder.Services.AddHostedService<NodeHeartbeatService>();

await builder.Build().RunAsync();
