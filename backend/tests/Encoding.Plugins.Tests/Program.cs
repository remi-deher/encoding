using Encoding.NodeAgent.Runtime;
using Encoding.Plugins.Tests;

// Le worker relance son propre exécutable avec --isolated-process pour appliquer Landlock avant de lancer le plugin :
// l’hôte de test joue ce rôle comme le fait le point d’entrée du worker.
if (args.FirstOrDefault() == "--isolated-process") return ProcessSandbox.Run(args);


PluginTests.Run();
HardwareInventoryTests.Run();
GpuDeviceSelectorTests.Run();
await MembershipTests.RunAsync();
await ProcessTests.RunAsync();

var connection = Environment.GetEnvironmentVariable("ENCODING_TEST_DATABASE");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.WriteLine("PostgreSQL trigger tests require ENCODING_TEST_DATABASE; see README.md.");
    return 0;
}
await TriggerTests.RunAsync(connection);
Console.WriteLine("PASS: all plugin and trigger scenarios.");
return 0;
