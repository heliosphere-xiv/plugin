using System.Text.Json;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.IoC;
using Dalamud.Plugin;
using Heliosphere.Model.Generated;
using Heliosphere.Ui;
using Microsoft.Extensions.DependencyInjection;
using StrawberryShake.Serialization;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Heliosphere;

public class Plugin : IDalamudPlugin {
    public string Name => "Heliosphere";

    internal static HttpClient Client { get; } = new();
    internal static IHeliosphereClient GraphQl { get; private set; }

    internal static GameFont GameFont { get; private set; }
    internal static DalamudPluginInterface PluginInterface { get; private set; }

    [PluginService]
    internal DalamudPluginInterface Interface { get; init; }

    [PluginService]
    internal ChatGui ChatGui { get; init; }

    [PluginService]
    internal ClientState ClientState { get; init; }

    [PluginService]
    internal CommandManager CommandManager { get; init; }

    [PluginService]
    internal Framework Framework { get; init; }

    internal Configuration Config { get; }
    internal PenumbraIpc Penumbra { get; }
    internal PackageState State { get; }
    internal List<DownloadTask> Downloads { get; } = new();
    internal PluginUi PluginUi { get; }
    private UriSniffer Sniffer { get; }
    private CommandHandler CommandHandler { get; }

    public Plugin() {
        GameFont = new GameFont(this);
        PluginInterface = this.Interface!;

        this.Config = this.Interface!.GetPluginConfig() as Configuration ?? new Configuration();
        this.Penumbra = new PenumbraIpc(this);
        this.State = new PackageState(this);
        this.PluginUi = new PluginUi(this);
        this.Sniffer = new UriSniffer(this);
        this.CommandHandler = new CommandHandler(this);

        Task.Run(async () => await this.State.UpdatePackages());

        var collection = new ServiceCollection();
        collection
            .AddSerializer<FileListSerializer>()
            .AddSerializer<OptionsSerializer>()
            .AddSerializer<InstallerImageListSerializer>()
            .AddSerializer<GraphqlJsonSerializer>()
            .AddHeliosphereClient()
            .ConfigureHttpClient(client => client.BaseAddress = new Uri($"{DownloadTask.ApiBase}/api/graphql"));
        var services = collection.BuildServiceProvider();
        GraphQl = services.GetRequiredService<IHeliosphereClient>();
    }

    public void Dispose() {
        this.CommandHandler.Dispose();
        this.Sniffer.Dispose();
        this.PluginUi.Dispose();
        GameFont.Dispose();
    }

    internal void SaveConfig() {
        this.Interface.SavePluginConfig(this.Config);
    }

    internal void AddDownload(DownloadTask task) {
        this.Downloads.Add(task);
        task.Start();
    }
}

public class FileList {
    public Dictionary<string, List<List<string?>>> Files { get; init; }
}

public class FileListSerializer : ScalarSerializer<JsonElement, FileList> {
    public FileListSerializer(string typeName = "FileList") : base(typeName) {
    }

    public override FileList Parse(JsonElement serializedValue) {
        return new FileList {
            Files = serializedValue.Deserialize<Dictionary<string, List<List<string?>>>>()!,
        };
    }

    protected override JsonElement Format(FileList runtimeValue) {
        return JsonSerializer.SerializeToElement(runtimeValue.Files);
    }
}

public class OptionsSerializer : ScalarSerializer<JsonElement, Dictionary<string, List<string>>> {
    public OptionsSerializer(string typeName = "Options") : base(typeName) {
    }

    public override Dictionary<string, List<string>> Parse(JsonElement serializedValue) {
        return serializedValue.Deserialize<Dictionary<string, List<string>>>()!;
    }

    protected override JsonElement Format(Dictionary<string, List<string>> runtimeValue) {
        return System.Text.Json.JsonSerializer.SerializeToElement(runtimeValue);
    }
}

public class InstallerImageList {
    public Dictionary<string, HashSet<string>> Images { get; init; }
}

public class InstallerImageListSerializer : ScalarSerializer<JsonElement, InstallerImageList> {
    public InstallerImageListSerializer(string typeName = "InstallerImageList") : base(typeName) {
    }

    public override InstallerImageList Parse(JsonElement serializedValue) {
        return new InstallerImageList {
            Images = serializedValue.Deserialize<Dictionary<string, HashSet<string>>>()!,
        };
    }

    protected override JsonElement Format(InstallerImageList runtimeValue) {
        return JsonSerializer.SerializeToElement(runtimeValue.Images);
    }
}

public class GraphqlJsonSerializer : ScalarSerializer<JsonElement, JsonElement> {
    public GraphqlJsonSerializer(string typeName = "JSON") : base(typeName) {
    }


    public override JsonElement Parse(JsonElement serializedValue) {
        return serializedValue;
    }

    protected override JsonElement Format(JsonElement runtimeValue) {
        return runtimeValue;
    }
}
