using Encoding.NodeAgent.Services;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Inventaire matériel (CPU, GPU) : lecture best-effort, jamais bloquante.</summary>
internal static class HardwareInventoryTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-hw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cpuInfo = Path.Combine(root, "cpuinfo");
            Directory.CreateDirectory(root);
            File.WriteAllText(cpuInfo, "processor\t: 0\nmodel name\t: AMD Ryzen 7 5700G\n\nprocessor\t: 1\nmodel name\t: AMD Ryzen 7 5700G\n");
            Check(HardwareInventory.ReadProcessorModel(cpuInfo) == "AMD Ryzen 7 5700G", "Le modèle de processeur est lu une fois.");
            Check(HardwareInventory.ReadProcessorModel(Path.Combine(root, "absent")) is null, "Un fichier absent donne un modèle inconnu, sans erreur.");

            var drm = Path.Combine(root, "drm");
            var device = Path.Combine(drm, "card0", "device");
            Directory.CreateDirectory(device);
            Directory.CreateDirectory(Path.Combine(drm, "card0-HDMI-A-1", "device"));
            File.WriteAllText(Path.Combine(device, "vendor"), "0x1002\n");
            File.WriteAllText(Path.Combine(device, "device"), "0x1638\n");
            File.WriteAllText(Path.Combine(device, "uevent"), "DRIVER=amdgpu\nPCI_ID=1002:1638\n");
            var gpus = HardwareInventory.ReadGpus(drm);
            Check(gpus.Count == 1, "Les connecteurs ne sont pas comptés comme des GPU.");
            Check(gpus[0].Vendor == "AMD" && gpus[0].Driver == "amdgpu" && gpus[0].Device == "card0", "Le GPU expose fabricant, pilote et périphérique.");
            Check(HardwareInventory.ReadGpus(Path.Combine(root, "absent")).Count == 0, "Sans GPU, la liste est vide.");
        }
        finally { Directory.Delete(root, true); }
    }
}
