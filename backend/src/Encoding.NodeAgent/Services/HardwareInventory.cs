using System.Globalization;
using System.Runtime.InteropServices;
using Encoding.Contracts.Nodes;

namespace Encoding.NodeAgent.Services;

/// <summary>
/// Inventaire matériel statique du nœud. Source unique pour l'enrôlement et les battements de cœur.
/// Le modèle de processeur et les GPU sont des informations best-effort ; leur absence n'est jamais une erreur.
/// </summary>
public static class HardwareInventory
{
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x1002"] = "AMD",
        ["0x10de"] = "NVIDIA",
        ["0x8086"] = "Intel",
    };

    public static NodeInventory Current() => Read("/proc/cpuinfo", "/sys/class/drm");

    public static NodeInventory Read(string cpuInfoPath, string drmRoot) => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount,
        Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)),
        ReadProcessorModel(cpuInfoPath),
        ReadGpus(drmRoot));

    public static string? ReadProcessorModel(string cpuInfoPath)
    {
        try
        {
            foreach (var line in File.ReadLines(cpuInfoPath))
            {
                var separator = line.IndexOf(':');
                if (separator < 0) continue;
                var key = line[..separator].Trim();
                if (key is "model name" or "Model" or "Hardware")
                {
                    var value = line[(separator + 1)..].Trim();
                    if (value.Length > 0) return value.Length > 256 ? value[..256] : value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static IReadOnlyList<NodeGpu> ReadGpus(string drmRoot)
    {
        var gpus = new List<NodeGpu>();
        try
        {
            if (!Directory.Exists(drmRoot)) return gpus;
            foreach (var card in Directory.EnumerateDirectories(drmRoot, "card*").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (Path.GetFileName(card).Contains('-')) continue; // connecteurs (card0-HDMI-A-1)
                var device = Path.Combine(card, "device");
                var vendorId = ReadTrimmed(Path.Combine(device, "vendor"));
                var deviceId = ReadTrimmed(Path.Combine(device, "device"));
                if (vendorId is null) continue;
                var vendor = Vendors.GetValueOrDefault(vendorId) ?? vendorId;
                var driver = ReadUeventValue(Path.Combine(device, "uevent"), "DRIVER");
                var name = deviceId is null ? vendor : $"{vendor} {deviceId}";
                gpus.Add(new NodeGpu(name, vendor, driver, Path.GetFileName(card)));
                if (gpus.Count == 16) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return gpus;
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            var value = File.ReadAllText(path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadUeventValue(string path, string key)
    {
        try
        {
            var prefix = key + "=";
            foreach (var line in File.ReadLines(path))
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
