using System.Text;
using Blake3;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using gfoidl.Base64;
using Heliosphere.Model;
using Heliosphere.Model.Generated;
using Heliosphere.Model.Penumbra;
using Heliosphere.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StrawberryShake;
using ZstdSharp;

namespace Heliosphere;

internal class DownloadTask : IDisposable {
    #if DEBUG
    internal const string ApiBase = "http://192.168.174.222:42011";
    #else
    internal const string ApiBase = "https://heliosphere.app/api";
    #endif

    private Plugin Plugin { get; }
    private string ModDirectory { get; }
    private Guid Version { get; }
    private Dictionary<string, List<string>> Options { get; }
    private bool Full { get; }
    private string? DownloadKey { get; }
    private bool IncludeTags { get; }
    private string? PenumbraModPath { get; set; }
    private string? PenumbraCollection { get; set; }
    internal string? PackageName { get; private set; }
    internal string? VariantName { get; private set; }

    internal CancellationTokenSource CancellationToken { get; } = new();
    internal State State { get; private set; } = State.NotStarted;
    internal uint StateData { get; private set; }
    internal uint StateDataMax { get; private set; }
    internal Exception? Error { get; private set; }

    private bool _disposed;
    private string? _oldModName;

    internal DownloadTask(Plugin plugin, string modDirectory, Guid version, bool includeTags, string? collection, string? downloadKey) {
        this.Plugin = plugin;
        this.ModDirectory = modDirectory;
        this.Version = version;
        this.Options = new Dictionary<string, List<string>>();
        this.Full = true;
        this.DownloadKey = downloadKey;
        this.IncludeTags = includeTags;
        this.PenumbraCollection = collection;
    }

    internal DownloadTask(Plugin plugin, string modDirectory, Guid version, Dictionary<string, List<string>> options, bool includeTags, string? collection, string? downloadKey) {
        this.Plugin = plugin;
        this.ModDirectory = modDirectory;
        this.Version = version;
        this.Options = options;
        this.DownloadKey = downloadKey;
        this.IncludeTags = includeTags;
        this.PenumbraCollection = collection;
    }

    ~DownloadTask() {
        this.Dispose();
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        GC.SuppressFinalize(this);
        this.CancellationToken.Dispose();
    }

    internal Task Start() {
        return Task.Run(this.Run, this.CancellationToken.Token);
    }

    private async Task Run() {
        try {
            var info = await this.GetPackageInfo();
            if (this.Full) {
                foreach (var group in info.Groups) {
                    this.Options[group.Name] = new List<string>();

                    foreach (var option in group.Options) {
                        this.Options[group.Name].Add(option.Name);
                    }
                }
            }

            this.PackageName = info.Variant.Package.Name;
            this.VariantName = info.Variant.Name;
            await this.DownloadFiles(info);
            await this.ConstructModPack(info);
            this.AddMod(info);
            this.RemoveOldFiles(info);
            this.State = State.Finished;
            this.Plugin.Interface.UiBuilder.AddNotification(
                $"{this.PackageName} installed in Penumbra.",
                this.Plugin.Name,
                NotificationType.Success
            );

            // refresh the manager package list after install finishes
            await this.Plugin.State.UpdatePackages();
        } catch (Exception ex) {
            this.State = State.Errored;
            this.Error = ex;
            ErrorHelper.Handle(ex, $"Error downloading version {this.Version}");
            this.Plugin.Interface.UiBuilder.AddNotification(
                $"Failed to install {this.PackageName ?? "mod"}.",
                this.Plugin.Name,
                NotificationType.Error,
                5_000
            );
        }
    }

    private void SetStateData(uint current, uint max) {
        this.StateData = current;
        this.StateDataMax = max;
    }

    internal static async Task<HttpResponseMessage> GetImage(Guid id, int imageId, CancellationToken token = default) {
        var resp = await Plugin.Client.GetAsync($"{ApiBase}/web/package/{id:N}/image/{imageId}", HttpCompletionOption.ResponseHeadersRead, token);
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    private async Task<IDownloadTask_GetVersion> GetPackageInfo() {
        this.State = State.DownloadingPackageInfo;
        this.SetStateData(0, 1);

        var resp = await Plugin.GraphQl.DownloadTask.ExecuteAsync(this.Version, this.Options, this.DownloadKey, this.Full);
        resp.EnsureNoErrors();

        if (this.DownloadKey != null) {
            this.Plugin.DownloadCodes.TryInsert(resp.Data!.GetVersion!.Variant.Package.Id, this.DownloadKey);
            this.Plugin.DownloadCodes.Save();
        }

        this.StateData += 1;
        return resp.Data!.GetVersion!;
    }

    private async Task DownloadFiles(IDownloadTask_GetVersion info) {
        this.State = State.DownloadingFiles;
        this.SetStateData(0, (uint) info.NeededFiles.Files.Files.Count);

        var directories = Directory.EnumerateDirectories(this.ModDirectory)
            .Select(Path.GetFileName)
            .Where(path => !string.IsNullOrEmpty(path))
            .Where(path => path!.StartsWith("hs-") && path.EndsWith($"-{info.Variant.Id:N}-{info.Variant.Package.Id:N}"))
            .ToArray();

        var dirName = HeliosphereMeta.ModDirectoryName(info.Variant.Package.Id, info.Variant.Package.Name, info.Version, info.Variant.Id);
        this.PenumbraModPath = Path.Join(this.ModDirectory, dirName);
        if (directories.Length == 1) {
            var oldName = Path.Join(this.ModDirectory, directories[0]!);
            if (oldName != this.PenumbraModPath) {
                this._oldModName = directories[0];
                Directory.Move(oldName, this.PenumbraModPath);
            }
        } else if (directories.Length > 1) {
            PluginLog.Warning($"multiple heliosphere mod directories found for {info.Variant.Package.Name} - not attempting a rename");
        }

        var filesPath = Path.Join(this.PenumbraModPath, "files");
        if (!await PathHelper.CreateDirectory(filesPath)) {
            throw new DirectoryNotFoundException($"Directory '{filesPath}' could not be found after waiting");
        }

        var tasks = info.NeededFiles.Files.Files
            .Select(pair => Task.Run(async () => {
                // semaphore isn't disposed until after Task.WhenAll, so this is
                // fine

                var (hash, files) = pair;
                var extensions = files
                    .Select(file => Path.GetExtension(file[2]!))
                    .ToHashSet();
                var discriminators = files
                    .Where(file => file[2]!.StartsWith("ui/"))
                    .Select(HashHelper.GetDiscriminator)
                    .ToHashSet();
                var allUi = files.Count > 0 && files.All(file => file[2]!.StartsWith("ui/"));

                if (extensions.Count == 0) {
                    // how does this happen?
                    PluginLog.LogWarning($"{hash} has no extension");
                    extensions.Add(".unk");
                }

                // ReSharper disable once AccessToDisposedClosure
                using (await SemaphoreGuard.WaitAsync(Plugin.DownloadSemaphore)) {
                    await this.DownloadFile(new Uri(info.NeededFiles.BaseUri), filesPath, extensions.ToArray(), allUi, discriminators.ToArray(), hash);
                }
            }));
        await Task.WhenAll(tasks);
    }

    private void RemoveOldFiles(IDownloadTask_GetVersion info) {
        this.State = State.RemovingOldFiles;
        this.SetStateData(0, 1);

        var filesPath = Path.Join(this.PenumbraModPath, "files");
        // FIXME: this doesn't remove unused discriminators
        // remove any old files no longer being used
        var neededHashes = info.NeededFiles.Files.Files.Keys.ToHashSet();
        var presentHashes = Directory.EnumerateFiles(filesPath)
            .Select(Path.GetFileName)
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .GroupBy(PathHelper.GetBaseName)
            .ToDictionary(group => group.Key, group => group.ToHashSet());
        var present = presentHashes.Keys.ToHashSet();
        present.ExceptWith(neededHashes);

        var total = (uint) presentHashes.Values
            .Select(set => set.Count)
            .Sum();
        this.SetStateData(0, total);

        var done = 0u;
        foreach (var extra in present) {
            foreach (var file in presentHashes[extra]) {
                var extraPath = Path.Join(filesPath, file);
                PluginLog.Log($"removing extra file {extraPath}");
                File.Delete(extraPath);

                done += 1;
                this.SetStateData(done, 1);
            }
        }
    }

    private async Task DownloadFile(Uri baseUri, string filesPath, string[] extensions, bool allUi, string[] discriminators, string hash) {
        var path = allUi
            ? Path.ChangeExtension(Path.Join(filesPath, hash), $"{discriminators[0]}{extensions[0]}")
            : Path.ChangeExtension(Path.Join(filesPath, hash), extensions[0]);

        if (File.Exists(path)) {
            // make sure checksum matches
            using var blake3 = new Blake3HashAlgorithm();
            blake3.Initialize();
            await using var file = File.OpenRead(path);
            var computed = await blake3.ComputeHashAsync(file, this.CancellationToken.Token);
            if (Base64.Url.Encode(computed) == hash) {
                // if the hash matches, don't redownload, just duplicate the
                // file as necessary
                goto Duplicate;
            }
        }

        for (var i = 0; i < 3; i++) {
            try {
                var uri = new Uri(baseUri, hash).ToString();
                using var resp = await Plugin.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, this.CancellationToken.Token);
                resp.EnsureSuccessStatusCode();

                await using var file = File.Create(path);
                var stream = await resp.Content.ReadAsStreamAsync(this.CancellationToken.Token);
                await new DecompressionStream(stream).CopyToAsync(file, this.CancellationToken.Token);
                break;
            } catch (Exception ex) {
                if (ex is DirectoryNotFoundException) {
                    await PathHelper.CreateDirectory(filesPath);
                }

                var message = $"Error downloading {baseUri}/{hash}";
                if (i == 2) {
                    // only send rethrown failures to sentry
                    ErrorHelper.Handle(ex, message);
                    // failed three times, so rethrow
                    throw;
                }

                PluginLog.LogError(ex, message);
            }
        }

        Duplicate:
        foreach (var ext in extensions) {
            // duplicate the file for each ui path discriminator
            foreach (var discriminator in discriminators) {
                if (allUi && discriminator == discriminators[0]) {
                    continue;
                }

                var uiDest = PathHelper.ChangeExtension(path, $"{discriminator}{ext}");
                if (File.Exists(uiDest)) {
                    File.Delete(uiDest);
                }

                File.Copy(path, uiDest);
            }

            // skip initial extension
            if (ext == extensions[0]) {
                continue;
            }

            // duplicate the file for each other extension it has
            var dest = PathHelper.ChangeExtension(path, ext);
            if (File.Exists(dest)) {
                File.Delete(dest);
            }

            File.Copy(path, dest);
        }

        this.StateData += 1;
    }

    private async Task ConstructModPack(IDownloadTask_GetVersion info) {
        this.State = State.ConstructingModPack;
        this.SetStateData(0, 4);
        await this.ConstructMeta(info);
        await this.ConstructHeliosphereMeta(info);
        await this.ConstructDefaultMod(info);
        await this.ConstructGroups(info);
    }

    private string GenerateModName(IDownloadTask_GetVersion info) {
        var pkgName = info.Variant.Package.Name.Replace('/', '-');
        var varName = info.Variant.Name.Replace('/', '-');
        return $"{this.Plugin.Config.TitlePrefix}{pkgName} ({varName})";
    }

    private async Task ConstructMeta(IDownloadTask_GetVersion info) {
        var meta = new ModMeta {
            Name = this.GenerateModName(info),
            Author = info.Variant.Package.User.Username,
            Description = info.Variant.Package.Description,
            Version = info.Version,
            Website = $"https://heliosphere.app/mod/{info.Variant.Package.Id.ToCrockford()}",
            ModTags = this.IncludeTags
                ? info.Variant.Package.Tags.Select(tag => tag.Slug).ToArray()
                : Array.Empty<string>(),
        };
        var json = JsonConvert.SerializeObject(meta, Formatting.Indented);

        var path = Path.Join(this.PenumbraModPath, "meta.json");
        await using var file = File.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
        this.State += 1;
    }

    private async Task ConstructHeliosphereMeta(IDownloadTask_GetVersion info) {
        var selectedAll = true;
        foreach (var group in info.Groups) {
            if (!this.Options.TryGetValue(group.Name, out var selected)) {
                selectedAll = false;
                break;
            }

            if (group.Options.All(option => selected.Contains(option.Name))) {
                continue;
            }

            selectedAll = false;
            break;
        }

        var meta = new HeliosphereMeta {
            Id = info.Variant.Package.Id,
            Name = info.Variant.Package.Name,
            Tagline = info.Variant.Package.Tagline,
            Description = info.Variant.Package.Description,
            Author = info.Variant.Package.User.Username,
            AuthorId = info.Variant.Package.User.Id,
            Variant = info.Variant.Name,
            VariantId = info.Variant.Id,
            Version = info.Version,
            VersionId = this.Version,
            FullInstall = selectedAll,
            IncludeTags = this.IncludeTags,
            SelectedOptions = this.Options,
        };

        var metaJson = JsonConvert.SerializeObject(meta, Formatting.Indented);
        var path = Path.Join(this.PenumbraModPath, "heliosphere.json");
        await using var file = File.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(metaJson), this.CancellationToken.Token);

        // save cover image
        if (info.Variant.Package.Images.Count > 0) {
            var coverImage = info.Variant.Package.Images[0];
            var coverPath = Path.Join(this.PenumbraModPath, "cover.jpg");

            try {
                using var image = await GetImage(info.Variant.Package.Id, coverImage.Id, this.CancellationToken.Token);
                await using var cover = File.Create(coverPath);
                await image.Content.CopyToAsync(cover, this.CancellationToken.Token);
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Could not download cover image");
            }
        }

        this.State += 1;
    }

    private async Task ConstructDefaultMod(IDownloadTask_GetVersion info) {
        var defaultMod = new DefaultMod {
            Manipulations = ManipTokensForOption(info.NeededFiles.Manipulations.FirstOrDefault(group => group.Name == null)?.Options, null),
        };
        foreach (var (hash, files) in info.NeededFiles.Files.Files) {
            foreach (var file in files) {
                if (file[0] != null || file[1] != null) {
                    continue;
                }

                var isUi = file[2]!.StartsWith("ui/");
                var gameExt = Path.GetExtension(file[2]);
                var replacedPath = Path.Join("files", hash);

                if (isUi) {
                    var discriminator = HashHelper.GetDiscriminator(file);
                    replacedPath = Path.ChangeExtension(replacedPath, $"{discriminator}{gameExt}");
                } else {
                    replacedPath = Path.ChangeExtension(replacedPath, gameExt);
                }

                defaultMod.Files[file[2]!] = replacedPath;
            }
        }

        var json = JsonConvert.SerializeObject(defaultMod, Formatting.Indented);

        var path = Path.Join(this.PenumbraModPath, "default_mod.json");
        await using var output = File.Create(path);
        await output.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
        this.StateData += 1;
    }

    private async Task ConstructGroups(IDownloadTask_GetVersion info) {
        // remove any groups that already exist
        var existingGroups = Directory.EnumerateFiles(this.PenumbraModPath!)
            .Where(file => {
                var name = Path.GetFileName(file);
                return name.StartsWith("group_") && name.EndsWith(".json");
            });
        foreach (var existing in existingGroups) {
            File.Delete(existing);
        }

        var modGroups = new Dictionary<string, ModGroup>(info.Groups.Count);
        foreach (var group in info.Groups) {
            var modGroup = new ModGroup(group.Name, group.Description, group.SelectionType.ToString()) {
                Priority = group.Priority,
                DefaultSettings = (uint) group.DefaultSettings,
            };
            var groupManips = info.NeededFiles.Manipulations.FirstOrDefault(manips => manips.Name == group.Name);

            foreach (var option in group.Options) {
                var manipulations = ManipTokensForOption(groupManips?.Options, option.Name);
                modGroup.Options.Add(new DefaultMod {
                    Name = option.Name,
                    Description = option.Description,
                    Priority = option.Priority,
                    Manipulations = manipulations,
                    IsDefault = option.IsDefault,
                });
            }

            modGroups[group.Name] = modGroup;
        }

        foreach (var (hash, files) in info.NeededFiles.Files.Files) {
            foreach (var file in files) {
                if (file[0] == null || file[1] == null) {
                    continue;
                }

                var groupName = file[0]!;
                var optionName = file[1]!;
                var gamePath = file[2]!;

                var modGroup = modGroups[groupName];

                var option = modGroup.Options.FirstOrDefault(opt => opt.Name == optionName);
                // this shouldn't be possible?
                if (option == null) {
                    var opt = new DefaultMod {
                        Name = optionName,
                    };

                    modGroup.Options.Add(opt);
                    option = opt;
                }

                var isUi = gamePath.StartsWith("ui/");
                var gameExt = Path.GetExtension(gamePath);
                var replacedPath = Path.Join("files", hash);

                if (isUi) {
                    var discriminator = HashHelper.GetDiscriminator(file);
                    replacedPath = Path.ChangeExtension(replacedPath, $"{discriminator}{gameExt}");
                } else {
                    replacedPath = Path.ChangeExtension(replacedPath, gameExt);
                }

                option.Files[gamePath] = replacedPath;
            }
        }

        // remove options that weren't downloaded
        foreach (var group in modGroups.Values) {
            if (this.Options.TryGetValue(group.Name, out var selected)) {
                switch (group.Type) {
                    case "Single": {
                        var enabled = group.DefaultSettings < group.Options.Count
                            ? group.Options[(int) group.DefaultSettings].Name
                            : null;

                        group.Options.RemoveAll(opt => !selected.Contains(opt.Name));

                        var idx = group.Options.FindIndex(mod => mod.Name == enabled);
                        group.DefaultSettings = idx == -1 ? 0 : (uint) idx;

                        break;
                    }
                    case "Multi": {
                        var enabled = new Dictionary<string, bool>();
                        for (var i = 0; i < group.Options.Count; i++) {
                            var option = group.Options[i];
                            enabled[option.Name] = (group.DefaultSettings & (1 << i)) > 0;
                        }

                        group.Options.RemoveAll(opt => !selected.Contains(opt.Name));
                        group.DefaultSettings = 0;

                        for (var i = 0; i < group.Options.Count; i++) {
                            var option = group.Options[i];
                            if (enabled.TryGetValue(option.Name, out var wasEnabled) && wasEnabled) {
                                group.DefaultSettings |= (uint) (1 << i);
                            }
                        }

                        break;
                    }
                }
            } else {
                group.DefaultSettings = 0;
                group.Options.Clear();
            }
        }

        // split groups that have more than 32 options
        var splitGroups = SplitGroups(modGroups.Values);

        var invalidChars = Path.GetInvalidFileNameChars();
        var list = splitGroups
            .OrderBy(group => info.Groups.FindIndex(g => g.Name == group.Name))
            .ToList();
        for (var i = 0; i < list.Count; i++) {
            var slug = list[i].Name.ToLowerInvariant()
                .Select(c => invalidChars.Contains(c) ? '-' : c)
                .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
                .ToString();
            var json = JsonConvert.SerializeObject(list[i], Formatting.Indented);
            var path = Path.Join(this.PenumbraModPath, $"group_{i + 1:000}_{slug}.json");
            await using var file = File.Create(path);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
            this.StateData += 1;
        }
    }

    private static IEnumerable<ModGroup> SplitGroups(IEnumerable<ModGroup> groups) {
        return groups.SelectMany(SplitGroup);
    }

    private static IEnumerable<ModGroup> SplitGroup(ModGroup group) {
        const int perGroup = 32;

        if (group.Type != "Multi" || group.Options.Count <= perGroup) {
            return new[] { group };
        }

        var newGroups = new List<ModGroup>();
        for (var i = 0; i < group.Options.Count; i++) {
            var option = group.Options[i];
            var groupIdx = i / perGroup;
            var optionIdx = i % perGroup;

            if (optionIdx == 0) {
                newGroups.Add(new ModGroup($"{group.Name}, Part {groupIdx + 1}", group.Description, group.Type) {
                    Priority = group.Priority,
                });
            }

            var newGroup = newGroups[groupIdx];
            newGroup.Options.Add(option);
            if (option.IsDefault) {
                newGroup.DefaultSettings |= (uint) (1 << optionIdx);
            }
        }

        return newGroups;
    }

    private static List<JToken> ManipTokensForOption(IEnumerable<IDownloadTask_GetVersion_NeededFiles_Manipulations_Options>? options, string? optionName) {
        if (options == null) {
            return new List<JToken>();
        }

        var manipulations = options
            .FirstOrDefault(opt => opt.Name == optionName)
            ?.Manipulations
            .Select(manip => {
                var token = JToken.Parse(manip.GetRawText());
                if (token is JObject jObject) {
                    var type = jObject["Type"];
                    jObject.Remove("Type");
                    jObject.AddFirst(new JProperty("Type", type));
                }

                return token;
            })
            .ToList();

        return manipulations ?? new List<JToken>();
    }

    private void AddMod(IDownloadTask_GetVersion info) {
        this.State = State.AddingMod;
        this.SetStateData(0, 1);

        this.Plugin.Framework.RunOnFrameworkThread(() => {
            string? oldPath = null;
            if (this._oldModName != null) {
                oldPath = this.Plugin.Penumbra.GetModPath(this._oldModName);
                if (oldPath != null && this.Plugin.Config.ReplaceSortName) {
                    var parts = oldPath.Split('/');
                    parts[^1] = this.GenerateModName(info);
                    oldPath = string.Join('/', parts);
                }

                this.Plugin.Penumbra.DeleteMod(this._oldModName);
            }

            var modPath = Path.GetFileName(this.PenumbraModPath!);
            if (this.Plugin.Penumbra.AddMod(modPath)) {
                // reload just in case
                this.Plugin.Penumbra.ReloadMod(modPath);

                // put mod in folder
                if (oldPath == null && !string.IsNullOrWhiteSpace(this.Plugin.Config.PenumbraFolder)) {
                    var modName = this.GenerateModName(info);
                    this.Plugin.Penumbra.SetModPath(modPath, $"{this.Plugin.Config.PenumbraFolder}/{modName}");
                } else if (oldPath != null) {
                    this.Plugin.Penumbra.SetModPath(modPath, oldPath);
                }

                if (this._oldModName != null) {
                    this.Plugin.Penumbra.CopyModSettings(this._oldModName, modPath);
                }

                if (this.PenumbraCollection != null) {
                    this.Plugin.Penumbra.TrySetMod(this.PenumbraCollection, modPath, true);
                }

                this.StateData += 1;
            } else {
                throw new Exception("could not add mod to Penumbra");
            }
        });
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    private struct DownloadOptions {
        public Dictionary<string, List<string>> Options;

        internal DownloadOptions(Dictionary<string, List<string>> options) {
            this.Options = options;
        }
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    private struct FullDownloadOptions {
        public bool Full;
    }
}

internal enum State {
    NotStarted,
    DownloadingPackageInfo,
    DownloadingFiles,
    ConstructingModPack,
    AddingMod,
    RemovingOldFiles,
    Finished,
    Errored,
}

internal static class StateExt {
    internal static string Name(this State state) {
        return state switch {
            State.NotStarted => "Not started",
            State.DownloadingPackageInfo => "Downloading package info",
            State.DownloadingFiles => "Downloading files",
            State.ConstructingModPack => "Constructing mod pack",
            State.AddingMod => "Adding mod",
            State.RemovingOldFiles => "Removing old files",
            State.Finished => "Finished",
            State.Errored => "Errored",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
    }
}
