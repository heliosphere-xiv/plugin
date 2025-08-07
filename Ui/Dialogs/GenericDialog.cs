using Dalamud.Bindings.ImGui;

namespace Heliosphere.Ui.Dialogs;

internal class GenericDialog : Dialog {
    private Func<DrawStatus> Drawer { get; }

    internal GenericDialog(string title, Func<DrawStatus> draw, ImGuiWindowFlags flags = ImGuiWindowFlags.None) : base(title, flags) {
        this.Drawer = draw;
    }

    protected override DrawStatus InnerDraw() {
        try {
            return this.Drawer();
        } catch (Exception ex) {
            Plugin.Log.Error(ex, $"Failed to draw generic dialog \"{this.Title}\"");
            return DrawStatus.Finished;
        }
    }
}
