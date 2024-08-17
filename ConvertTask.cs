using System.Security;
using System.Text;
using Blake3;
using Dalamud.Interface.ImGuiNotification;
using gfoidl.Base64;
using Heliosphere.Model;
using Heliosphere.Util;
using Newtonsoft.Json;
using StrawberryShake;
using Windows.Win32;

namespace Heliosphere;

internal class ConvertTask {
    private HeliosphereMeta Package { get; }
    private IActiveNotification Notification { get; }

    internal ConvertTask(HeliosphereMeta pkg, IActiveNotification notification) {
        this.Package = pkg;
        this.Notification = notification;
    }

    internal async Task Run() {
        if (!Plugin.Instance.DownloadCodes.TryGetCode(this.Package.Id, out var code)) {
            code = null;
        }

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Downloading package information";
        });

        var resp = await Plugin.GraphQl.ConvertTask.ExecuteAsync(
            this.Package.VersionId,
            this.Package.SelectedOptions,
            code,
            this.Package.FullInstall,
            Model.Generated.DownloadKind.Update
        );
        resp.EnsureNoErrors();

        var neededFiles = resp.Data?.GetVersion?.NeededFiles.Files.Files;
        if (neededFiles == null) {
            throw new Exception("TODO");
        }

        if (!Plugin.Instance.Penumbra.TryGetModDirectory(out var modDirectory)) {
            throw new Exception("TODO");
        }

        var dirName = this.Package.ModDirectoryName();
        var penumbraModPath = Path.Join(modDirectory, dirName);
        var filesPath = Path.Join(penumbraModPath, "files");

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Checking existing files";
            notif.Progress = 0;
        });

        // gather a list of all files in the files directory
        var installedFiles = Directory.EnumerateFileSystemEntries(filesPath, "*", SearchOption.AllDirectories)
            .Where(path => (File.GetAttributes(path) & FileAttributes.Directory) == 0)
            .Select(Path.GetFileName)
            .Where(path => path != null)
            .Cast<string>()
            .ToHashSet();

        // create a mapping of each file and its hash (hash => path)
        var finished = 0;
        var total = installedFiles.Count;
        var hashPaths = new Dictionary<string, string>();
        using var blake3 = new Blake3HashAlgorithm();
        foreach (var path in installedFiles) {
            blake3.Initialize();
            var file = FileHelper.OpenSharedReadIfExists(Path.Join(filesPath, path));
            if (file == null) {
                continue;
            }

            var expected = PathHelper.GetBaseName(path);
            if (hashPaths.ContainsKey(expected)) {
                continue;
            }

            var rawHash = await blake3.ComputeHashAsync(file);
            var hash = Base64.Url.Encode(rawHash);
            hashPaths[hash] = path;

            finished += 1;
            this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
                notif.Progress = (float) finished / total;
            });
        }

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Converting file layout";
            notif.Progress = 0;
        });

        finished = 0;
        total = neededFiles.Count;

        // loop through all needed files and find their corresponding hash,
        // then link each output path to it
        foreach (var (hash, files) in neededFiles) {
            if (!hashPaths.TryGetValue(hash, out var existingPath)) {
                Plugin.Log.Warning($"missing a file for {hash}");
                continue;
            }

            existingPath = Path.Join(filesPath, existingPath);
            var outputPaths = DownloadTask.GetOutputPaths(files);
            foreach (var shortOutputPath in outputPaths) {
                var outputPath = Path.Join(filesPath, shortOutputPath);
                if (PathHelper.MakeRelativeSub(filesPath, Path.GetFullPath(outputPath)) == null) {
                    throw new SecurityException("path from mod was attempting to leave the files directory");
                }

                var parent = PathHelper.GetParent(outputPath);
                Directory.CreateDirectory(parent);

                if (!PInvoke.CreateHardLink(@$"\\?\{outputPath}", @$"\\?\{existingPath}")) {
                    throw new IOException($"failed to create hard link: {existingPath} -> {outputPath}");
                }
            }

            finished += 1;
            this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
                notif.Progress = (float) finished / total;
            });
        }

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Removing old files";
            notif.Progress = 0;
        });

        finished = 0;
        total = installedFiles.Count;

        // delete all previously-existing files
        foreach (var path in installedFiles) {
            File.Delete(path);

            finished += 1;
            this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
                notif.Progress = (float) total / finished;
            });
        }

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Updating package metadata";
            notif.Progress = 0;
        });

        // update package
        this.Package.FileStorageMethod = FileStorageMethod.Original;
        var json = JsonConvert.SerializeObject(this.Package, Formatting.Indented);
        var metaPath = Path.Join(penumbraModPath, "heliosphere.json");
        await using var metaFile = FileHelper.Create(metaPath);
        await metaFile.WriteAsync(Encoding.UTF8.GetBytes(json));

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Type = NotificationType.Success;
            notif.Content = "Successfully converted file layout";
            notif.InitialDuration = TimeSpan.FromSeconds(3);
        });
    }
}
