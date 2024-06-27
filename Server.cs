using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Interface.ImGuiNotification;
using gfoidl.Base64;
using Heliosphere.Ui;
using Heliosphere.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StrawberryShake;

namespace Heliosphere;

internal partial class Server : IDisposable {
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    private const int Shift = 0x10;

    private static bool HoldingShift => (GetAsyncKeyState(Shift) & 0x8000) > 0;

    private Plugin Plugin { get; }
    private HttpListener Listener { get; }

    internal bool Listening => this.Listener.IsListening;

    private bool _disposed;

    internal Server(Plugin plugin) {
        this.Plugin = plugin;

        this.Listener = new HttpListener {
            Prefixes = { "http://localhost:27389/" },
        };

        try {
            this.StartServer();
        } catch (HttpListenerException ex) {
            ErrorHelper.Handle(ex, "Could not start HTTP server");
        }
    }

    public void Dispose() {
        this._disposed = true;
        ((IDisposable) this.Listener).Dispose();
    }

    internal void StartServer() {
        if (this.Listener.IsListening) {
            return;
        }

        new Thread(() => {
            while (!this._disposed) {
                try {
                    this.Listener.Start();
                } catch (HttpListenerException) {
                    Thread.Sleep(TimeSpan.FromSeconds(3));
                    continue;
                } catch (ObjectDisposedException) {
                    return;
                }

                // ReSharper disable RedundantJumpStatement
                while (this.Listener.IsListening) {
                    try {
                        this.HandleConnection();
                    } catch (HttpListenerException ex) when (ex.ErrorCode is 995 or 64 or 87) {
                        // 995 - I don't remember
                        // 64 - "The specified network name is no longer available."
                        //      this is the error when the other side has closed
                        // 87 - "The parameter is incorrect." - what am I
                        //      supposed to do with that?
                        continue;
                    } catch (SEHException) {
                        continue;
                    } catch (InvalidOperationException) {
                        return;
                    } catch (Exception ex) {
                        ErrorHelper.Handle(ex, "Error handling request");
                    }
                }
                // ReSharper restore RedundantJumpStatement
            }
        }).Start();
    }

    /// <summary>
    /// Read and deserialise JSON from a HTTP request to a C# type.
    /// </summary>
    /// <param name="req">Request to read from</param>
    /// <typeparam name="T">The type to attempt deserialisation to</typeparam>
    /// <returns>
    /// If the input data was
    /// <list type="bullet">
    /// <item>valid data: the deserialised object/array</item>
    /// <item>"null": null</item>
    /// <item>invalid data: null</item>
    /// </list>
    /// </returns>
    private static T? ReadJson<T>(HttpListenerRequest req) {
        try {
            using var reader = new StreamReader(req.InputStream);
            var json = reader.ReadToEnd();
            return JsonConvert.DeserializeObject<T>(json);
        } catch {
            return default;
        }
    }

    private void HandleConnection() {
        HttpListenerContext ctx;
        try {
            ctx = this.Listener.GetContext();
        } catch (HttpListenerException ex) {
            Plugin.Log.Warning(ex, "Could not get request context");
            return;
        }

        var req = ctx.Request;
        var resp = ctx.Response;
        var url = req.Url?.AbsolutePath ?? "/";
        var method = req.HttpMethod.ToLowerInvariant();

        int statusCode;
        object? response = null;

        var holdingShift = HoldingShift;

        IActiveNotification? notif = null;

        switch (url) {
            case "/install" when method == "post": {
                var info = ReadJson<InstallRequest>(req);
                if (info == null) {
                    statusCode = 400;
                    break;
                }

                var oneClick = this.OneClickPassed(info.OneClickPassword, holdingShift);

                SentrySdk.AddBreadcrumb(
                    "Processing install request",
                    "user",
                    data: new Dictionary<string, string> {
                        [nameof(info.VersionId)] = info.VersionId.ToCrockford(),
                        ["OneClickedPassed"] = oneClick.ToString(),
                    }
                );

                Task.Run(async () => {
                    if (oneClick) {
                        try {
                            if (!this.Plugin.Config.UseNotificationProgress) {
                                notif = notif.AddOrUpdate(
                                    this.Plugin.NotificationManager,
                                    type: NotificationType.Info,
                                    content: "Installing a mod...",
                                    initialDuration: TimeSpan.MaxValue
                                );
                            }

                            if (this.Plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                                await this.Plugin.AddDownloadAsync(new DownloadTask {
                                    Plugin = this.Plugin,
                                    ModDirectory = modDir,
                                    PackageId = info.PackageId,
                                    VariantId = info.VariantId,
                                    VersionId = info.VersionId,
                                    IncludeTags = this.Plugin.Config.IncludeTags,
                                    OpenInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall,
                                    PenumbraCollection = this.Plugin.Config.OneClickCollectionId,
                                    DownloadKey = info.DownloadCode,
                                    Full = true,
                                    Options = [],
                                    Notification = this.Plugin.Config.UseNotificationProgress
                                        ? notif
                                        : null,
                                });

                                if (!this.Plugin.Config.UseNotificationProgress) {
                                    notif?.DismissNow();
                                }
                            } else {
                                notif = notif.AddOrUpdate(
                                    this.Plugin.NotificationManager,
                                    type: NotificationType.Error,
                                    content: "Cannot install mod: Penumbra is not set up.",
                                    autoDuration: true
                                );
                            }
                        } catch (Exception ex) {
                            ErrorHelper.Handle(ex, "Error performing one-click install");
                            notif = notif.AddOrUpdate(
                                this.Plugin.NotificationManager,
                                type: NotificationType.Error,
                                content: "Error performing one-click install.",
                                autoDuration: true
                            );
                        }

                        return;
                    }

                    notif = notif.AddOrUpdate(
                        this.Plugin.NotificationManager,
                        type: NotificationType.Info,
                        content: "Opening mod installer, please wait...",
                        initialDuration: TimeSpan.MaxValue
                    );
                    try {
                        var window = await PromptWindow.Open(this.Plugin, info.PackageId, info.VersionId, info.DownloadCode);
                        await this.Plugin.PluginUi.AddToDrawAsync(window);
                        notif.DismissNow();
                    } catch (Exception ex) {
                        ErrorHelper.Handle(ex, "Error opening prompt window");
                        notif.Type = NotificationType.Error;
                        notif.Content = "Error opening installer prompt.";
                        notif.InitialDuration = TimeSpan.FromSeconds(5);
                    }
                });

                statusCode = 204;
                break;
            }
            case "/multi-install" when method == "post": {
                var info = ReadJson<MultiVariantInstallRequest>(req);
                if (info == null) {
                    statusCode = 400;
                    break;
                }

                var oneClick = this.OneClickPassed(info.OneClickPassword, holdingShift);

                SentrySdk.AddBreadcrumb(
                    "Processing multiple install request",
                    "user",
                    data: new Dictionary<string, string> {
                        [nameof(info.VariantIds)] = string.Join(", ", info.VariantIds.Select(v => v.ToCrockford())),
                        ["OneClickedPassed"] = oneClick.ToString(),
                    }
                );

                Task.Run(async () => {
                    if (oneClick) {
                        try {
                            if (!this.Plugin.Config.UseNotificationProgress) {
                                var plural = info.VariantIds.Length == 1 ? "" : "s";
                                notif = notif.AddOrUpdate(
                                    this.Plugin.NotificationManager,
                                    type: NotificationType.Info,
                                    content: $"Installing a mod with {info.VariantIds.Length} variant{plural}...",
                                    initialDuration: TimeSpan.MaxValue
                                );
                            }

                            var resp = await Plugin.GraphQl.MultiVariantInstall.ExecuteAsync(info.PackageId);
                            resp.EnsureNoErrors();

                            if (this.Plugin.Penumbra.TryGetModDirectory(out var modDir) && resp.Data?.Package?.Variants != null) {
                                foreach (var variant in resp.Data.Package.Variants) {
                                    if (variant.Versions.Count <= 0) {
                                        continue;
                                    }

                                    await this.Plugin.AddDownloadAsync(new DownloadTask {
                                        Plugin = this.Plugin,
                                        ModDirectory = modDir,
                                        PackageId = info.PackageId,
                                        VariantId = variant.Id,
                                        VersionId = variant.Versions[0].Id,
                                        IncludeTags = this.Plugin.Config.IncludeTags,
                                        OpenInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall && variant.Id == resp.Data.Package.Variants[0].Id,
                                        PenumbraCollection = this.Plugin.Config.OneClickCollectionId,
                                        DownloadKey = info.DownloadCode,
                                        Full = true,
                                        Options = [],
                                        Notification = this.Plugin.Config.UseNotificationProgress
                                            ? notif
                                            : null,
                                    });

                                    if (!this.Plugin.Config.UseNotificationProgress) {
                                        notif?.DismissNow();
                                    }
                                }
                            } else {
                                notif = notif.AddOrUpdate(
                                    this.Plugin.NotificationManager,
                                    type: NotificationType.Error,
                                    content: "Cannot install mod: Penumbra is not set up.",
                                    autoDuration: true
                                );
                            }
                        } catch (Exception ex) {
                            ErrorHelper.Handle(ex, "Error performing one-click install");
                            notif = notif.AddOrUpdate(
                                this.Plugin.NotificationManager,
                                type: NotificationType.Error,
                                content: "Error performing one-click install.",
                                autoDuration: true
                            );
                        }

                        return;
                    }

                    notif = notif.AddOrUpdate(
                        this.Plugin.NotificationManager,
                        type: NotificationType.Info,
                        content: "Opening mod installer, please wait...",
                        initialDuration: TimeSpan.MaxValue
                    );
                    try {
                        var window = await MultiVariantPromptWindow.Open(this.Plugin, info.PackageId, info.VariantIds, info.DownloadCode);
                        await this.Plugin.PluginUi.AddToDrawAsync(window);
                        notif.DismissNow();
                    } catch (Exception ex) {
                        ErrorHelper.Handle(ex, "Error opening prompt window");
                        notif.Type = NotificationType.Error;
                        notif.Content = "Error opening installer prompt.";
                        notif.InitialDuration = TimeSpan.FromSeconds(5);
                    }
                });

                statusCode = 204;
                break;
            }
            case "/install-multiple" when method == "post": {
                var info = ReadJson<InstallMultipleRequest>(req);
                if (info == null) {
                    statusCode = 400;
                    break;
                }

                var oneClick = this.OneClickPassed(info.OneClickPassword, holdingShift);

                SentrySdk.AddBreadcrumb(
                    "Processing install multiple request",
                    "user",
                    data: new Dictionary<string, string> {
                        ["VersionIds"] = string.Join(", ", info.Installs.Select(i => i.VersionId.ToCrockford())),
                        ["OneClickedPassed"] = oneClick.ToString(),
                    }
                );

                if (!this.Plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                    notif = notif.AddOrUpdate(
                        this.Plugin.NotificationManager,
                        type: NotificationType.Error,
                        content: "Cannot install mod: Penumbra is not set up.",
                        autoDuration: true
                    );

                    return;
                }

                Task.Run(async () => {
                    if (oneClick) {
                        if (!this.Plugin.Config.UseNotificationProgress) {
                            var plural = info.Installs.Length == 1 ? "" : "s";
                            notif = notif.AddOrUpdate(
                                this.Plugin.NotificationManager,
                                type: NotificationType.Info,
                                content: $"Installing {info.Installs.Length} mod{plural}...",
                                initialDuration: TimeSpan.MaxValue
                            );
                        }

                        foreach (var install in info.Installs) {
                            try {
                                await this.Plugin.AddDownloadAsync(new DownloadTask {
                                    Plugin = this.Plugin,
                                    ModDirectory = modDir,
                                    PackageId = install.PackageId,
                                    VariantId = install.VariantId,
                                    VersionId = install.VersionId,
                                    IncludeTags = this.Plugin.Config.IncludeTags,
                                    OpenInPenumbra = this.Plugin.Config.OpenPenumbraAfterInstall && install.VersionId == info.Installs[0].VersionId,
                                    PenumbraCollection = this.Plugin.Config.OneClickCollectionId,
                                    DownloadKey = install.DownloadCode,
                                    Full = true,
                                    Options = [],
                                    Notification = this.Plugin.Config.UseNotificationProgress
                                        ? notif
                                        : null,
                                });

                                if (!this.Plugin.Config.UseNotificationProgress) {
                                    notif?.DismissNow();
                                }
                            } catch (Exception ex) {
                                ErrorHelper.Handle(ex, "Error performing one-click install");
                                notif = notif.AddOrUpdate(
                                    this.Plugin.NotificationManager,
                                    type: NotificationType.Error,
                                    content: "Error performing one-click install.",
                                    autoDuration: true
                                );
                            }
                        }

                        return;
                    }

                    notif = notif.AddOrUpdate(
                        this.Plugin.NotificationManager,
                        type: NotificationType.Info,
                        content: "Opening mod installer, please wait...",
                        initialDuration: TimeSpan.MaxValue
                    );
                    try {
                        var window = await MultiPromptWindow.Open(this.Plugin, info.Installs);
                        await this.Plugin.PluginUi.AddToDrawAsync(window);
                        notif.DismissNow();
                    } catch (Exception ex) {
                        ErrorHelper.Handle(ex, "Error opening prompt window");
                        notif.Type = NotificationType.Error;
                        notif.Content = "Error opening installer prompt.";
                        notif.InitialDuration = TimeSpan.FromSeconds(5);
                    }
                });

                statusCode = 204;
                break;
            }
            case "/mods/installed" when method == "get": {
                var mods = this.Plugin.State.Installed.Values
                    .SelectMany(mod => mod.Variants)
                    .Select(meta => new {
                        PackageId = $"{meta.Id:N}",
                        VariantId = $"{meta.VariantId:N}",
                        VersionId = $"{meta.VersionId:N}",
                    })
                    .ToArray();

                statusCode = 200;
                response = mods;
                break;
            }
            case "/version" when method == "get": {
                statusCode = 200;
                response = new {
                    Plugin.Version,
                };
                break;
            }
            default: {
                if (method == "options") {
                    statusCode = 200;
                    break;
                }

                statusCode = 404;
                response = new {
                    Error = "not found",
                };

                break;
            }
        }

        resp.StatusCode = statusCode;
        #if LOCAL
        resp.AddHeader("Access-Control-Allow-Origin", "https://192.168.174.246");
        #else
        resp.AddHeader("Access-Control-Allow-Origin", "https://heliosphere.app");
        #endif
        resp.AddHeader("Access-Control-Allow-Headers", "Content-Type");

        if (response != null) {
            var json = JsonConvert.SerializeObject(response, Formatting.None, new JsonSerializerSettings {
                ContractResolver = new DefaultContractResolver {
                    NamingStrategy = new SnakeCaseNamingStrategy(),
                },
            });
            resp.AddHeader("content-type", "application/json");
            var buffer = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = buffer.Length;
            resp.OutputStream.Write(buffer, 0, buffer.Length);
            resp.OutputStream.Close();
        }

        resp.Close();
    }

    private bool OneClickPassed(string? providedPassword, bool holdingShift) {
        if (holdingShift || this.Plugin.Config is not { OneClick: true, OneClickHash: not null, OneClickSalt: not null } || providedPassword == null) {
            return false;
        }

        try {
            var password = Base64.Default.Decode(providedPassword);
            var hash = HashHelper.Argon2id(this.Plugin.Config.OneClickSalt, password);
            return CryptographicOperations.FixedTimeEquals(
                hash,
                Base64.Default.Decode(this.Plugin.Config.OneClickHash)
            );
        } catch (Exception ex) {
            Plugin.Log.Warning(ex, "Failed to decode one-click password");
        }

        return false;
    }

    internal static void StartInstall(
        Plugin plugin,
        bool oneClick,
        Guid packageId,
        Guid variantId,
        Guid versionId,
        string? downloadCode,
        IActiveNotification? notif = null
    ) {
        Task.Run(async () => {
            if (oneClick) {
                try {
                    if (!plugin.Config.UseNotificationProgress) {
                        notif = notif.AddOrUpdate(
                            plugin.NotificationManager,
                            type: NotificationType.Info,
                            content: "Installing a mod..."
                        );
                    }

                    if (plugin.Penumbra.TryGetModDirectory(out var modDir)) {
                        await plugin.AddDownloadAsync(new DownloadTask {
                            Plugin = plugin,
                            ModDirectory = modDir,
                            PackageId = packageId,
                            VariantId = variantId,
                            VersionId = versionId,
                            IncludeTags = plugin.Config.IncludeTags,
                            OpenInPenumbra = plugin.Config.OpenPenumbraAfterInstall,
                            PenumbraCollection = plugin.Config.OneClickCollectionId,
                            DownloadKey = downloadCode,
                            Full = true,
                            Options = [],
                            Notification = notif,
                        });
                    } else {
                        notif = notif.AddOrUpdate(
                            plugin.NotificationManager,
                            type: NotificationType.Error,
                            content: "Cannot install mod: Penumbra is not set up.",
                            initialDuration: TimeSpan.FromSeconds(5)
                        );
                    }
                } catch (Exception ex) {
                    ErrorHelper.Handle(ex, "Error performing one-click install");
                    notif = notif.AddOrUpdate(
                        plugin.NotificationManager,
                        type: NotificationType.Error,
                        content: "Error performing one-click install.",
                        initialDuration: TimeSpan.FromSeconds(5)
                    );
                }

                return;
            }

            notif = notif.AddOrUpdate(
                plugin.NotificationManager,
                type: NotificationType.Info,
                content: "Opening mod installer, please wait...",
                initialDuration: TimeSpan.MaxValue
            );
            try {
                var window = await PromptWindow.Open(plugin, packageId, versionId, downloadCode);
                await plugin.PluginUi.AddToDrawAsync(window);
                notif.DismissNow();
            } catch (Exception ex) {
                ErrorHelper.Handle(ex, "Error opening prompt window");
                notif.Type = NotificationType.Error;
                notif.Content = "Error opening installer prompt.";
                notif.InitialDuration = TimeSpan.FromSeconds(5);
            }
        });
    }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class InstallRequest {
    public Guid PackageId { get; set; }
    public Guid VariantId { get; set; }
    public Guid VersionId { get; set; }
    public string? OneClickPassword { get; set; }
    public string? DownloadCode { get; set; }

    // values to display in a temp window while grabbing metadata?
    // public string PackageName { get; set; }
    // public string VariantName { get; set; }
    // public string Version { get; set; }
    // public string AuthorName { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class MultiVariantInstallRequest {
    public Guid PackageId { get; set; }
    public Guid[] VariantIds { get; set; }
    public string? OneClickPassword { get; set; }
    public string? DownloadCode { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class InstallMultipleRequest {
    public InstallInfo[] Installs { get; set; }
    public string? OneClickPassword { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class InstallInfo {
    public Guid PackageId { get; set; }
    public Guid VariantId { get; set; }
    public Guid VersionId { get; set; }
    public string? DownloadCode { get; set; }
}
