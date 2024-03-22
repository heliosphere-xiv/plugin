using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Utility;
using Heliosphere.Model;
using Heliosphere.Util;
using ImGuiNET;
using OtterGui.Widgets;

namespace Heliosphere.Ui;

internal class PenumbraWindowIntegration {
    private Plugin Plugin { get; }

    internal PenumbraWindowIntegration(Plugin plugin) {
        this.Plugin = plugin;
    }

    private (InstalledPackage Package, HeliosphereMeta Meta)? ParseDirectory(string directory) {
        if (HeliosphereMeta.ParseDirectory(Path.GetFileName(directory)) is not { } info) {
            return null;
        }

        if (!this.Plugin.State.InstalledNoBlock.TryGetValue(info.PackageId, out var pkg)) {
            return null;
        }

        var meta = pkg.Variants.FirstOrDefault(v => v.VariantId == info.VariantId);
        if (meta == null) {
            return null;
        }

        return (pkg, meta);
    }

    internal void PreSettingsTabBarDraw(string directory, float width, float titleWidth) {
        if (this.ParseDirectory(directory) is not var (pkg, meta)) {
            return;
        }

        if (pkg.CoverImage is { } img) {
            var maxHeight = width * 0.5625f;
            ImGuiHelper.ImageFullWidth(img, maxHeight, true);

            if (ImGui.IsItemHovered()) {
                ImGui.BeginTooltip();
                using var endTooltip = new OnDispose(ImGui.EndTooltip);

                ImGui.TextUnformatted("Click to open this image. Hold right-click to zoom.");
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) {
                Process.Start(new ProcessStartInfo(pkg.CoverImagePath) {
                    UseShellExecute = true,
                });
            }

            if (ImGui.IsMouseDown(ImGuiMouseButton.Right)) {
                var winSize = ImGuiHelpers.MainViewport.WorkSize;

                Vector2 min;
                Vector2 max;

                if (img.Width > winSize.X && img.Height > winSize.Y) {
                    // determine which axis is most larger than the screen
                    var bigAxis = img.Width - winSize.X > img.Height - winSize.Y
                        ? "x"
                        : "y";

                    /*
                    img.Width    winSize.X
                    ---------- = ---------
                    img.Height   winSize.Y

                    img.Width * winSize.Y = img.Height * winSize.X
                    img.Height = img.Width * winSize.Y / winSize.X
                    img.Width = img.Height * winSize.X / winSize.Y
                    */

                    var aspectRatio = (float) img.Width / img.Height;
                    if (bigAxis == "x") {
                        var newHeight = img.Width * winSize.Y / winSize.X;
                        min = new Vector2(0, winSize.Y / 2 - newHeight / 2);
                        max = new Vector2(winSize.X, winSize.Y / 2 + newHeight / 2);
                    } else if (bigAxis == "y") {
                        var newWidth = img.Height * winSize.X / winSize.Y;
                        min = new Vector2(winSize.X / 2 - newWidth / 2, 0);
                        max = new Vector2(winSize.X / 2 + newWidth / 2, winSize.Y);
                    } else {
                        throw new Exception();
                    }
                } else if (img.Width > winSize.X) {
                    /*
                    img.Width   newHeight
                    --------- = ---------
                    winSize.X   winSize.Y

                    newHeight * winSize.X = img.Width * winSize.Y
                    newHeight = img.Width * winSize.Y / winSize.X
                    */
                    var newHeight = img.Width * winSize.Y / winSize.X;
                    var imgSize = new Vector2(winSize.X, newHeight);

                    min = new Vector2(0, winSize.Y / 2 - imgSize.Y / 2);
                    max = new Vector2(winSize.X, winSize.Y / 2 + imgSize.Y / 2);
                } else if (img.Height > winSize.Y) {
                    /*
                    newWidth    img.Height
                    --------- = ----------
                    winSize.X   winSize.Y

                    newWidth * winSize.Y = img.Height * winSize.X
                    newWidth = img.Height * winSize.X / winSize.Y
                    */
                    var newWidth = img.Height * winSize.X / winSize.Y;
                    var imgSize = new Vector2(newWidth, winSize.Y);

                    min = new Vector2(winSize.X / 2 - imgSize.X / 2, 0);
                    max = new Vector2(winSize.X / 2 + imgSize.X / 2, winSize.Y);
                } else {
                    min = new Vector2(winSize.X / 2 - img.Width / 2, winSize.Y / 2 - img.Height / 2);
                    max = new Vector2(winSize.X / 2 + img.Width / 2, winSize.Y / 2 + img.Height / 2);
                }

                ImGui.GetForegroundDrawList().AddImage(img.ImGuiHandle, min, max);
            }
        }
    }

    internal void PostEnabledDraw(string directory) {
        if (this.ParseDirectory(directory) is not var (pkg, meta)) {
            return;
        }

        Widget.BeginFramedGroup("Heliosphere");
        using (new OnDispose(() => Widget.EndFramedGroup())) {
            if (ImGui.Button("Download updates")) {
                meta.DownloadUpdates(this.Plugin);
            }
        }

        ImGui.Spacing();
    }
}
