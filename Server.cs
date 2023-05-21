using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
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

                while (this.Listener.IsListening) {
                    try {
                        this.HandleConnection();
                    } catch (HttpListenerException ex) when (ex.ErrorCode == 995) {
                        // ReSharper disable once RedundantJumpStatement
                        continue;
                    } catch (InvalidOperationException) {
                        return;
                    } catch (Exception ex) {
                        ErrorHelper.Handle(ex, "Error handling request");
                    }
                }
            }
        }).Start();
    }

    private void HandleConnection() {
        var ctx = this.Listener.GetContext();
        var req = ctx.Request;
        var resp = ctx.Response;
        var url = req.Url?.AbsolutePath ?? "/";
        var method = req.HttpMethod.ToLowerInvariant();

        int statusCode;
        object? response = null;

        var holdingShift = HoldingShift;

        switch (url) {
            case "/install" when method == "post": {
                using var reader = new StreamReader(req.InputStream);
                var json = reader.ReadToEnd();
                var info = JsonConvert.DeserializeObject<InstallRequest>(json);
                if (info == null) {
                    statusCode = 400;
                    break;
                }

                var oneClick = this.OneClickPassed(info.OneClickPassword, holdingShift);

                Task.Run(async () => {
                    if (oneClick) {
                        try {
                            this.Plugin.Interface.UiBuilder.AddNotification(
                                "Installing a mod...",
                                this.Plugin.Name,
                                NotificationType.Info
                            );
                            var modDir = this.Plugin.Penumbra.GetModDirectory();
                            if (!string.IsNullOrWhiteSpace(modDir)) {
                                this.Plugin.AddDownload(new DownloadTask(
                                    this.Plugin,
                                    modDir,
                                    info.VersionId,
                                    this.Plugin.Config.IncludeTags,
                                    this.Plugin.Config.OneClickCollection,
                                    info.DownloadCode
                                ));
                            } else {
                                this.Plugin.Interface.UiBuilder.AddNotification(
                                    "Could not ask Penumbra where its directory is.",
                                    this.Plugin.Name,
                                    NotificationType.Error
                                );
                            }
                        } catch (Exception ex) {
                            ErrorHelper.Handle(ex, "Error performing one-click install");
                            this.Plugin.Interface.UiBuilder.AddNotification(
                                "Error performing one-click install.",
                                this.Plugin.Name,
                                NotificationType.Error
                            );
                        }

                        return;
                    }

                    try {
                        this.Plugin.Interface.UiBuilder.AddNotification(
                            "Opening mod installer, please wait...",
                            this.Plugin.Name,
                            NotificationType.Info
                        );
                        var window = await PromptWindow.Open(this.Plugin, info.PackageId, info.VersionId, info.DownloadCode);
                        await this.Plugin.PluginUi.AddToDrawAsync(window);
                    } catch (Exception ex) {
                        ErrorHelper.Handle(ex, "Error opening prompt window");
                        this.Plugin.Interface.UiBuilder.AddNotification(
                            "Error opening installer prompt.",
                            this.Plugin.Name,
                            NotificationType.Error
                        );
                    }
                });

                statusCode = 204;
                break;
            }
            case "/multi-install" when method == "post": {
                using var reader = new StreamReader(req.InputStream);
                var json = reader.ReadToEnd();
                var info = JsonConvert.DeserializeObject<MultiInstallRequest>(json);
                if (info == null) {
                    statusCode = 400;
                    break;
                }

                var oneClick = this.OneClickPassed(info.OneClickPassword, holdingShift);

                Task.Run(async () => {
                    if (oneClick) {
                        try {
                            var plural = info.VariantIds.Length == 1 ? "" : "s";
                            this.Plugin.Interface.UiBuilder.AddNotification(
                                $"Installing a mod with {info.VariantIds.Length} variant{plural}...",
                                this.Plugin.Name,
                                NotificationType.Info
                            );
                            var resp = await Plugin.GraphQl.MultiInstall.ExecuteAsync(info.PackageId);
                            resp.EnsureNoErrors();

                            var modDir = this.Plugin.Penumbra.GetModDirectory();
                            if (!string.IsNullOrWhiteSpace(modDir) && resp.Data?.Package?.Variants != null) {
                                foreach (var variant in resp.Data.Package.Variants) {
                                    if (variant.Versions.Count <= 0) {
                                        continue;
                                    }

                                    this.Plugin.AddDownload(new DownloadTask(
                                        this.Plugin,
                                        modDir,
                                        variant.Versions[0].Id,
                                        this.Plugin.Config.IncludeTags,
                                        this.Plugin.Config.OneClickCollection,
                                        info.DownloadCode
                                    ));
                                }
                            } else {
                                this.Plugin.Interface.UiBuilder.AddNotification(
                                    "Could not ask Penumbra where its directory is.",
                                    this.Plugin.Name,
                                    NotificationType.Error
                                );
                            }
                        } catch (Exception ex) {
                            ErrorHelper.Handle(ex, "Error performing one-click install");
                            this.Plugin.Interface.UiBuilder.AddNotification(
                                "Error performing one-click install.",
                                this.Plugin.Name,
                                NotificationType.Error
                            );
                        }

                        return;
                    }

                    try {
                        this.Plugin.Interface.UiBuilder.AddNotification(
                            "Opening mod installer, please wait...",
                            this.Plugin.Name,
                            NotificationType.Info
                        );
                        var window = await MultiPromptWindow.Open(this.Plugin, info.PackageId, info.VariantIds, info.DownloadCode);
                        await this.Plugin.PluginUi.AddToDrawAsync(window);
                    } catch (Exception ex) {
                        ErrorHelper.Handle(ex, "Error opening prompt window");
                        this.Plugin.Interface.UiBuilder.AddNotification(
                            "Error opening installer prompt.",
                            this.Plugin.Name,
                            NotificationType.Error
                        );
                    }
                });

                statusCode = 204;
                break;
            }
            case "/version" when method == "get": {
                var version = Assembly.GetAssembly(typeof(Plugin))?
                    .GetName()
                    .Version?
                    .ToString(3);

                statusCode = 200;
                response = new {
                    Version = version,
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
        resp.AddHeader("Access-Control-Allow-Origin", "https://heliosphere.app");
        resp.AddHeader("Access-Control-Allow-Headers", "Content-Type");

        if (response != null) {
            var json = JsonConvert.SerializeObject(response, Formatting.None, new JsonSerializerSettings {
                ContractResolver = new DefaultContractResolver {
                    NamingStrategy = new SnakeCaseNamingStrategy(),
                },
            });
            resp.AddHeader("content-type", "application/json");
            resp.OutputStream.Write(Encoding.UTF8.GetBytes(json));
        }

        resp.Close();
    }

    public void Dispose() {
        this._disposed = true;
        ((IDisposable) this.Listener).Dispose();
    }

    private bool OneClickPassed(string? providedPassword, bool holdingShift) {
        if (holdingShift || this.Plugin.Config is not { OneClick: true, OneClickHash: not null, OneClickSalt: not null } || providedPassword == null) {
            return false;
        }

        try {
            var password = Base64.Default.Decode(providedPassword);
            var hash = HashHelper.Argon2id(this.Plugin.Config.OneClickSalt, password);
            return Base64.Default.Encode(hash) == this.Plugin.Config.OneClickHash;
        } catch (Exception ex) {
            PluginLog.LogWarning(ex, "Failed to decode one-click password");
        }

        return false;
    }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class InstallRequest {
    public Guid PackageId { get; set; }
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
internal class MultiInstallRequest {
    public Guid PackageId { get; set; }
    public Guid[] VariantIds { get; set; }
    public string? OneClickPassword { get; set; }
    public string? DownloadCode { get; set; }
}
