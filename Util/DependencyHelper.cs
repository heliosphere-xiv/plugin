using System.Security.Cryptography;
using Dalamud.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Heliosphere.Util;

internal static class DependencyHelper {
    private static string InternalName => typeof(Plugin).Assembly.GetName().Name ?? "heliosphere-plugin";

    private static IEnumerable<(string, Library)> NeededDependencies(DependencyInfo info) {
        var assemblyName = InternalName;

        var dict = info.Targets.First().Value;
        foreach (var (key, target) in dict) {
            if (key.StartsWith($"{assemblyName}/") || target.Runtime.Count == 0) {
                continue;
            }

            yield return (key, info.Libraries[key]);
        }
    }

    internal static async Task<bool> CheckDependencies(Plugin plugin) {
        var dllPath = Path.GetDirectoryName(plugin.GetType().Assembly.Location)!;
        var infoFilePath = Path.Join(dllPath, $"{InternalName}.deps.json");
        var infoFileJson = await File.ReadAllTextAsync(infoFilePath);
        var info = JsonConvert.DeserializeObject<DependencyInfo>(infoFileJson)!;

        var hashes = new List<string>();
        foreach (var path in Directory.EnumerateFiles(dllPath)) {
            if (Path.GetExtension(path) != ".dll") {
                continue;
            }

            await using var file = File.Open(path, FileMode.Open);
            using var hasher = SHA512.Create();
            await hasher.ComputeHashAsync(file);

            var base64 = Convert.ToBase64String(hasher.Hash!);
            hashes.Add($"sha512-{base64}");
        }

        var showWarning = false;
        foreach (var (name, dep) in NeededDependencies(info)) {
            if (hashes.Contains(dep.Sha512)) {
                continue;
            }

            PluginLog.Warning($"Missing dependency {name} with hash {dep.Sha512}");
            showWarning = true;
        }

        return showWarning;
    }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class DependencyInfo {
    public Dictionary<string, Dictionary<string, Target>> Targets { get; set; }
    public Dictionary<string, Library> Libraries { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class Target {
    internal Dictionary<string, TargetRuntime> Runtime { get; set; }
    internal Dictionary<string, string>? Dependencies { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class TargetRuntime {
    internal string AssemblyVersion { get; set; }
    internal string FileVersion { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class Library {
    internal string Type { get; set; }
    internal bool Serviceable { get; set; }
    internal string Sha512 { get; set; }
}
