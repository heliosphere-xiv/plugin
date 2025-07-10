using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Style;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Heliosphere.Ui;
using ImGuiNET;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Heliosphere.Util;

internal static class ImGuiHelper {
    private static readonly ImGuiRenderer Renderer = new();

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    internal static bool IconButton(FontAwesomeIcon icon, string? id = null, string? tooltip = null) {
        var label = icon.ToIconString();
        if (id != null) {
            label += $"##{id}";
        }

        ImGui.PushFont(UiBuilder.IconFont);
        var ret = ImGui.Button(label);
        ImGui.PopFont();

        if (tooltip != null) {
            Tooltip(tooltip);
        }

        return ret;
    }

    internal static void Tooltip(string text, ImGuiHoveredFlags flags = ImGuiHoveredFlags.None) {
        if (!ImGui.IsItemHovered(flags)) {
            return;
        }

        var w = ImGui.CalcTextSize("m").X;
        ImGui.BeginTooltip();
        using var endTooltip = new OnDispose(ImGui.EndTooltip);

        ImGui.PushTextWrapPos(w * 40);
        using var popTextWrapPos = new OnDispose(ImGui.PopTextWrapPos);

        ImGui.TextUnformatted(text);
    }

    internal static void Help(string text) {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled]);
        try {
            ImGui.TextUnformatted(FontAwesomeIcon.QuestionCircle.ToIconString());
        } finally {
            ImGui.PopStyleColor();
            ImGui.PopFont();
        }

        Tooltip(text);
    }

    internal static void TextUnformattedSize(string text, uint size) {
        using var font = size == 0 ? null : Plugin.GameFont.WithFont(size);
        ImGui.TextUnformatted(text);
    }

    internal static void TextUnformattedColour(string text, ImGuiCol colour) {
        TextUnformattedColour(text, ImGui.GetStyle().Colors[(int) colour]);
    }

    internal static void TextUnformattedColour(string text, Vector4 colour) {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        using var pop = new OnDispose(ImGui.PopStyleColor);

        ImGui.TextUnformatted(text);
    }

    internal static OnDispose TextWrap(float? pos = null) {
        if (pos == null) {
            ImGui.PushTextWrapPos();
        } else {
            ImGui.PushTextWrapPos(pos.Value);
        }

        return new OnDispose(ImGui.PopTextWrapPos);
    }

    internal static DalamudColors? DalamudStyle() {
        var model = StyleModel.GetConfiguredStyle() ?? StyleModel.GetFromCurrent();
        return model.BuiltInColors;
    }

    internal static OnDispose? PushColor(ImGuiCol idx, Vector4? colour) {
        if (colour == null) {
            return null;
        }

        ImGui.PushStyleColor(idx, colour.Value);
        return new OnDispose(ImGui.PopStyleColor);
    }

    internal static void ImageFullWidth(IDalamudTextureWrap wrap, float maxHeight = 0f, bool centred = false) {
        // get the available area
        var widthAvail = centred && ImGui.GetScrollMaxY() == 0
            ? ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize
            : ImGui.GetContentRegionAvail().X;
        widthAvail = Math.Max(0, widthAvail);

        // set max height to image height if unspecified
        if (maxHeight == 0f) {
            maxHeight = wrap.Height;
        }

        // clamp height at the actual image height
        maxHeight = Math.Min(wrap.Height, maxHeight);

        // for the width, either use the whole space available
        // or the actual image's width, whichever is smaller
        var width = widthAvail == 0
            ? wrap.Width
            : Math.Min(widthAvail, wrap.Width);
        // determine the ratio between the actual width and the
        // image's width and multiply the image's height by that
        // to determine the height
        var height = wrap.Height * (width / wrap.Width);

        // check if the height is greater than the max height,
        // in which case we'll have to scale the width down
        if (height > maxHeight) {
            width *= maxHeight / height;
            height = maxHeight;
        }

        if (centred && width < widthAvail) {
            var cursor = ImGui.GetCursorPos();
            ImGui.SetCursorPos(cursor with {
                X = widthAvail / 2 - width / 2,
            });
        }

        ImGui.Image(wrap.ImGuiHandle, new Vector2(width, height));
    }

    internal static void TextUnformattedCentred(string text, float size = 0) {
        var widthAvail = ImGui.GetContentRegionAvail().X;
        using var titleFont = size == 0 ? null : Plugin.GameFont.WithFont(size);

        var textSize = ImGui.CalcTextSize(text);
        if (textSize.X < widthAvail) {
            var cursor = ImGui.GetCursorPos();
            ImGui.SetCursorPos(cursor with {
                X = widthAvail / 2 - textSize.X / 2,
            });
        }

        ImGui.TextUnformatted(text);
    }

    internal static bool FullWidthButton(string label) {
        return ImGui.Button(
            label,
            ImGui.GetContentRegionAvail() with {
                Y = 0,
            }
        );
    }

    internal static bool CentredWideButton(string label) {
        var avail = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(label).X;

        var buttonSizeBase = Math.Max(avail / 2, textSize);
        var buttonWidth = buttonSizeBase + ImGui.GetStyle().FramePadding.X * 2;
        var buttonStart = avail / 2 - buttonWidth / 2;

        var cursor = ImGui.GetCursorPos();
        cursor.X = buttonStart;
        ImGui.SetCursorPos(cursor);
        return ImGui.Button(label, new Vector2(buttonWidth, 0));
    }

    internal static bool WideButton(string label) {
        var avail = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(label).X;

        var buttonSizeBase = Math.Max(avail / 2, textSize);
        var buttonWidth = buttonSizeBase + ImGui.GetStyle().FramePadding.X * 2;

        return ImGui.Button(label, new Vector2(buttonWidth, 0));
    }

    internal static void FullWidthProgressBar(float ratio, string? overlay = null) {
        if (overlay == null) {
            ImGui.ProgressBar(
                ratio,
                ImGui.GetContentRegionAvail() with {
                    Y = 25 * ImGuiHelpers.GlobalScale,
                }
            );
        } else {
            ImGui.ProgressBar(
                ratio,
                ImGui.GetContentRegionAvail() with {
                    Y = 25 * ImGuiHelpers.GlobalScale,
                },
                overlay
            );
        }
    }

    public static float BeginFramedGroup(string label, string description = "", uint headerColor = 0, FontAwesomeIcon headerPreSymbol = FontAwesomeIcon.None) {
        return BeginFramedGroupInternal(label, Vector2.Zero, description, headerColor, headerPreSymbol);
    }

    public static float BeginFramedGroup(string label, Vector2 minSize, string description = "", uint headerColor = 0, FontAwesomeIcon headerPreSymbol = FontAwesomeIcon.None) {
        return BeginFramedGroupInternal(label, minSize, description, headerColor, headerPreSymbol);
    }

    private static float BeginFramedGroupInternal(string label, Vector2 minSize, string description, uint headerColor, FontAwesomeIcon headerPreSymbol) {
        var itemSpacing = ImGui.GetStyle().ItemSpacing;
        var frameHeight = ImGui.GetFrameHeight();
        var halfFrameHeight = new Vector2(ImGui.GetFrameHeight() / 2, 0);
        var startPoint = ImGui.GetCursorScreenPos().X + halfFrameHeight.X;

        ImGui.BeginGroup(); // First group

        Vector2 labelMin;
        Vector2 labelMax;
        Vector2 effectiveSize;

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using (new OnDispose(() => ImGui.PopStyleVar(2))) {
            ImGui.BeginGroup(); // Second group

            effectiveSize = minSize;
            if (effectiveSize.X < 0) {
                effectiveSize.X = ImGui.GetContentRegionAvail().X;
            }

            // Ensure width.
            ImGui.Dummy(Vector2.UnitX * effectiveSize.X);
            // Ensure left half boundary width/distance.
            ImGui.Dummy(halfFrameHeight);

            ImGui.SameLine();
            ImGui.BeginGroup(); // Third group.
            // Ensure right half of boundary width/distance
            ImGui.Dummy(halfFrameHeight);

            // Label block
            ImGui.SameLine();
            ImGui.BeginGroup();
            using (new OnDispose(ImGui.EndGroup)) {
                using var popColor = headerColor != 0
                    ? new OnDispose(ImGui.PopStyleColor)
                    : null;
                if (headerColor != 0) {
                    ImGui.PushStyleColor(ImGuiCol.Text, headerColor);
                }

                if (headerPreSymbol is not FontAwesomeIcon.None) {
                    ImGui.PushFont(UiBuilder.IconFont);
                    using var popFont = new OnDispose(ImGui.PopFont);
                    ImGui.TextUnformatted(headerPreSymbol.ToIconString());
                    ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                }

                ImGui.TextUnformatted(label);

                if (description.Length > 0) {
                    ImGui.SameLine(0, itemSpacing.X);
                    ImGuiComponents.HelpMarker(description);
                }
            }

            labelMin = ImGui.GetItemRectMin();
            labelMax = ImGui.GetItemRectMax();
            ImGui.SameLine();
            // Ensure height and distance to label.
            ImGui.Dummy(Vector2.UnitY * (frameHeight + itemSpacing.Y));

            ImGui.BeginGroup(); // Fourth Group.
        }

        var itemWidth = ImGui.CalcItemWidth();
        ImGui.PushItemWidth(Math.Max(0f, itemWidth - frameHeight));

        LabelStack.Push((labelMin, labelMax));
        return Math.Max(effectiveSize.X, labelMax.X - startPoint);
    }

    private static void DrawClippedRect(Vector2 clipMin, Vector2 clipMax, Vector2 drawMin, Vector2 drawMax, uint color, float thickness) {
        ImGui.PushClipRect(clipMin, clipMax, true);
        ImGui.GetWindowDrawList().AddRect(drawMin, drawMax, color, ImGui.GetStyle().FrameRounding, ImDrawFlags.RoundCornersAll, thickness);
        ImGui.PopClipRect();
    }

    public static void EndFramedGroup(uint borderColor = 0) {
        if (borderColor == 0) {
            borderColor = ImGui.GetColorU32(ImGuiCol.Border);
        }

        var itemSpacing = ImGui.GetStyle().ItemSpacing;
        var frameHeight = ImGui.GetFrameHeight();
        var halfFrameHeight = new Vector2(ImGui.GetFrameHeight() / 2, 0);
        var (currentLabelMin, currentLabelMax) = LabelStack.Pop();

        ImGui.PopItemWidth();

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using (new OnDispose(() => ImGui.PopStyleVar(2))) {
            ImGui.EndGroup(); // Close fourth group
            ImGui.EndGroup(); // Close third group

            ImGui.SameLine();
            // Ensure right distance.
            ImGui.Dummy(halfFrameHeight);
            // Ensure bottom distance
            ImGui.Dummy(Vector2.UnitY * (frameHeight / 2 - itemSpacing.Y));
            ImGui.EndGroup(); // Close second group

            var itemMin = ImGui.GetItemRectMin();
            var itemMax = ImGui.GetItemRectMax();
            var halfFrame = new Vector2(frameHeight / 8, frameHeight / 2);
            var frameMin = itemMin + halfFrame;
            var frameMax = itemMax - Vector2.UnitX * halfFrame.X;
            currentLabelMin.X -= itemSpacing.X;
            currentLabelMax.X += itemSpacing.X;
            var thickness = 2 * ImGui.GetStyle().ChildBorderSize;

            // Left
            DrawClippedRect(
                new Vector2(-float.MaxValue, -float.MaxValue),
                currentLabelMin with { Y = float.MaxValue },
                frameMin,
                frameMax,
                borderColor,
                thickness
            );

            // Right
            DrawClippedRect(
                currentLabelMax with { Y = -float.MaxValue },
                new Vector2(float.MaxValue, float.MaxValue),
                frameMin,
                frameMax,
                borderColor,
                thickness
            );

            // Top
            DrawClippedRect(
                currentLabelMin with { Y = -float.MaxValue },
                new Vector2(currentLabelMax.X, currentLabelMin.Y),
                frameMin,
                frameMax,
                borderColor,
                thickness
            );

            // Bottom
            DrawClippedRect(
                new Vector2(currentLabelMin.X, currentLabelMax.Y),
                currentLabelMax with { Y = float.MaxValue },
                frameMin,
                frameMax,
                borderColor,
                thickness
            );
        }

        // This seems wrong?
        // ImGui.SetWindowSize( new Vector2( ImGui.GetWindowSize().X + frameHeight, ImGui.GetWindowSize().Y ) );
        ImGui.Dummy(Vector2.Zero);

        ImGui.EndGroup(); // Close first group
    }

    internal static bool ChooseYesNo(string label, out bool choice, string? id = null, string yesLabel = "Yes", string noLabel = "No") {
        id ??= label;
        choice = false;

        var changed = false;

        using var endGroup = new OnDispose(ImGui.EndGroup);
        ImGui.BeginGroup();

        ImGuiHelper.TextUnformattedCentred(label);

        var widthAvail = ImGui.GetContentRegionAvail().X;
        var buttonWidth = widthAvail * 0.25f;
        var combinedWidth = 2f * buttonWidth + ImGui.GetStyle().ItemSpacing.X;

        ImGui.SetCursorPosX(widthAvail / 2f - combinedWidth / 2f);

        if (ImGui.Button($"{yesLabel}##{id}", new Vector2(buttonWidth, 0))) {
            choice = true;
            changed = true;
        }

        ImGui.SameLine();

        if (ImGui.Button($"{noLabel}##{id}", new Vector2(buttonWidth, 0))) {
            choice = false;
            changed = true;
        }

        return changed;
    }

    internal static bool BooleanYesNo(string label, ref bool option, string? id = null, string yesLabel = "Yes", string noLabel = "No") {
        id ??= label;

        var changed = false;

        using var endGroup = new OnDispose(ImGui.EndGroup);
        ImGui.BeginGroup();

        ImGuiHelper.TextUnformattedCentred(label);

        // var yesSize = ImGuiHelpers.GetButtonSize(yesLabel);
        // var noSize = ImGuiHelpers.GetButtonSize(noLabel);

        var widthAvail = ImGui.GetContentRegionAvail().X;
        var buttonWidth = widthAvail * 0.25f;
        var combinedWidth = 2f * buttonWidth + ImGui.GetStyle().ItemSpacing.X;

        ImGui.SetCursorPosX(widthAvail / 2f - combinedWidth / 2f);

        using (ImGuiHelper.DisabledIf(option)) {
            if (ImGui.Button($"{yesLabel}##{id}", new Vector2(buttonWidth, -1))) {
                option = true;
                changed = true;
            }
        }

        ImGui.SameLine();

        using (ImGuiHelper.DisabledIf(!option)) {
            if (ImGui.Button($"{noLabel}##{id}", new Vector2(buttonWidth, -1))) {
                option = false;
                changed = true;
            }
        }

        return changed;
    }

    private static readonly Stack<(Vector2, Vector2)> LabelStack = new();


    [SuppressMessage("ReSharper", "AccessToModifiedClosure")]
    internal static unsafe void WrapText(string csText, float lineWidth, Action? onClick = null, Action? onHover = null) {
        if (csText.Length == 0) {
            return;
        }

        foreach (var part in csText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None)) {
            var bytes = Encoding.UTF8.GetBytes(part);
            fixed (byte* rawText = bytes) {
                var text = rawText;
                var textEnd = text + bytes.Length;

                // empty string
                if (text == null) {
                    ImGui.Dummy(Vector2.Zero);
                    continue;
                }

                var widthLeft = ImGui.GetContentRegionAvail().X;
                var endPrevLine = ImGuiNative.ImFont_CalcWordWrapPositionA(ImGui.GetFont().NativePtr, ImGuiHelpers.GlobalScale, text, textEnd, widthLeft);
                if (endPrevLine == null) {
                    continue;
                }

                var firstSpace = FindFirstSpace(text, textEnd);
                var properBreak = firstSpace <= endPrevLine;
                if (properBreak) {
                    WithActions(onClick, onHover, () => ImGuiNative.igTextUnformatted(text, endPrevLine));
                } else {
                    if (lineWidth == 0f) {
                        ImGui.Dummy(Vector2.Zero);
                    } else {
                        // check if the next bit is longer than the entire line width
                        var wrapPos = ImGuiNative.ImFont_CalcWordWrapPositionA(ImGui.GetFont().NativePtr, ImGuiHelpers.GlobalScale, text, firstSpace, lineWidth);
                        if (wrapPos >= firstSpace) {
                            // only go to next line if it's going to wrap at the space
                            ImGui.Dummy(Vector2.Zero);
                        }
                    }
                }

                widthLeft = ImGui.GetContentRegionAvail().X;
                while (endPrevLine < textEnd) {
                    if (properBreak) {
                        text = endPrevLine;
                    }

                    if (*text == ' ') {
                        ++text;
                    } // skip a space at start of line

                    var newEnd = ImGuiNative.ImFont_CalcWordWrapPositionA(ImGui.GetFont().NativePtr, ImGuiHelpers.GlobalScale, text, textEnd, widthLeft);
                    if (properBreak && newEnd == endPrevLine) {
                        break;
                    }

                    endPrevLine = newEnd;
                    if (endPrevLine == null) {
                        ImGui.Dummy(Vector2.Zero);
                        ImGui.Dummy(Vector2.Zero);
                        break;
                    }

                    WithActions(onClick, onHover, () => ImGuiNative.igTextUnformatted(text, endPrevLine));

                    if (!properBreak) {
                        properBreak = true;
                        widthLeft = ImGui.GetContentRegionAvail().X;
                    }
                }
            }
        }
    }

    private static unsafe byte* FindFirstSpace(byte* text, byte* textEnd) {
        for (var i = text; i < textEnd; i++) {
            if (char.IsWhiteSpace((char) *i)) {
                return i;
            }
        }

        return textEnd;
    }

    internal static void Markdown(string text) {
        var document = Markdig.Markdown.Parse(text, Pipeline);
        Renderer.Render(document);
    }

    private static void WithActions(Action? onClick, Action? onHover, Action action) {
        action();

        if (onHover != null && ImGui.IsItemHovered()) {
            onHover();
        }

        if (onClick != null && ImGui.IsItemClicked()) {
            onClick();
        }
    }

    internal static bool InputTextVertical(string title, string id, ref string input, uint max, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, string? help = null) {
        ImGui.TextUnformatted(title);
        if (help != null) {
            ImGui.SameLine();
            Help(help);
        }

        ImGui.SetNextItemWidth(-1);
        return ImGui.InputText(id, ref input, max, flags);
    }

    internal static bool InputULongVertical(string title, string id, ref ulong input) {
        ImGui.TextUnformatted(title);
        ImGui.SetNextItemWidth(-1);
        var text = input.ToString();
        if (!ImGui.InputText(id, ref text, 100, ImGuiInputTextFlags.CharsDecimal)) {
            return false;
        }

        if (!ulong.TryParse(text, out var parsed)) {
            return false;
        }

        input = parsed;
        return true;
    }

    internal static bool CollectionChooser(PenumbraIpc penumbra, string label, ref Guid? collectionId) {
        var anyChanged = false;

        if (penumbra.GetCollections() is not { } collections) {
            return false;
        }

        ImGui.SetNextItemWidth(-1);
        var preview = collectionId == null
            ? "<none>"
            : collections.TryGetValue(collectionId.Value, out var selectedName)
                ? selectedName
                : "<deleted>";
        if (ImGui.BeginCombo(label, preview)) {
            if (ImGui.Selectable("<none>", collectionId == null)) {
                collectionId = null;
                anyChanged = true;
            }

            ImGui.Separator();

            foreach (var (id, name) in collections) {
                if (!ImGui.Selectable(name, collectionId == id)) {
                    continue;
                }

                collectionId = id;
                anyChanged = true;
            }

            ImGui.EndCombo();
        }

        return anyChanged;
    }

    internal static OnDispose DisabledUnless(bool unless) {
        return DisabledIf(!unless);
    }

    internal static OnDispose DisabledIf(bool disabled) {
        if (disabled) {
            ImGui.BeginDisabled();
        }

        return new OnDispose(() => {
            if (disabled) {
                ImGui.EndDisabled();
            }
        });
    }

    internal static OnDispose WithId(string id) {
        ImGui.PushID(id);

        return new OnDispose(ImGui.PopID);
    }

    internal static unsafe bool BeginTabItem(string label, bool forceOpen = false) {
        var flags = forceOpen
            ? ImGuiTabItemFlags.SetSelected
            : ImGuiTabItemFlags.None;

        var bufSize = Encoding.UTF8.GetByteCount(label);
        var labelBuf = stackalloc byte[bufSize + 1];
        fixed (char* labelPtr = label) {
            Encoding.UTF8.GetBytes(labelPtr, label.Length, labelBuf, bufSize);
        }

        labelBuf[bufSize] = 0;

        return ImGuiNative.igBeginTabItem(labelBuf, null, flags) > 0u;
    }

    internal static bool BeginTab(PluginUi ui, PluginUi.Tab tab) {
        var label = tab switch {
            PluginUi.Tab.Manager => "Manager",
            PluginUi.Tab.LatestUpdate => "Latest update",
            PluginUi.Tab.DownloadHistory => "Downloads",
            PluginUi.Tab.Settings => "Settings",
            _ => throw new ArgumentOutOfRangeException(nameof(tab), tab, null),
        };

        return BeginTabItem(label, ui.ShouldForceOpen(tab));
    }

    internal static void TextUnformattedDisabled(string text) {
        var disabledColour = ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled];
        TextUnformattedColour(text, disabledColour);
    }

    internal static OnDispose WithFont(ImFontPtr font) {
        ImGui.PushFont(font);
        return new OnDispose(ImGui.PopFont);
    }

    internal static OnDispose? WithWarningColour() {
        var model = StyleModel.GetConfiguredStyle() ?? StyleModel.GetFromCurrent();
        var orange = model.BuiltInColors?.DalamudOrange;

        if (orange == null) {
            return null;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, orange.Value);
        return new OnDispose(ImGui.PopStyleColor);
    }
}

internal class OnDispose : IDisposable {
    private Action Action { get; }
    private bool _disposed;

    internal OnDispose(Action action) {
        this.Action = action;
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        this.Action();
    }
}

internal class ImGuiRenderer : RendererBase {
    private float _verticalSpacing;
    private bool _lastWasInline;
    private Action? _onHover;
    private Action? _onClick;
    private bool _ignoreInline;
    private bool _addedSpacing;

    internal ImGuiRenderer() {
        this.ObjectRenderers.AddRange(new IMarkdownObjectRenderer[] {
            // blocks
            new CodeBlockRenderer(),
            new HeadingBlockRenderer(),
            new ListRenderer(),
            new ParagraphRenderer(),
            new QuoteBlockRenderer(),
            new ThematicBreakRenderer(),

            // inlines
            new AutolinkInlineRenderer(),
            new CodeInlineRenderer(),
            new EmphasisInlineRenderer(),
            new LineBreakInlineRenderer(),
            new LinkInlineRenderer(),
            new LiteralInlineRenderer(),
        });

        this.ObjectWriteBefore += this.BeforeWrite;
        this.ObjectWriteAfter += this.AfterWrite;
    }

    private void AfterWrite(IMarkdownRenderer _, MarkdownObject obj) {
        if (obj is not Block) {
            this._addedSpacing = false;
            return;
        }

        if (this._addedSpacing) {
            return;
        }

        this._addedSpacing = true;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, this._verticalSpacing));
        try {
            ImGui.Spacing();
        } finally {
            ImGui.PopStyleVar();
        }
    }

    private void BeforeWrite(IMarkdownRenderer _, MarkdownObject obj) {
        var isInline = obj is Inline && obj.GetType() != typeof(ContainerInline);
        if (!this._ignoreInline && this._lastWasInline && isInline) {
            ImGui.SameLine();
        }

        this._lastWasInline = isInline;
    }

    public override object Render(MarkdownObject obj) {
        this._verticalSpacing = ImGui.GetStyle().ItemSpacing.Y;
        this._lastWasInline = false;
        this._addedSpacing = false;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        try {
            this.Write(obj);
        } finally {
            ImGui.PopStyleVar();
        }

        return null!;
    }

    private void WriteLeafBlock(LeafBlock leafBlock) {
        var slices = leafBlock.Lines.Lines;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (slices == null) {
            return;
        }

        for (var i = 0; i < slices.Length; i++) {
            ref var slice = ref slices[i].Slice;
            if (slice.Text is null) {
                break;
            }

            this.Write(new LiteralInline(slice));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteLeafInline(LeafBlock leafBlock) {
        Inline? inline = leafBlock.Inline;

        while (inline != null) {
            this.Write(inline);
            inline = inline.NextSibling;
        }
    }

    private class CodeBlockRenderer : MarkdownObjectRenderer<ImGuiRenderer, CodeBlock> {
        protected override void Write(ImGuiRenderer renderer, CodeBlock obj) {
            ImGui.PushFont(UiBuilder.MonoFont);
            renderer._ignoreInline = true;
            try {
                renderer.WriteLeafBlock(obj);
            } finally {
                renderer._ignoreInline = false;
                ImGui.PopFont();
            }
        }
    }

    private class ListRenderer : MarkdownObjectRenderer<ImGuiRenderer, ListBlock> {
        protected override void Write(ImGuiRenderer renderer, ListBlock obj) {
            if (obj.IsOrdered) {
                for (var i = 0; i < obj.Count; i++) {
                    var item = (ListItemBlock) obj[i];
                    ImGui.TextUnformatted($"{i + 1}{obj.OrderedDelimiter} ");
                    renderer._lastWasInline = true;
                    ImGui.SameLine();
                    renderer.WriteChildren(item);
                }
            } else {
                foreach (var item in obj) {
                    ImGui.TextUnformatted("• ");
                    renderer._lastWasInline = true;
                    ImGui.SameLine();
                    renderer.WriteChildren((ListItemBlock) item);
                }
            }
        }
    }

    private class ParagraphRenderer : MarkdownObjectRenderer<ImGuiRenderer, ParagraphBlock> {
        protected override void Write(ImGuiRenderer renderer, ParagraphBlock obj) {
            renderer.WriteLeafInline(obj);
        }
    }

    private class ThematicBreakRenderer : MarkdownObjectRenderer<ImGuiRenderer, ThematicBreakBlock> {
        protected override void Write(ImGuiRenderer renderer, ThematicBreakBlock obj) {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, renderer._verticalSpacing));
            try {
                ImGui.Separator();
            } finally {
                ImGui.PopStyleVar();
            }
        }
    }

    private class QuoteBlockRenderer : MarkdownObjectRenderer<ImGuiRenderer, QuoteBlock> {
        protected override void Write(ImGuiRenderer renderer, QuoteBlock obj) {
            ImGui.Indent();
            try {
                renderer.WriteChildren(obj);
            } finally {
                ImGui.Unindent();
            }
        }
    }

    private class HeadingBlockRenderer : MarkdownObjectRenderer<ImGuiRenderer, HeadingBlock> {
        protected override void Write(ImGuiRenderer renderer, HeadingBlock obj) {
            var baseSize = Plugin.Instance.PluginUi.BaseSize;
            var range = Plugin.Instance.PluginUi.TitleSize - baseSize;
            var fontSize = Math.Max(baseSize, (uint) (baseSize + range / obj.Level));
            using var font = Plugin.GameFont.WithFont(fontSize);
            renderer.WriteLeafInline(obj);
        }
    }

    private class LiteralInlineRenderer : MarkdownObjectRenderer<ImGuiRenderer, LiteralInline> {
        protected override void Write(ImGuiRenderer renderer, LiteralInline obj) {
            ImGuiHelper.WrapText(
                obj.Content.ToString(),
                ImGui.GetContentRegionAvail().X,
                renderer._onClick,
                renderer._onHover
            );
        }
    }

    private class EmphasisInlineRenderer : MarkdownObjectRenderer<ImGuiRenderer, EmphasisInline> {
        protected override void Write(ImGuiRenderer renderer, EmphasisInline obj) {
            var baseSize = Plugin.Instance.PluginUi.BaseSize;
            using var font = Plugin.GameFont.WithFont(baseSize, true);
            renderer.WriteChildren(obj);
        }
    }

    private class LinkInlineRenderer : MarkdownObjectRenderer<ImGuiRenderer, LinkInline> {
        private readonly SemaphoreSlim _imagesMutex = new(1, 1);
        private readonly Dictionary<string, ImageInfo> _images = new();

        ~LinkInlineRenderer() {
            this._imagesMutex.Dispose();
            foreach (var info in this._images.Values) {
                info.Wrap?.Dispose();
            }
        }

        private class ImageInfo {
            internal IDalamudTextureWrap? Wrap { get; set; }
            internal Exception? Exception { get; set; }
            internal DateTime LastAccessed { get; private set; }

            internal ImageInfo() {
                this.UpdateAccess();
            }

            internal void UpdateAccess() {
                this.LastAccessed = DateTime.UtcNow;
            }

            internal TimeSpan LastUsed => DateTime.UtcNow - this.LastAccessed;
        }

        private void RemoveStale() {
            var toRemove = this._images
                .Where(e => e.Value.LastUsed > TimeSpan.FromMinutes(5))
                .Select(e => e.Key);

            foreach (var key in toRemove) {
                if (this._images.Remove(key, out var info)) {
                    info.Wrap?.Dispose();
                }
            }
        }

        private void InternalWrite(ImGuiRenderer renderer, LinkInline obj) {
            if (obj is { IsImage: true, Url: not null }) {
                using (SemaphoreGuard.Wait(this._imagesMutex)) {
                    if (this._images.TryGetValue(obj.Url, out var info)) {
                        info.UpdateAccess();

                        if (info.Wrap != null) {
                            ImGuiHelper.ImageFullWidth(info.Wrap);

                            if (!string.IsNullOrEmpty(obj.Title) && ImGui.IsItemHovered()) {
                                ImGuiHelper.Tooltip(obj.Title);
                            }
                        } else {
                            renderer.WriteChildren(obj);
                        }
                    } else {
                        var newInfo = new ImageInfo();
                        this._images[obj.Url] = newInfo;
                        Task.Run(async () => {
                            try {
                                var bytes = await Plugin.Client.GetByteArrayAsync2(obj.Url);
                                var image = await Plugin.Instance.TextureProvider.CreateFromImageAsync(bytes);
                                newInfo.Wrap = image;
                            } catch (Exception ex) {
                                newInfo.Exception = ex;
                                ErrorHelper.Handle(ex, "Could not download image");
                            }
                        });
                        renderer.WriteChildren(obj);
                    }

                    this.RemoveStale();
                }

                return;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.ParsedBlue);
            try {
                renderer.WriteChildren(obj);
            } finally {
                ImGui.PopStyleColor();
            }
        }

        protected override void Write(ImGuiRenderer renderer, LinkInline obj) {
            if (!string.IsNullOrEmpty(obj.Url)) {
                renderer._onClick = () => Process.Start(new ProcessStartInfo(obj.Url) {
                    UseShellExecute = true,
                });
            }

            if (!string.IsNullOrEmpty(obj.Title)) {
                var origColour = ImGui.GetStyle().Colors[(int) ImGuiCol.Text];
                renderer._onHover = () => {
                    ImGui.PushStyleColor(ImGuiCol.Text, origColour);

                    try {
                        ImGuiHelper.Tooltip(obj.Title);
                    } finally {
                        ImGui.PopStyleColor();
                    }
                };
            }

            try {
                this.InternalWrite(renderer, obj);
            } finally {
                renderer._onClick = null;
                renderer._onHover = null;
            }
        }
    }

    private class AutolinkInlineRenderer : MarkdownObjectRenderer<ImGuiRenderer, AutolinkInline> {
        protected override void Write(ImGuiRenderer renderer, AutolinkInline obj) {
            renderer._onClick = () => Process.Start(new ProcessStartInfo(obj.Url) {
                UseShellExecute = true,
            });

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.ParsedBlue);
            try {
                ImGuiHelper.WrapText(
                    obj.Url,
                    ImGui.GetContentRegionAvail().X,
                    renderer._onClick,
                    renderer._onHover
                );
            } finally {
                ImGui.PopStyleColor();
                renderer._onClick = null;
            }
        }
    }

    private class CodeInlineRenderer : MarkdownObjectRenderer<ImGuiRenderer, CodeInline> {
        protected override void Write(ImGuiRenderer renderer, CodeInline obj) {
            ImGui.PushFont(UiBuilder.MonoFont);
            try {
                ImGuiHelper.WrapText(
                    obj.ContentSpan.ToString(),
                    ImGui.GetContentRegionAvail().X,
                    renderer._onClick,
                    renderer._onHover
                );
            } finally {
                ImGui.PopFont();
            }
        }
    }

    private class LineBreakInlineRenderer : MarkdownObjectRenderer<ImGuiRenderer, LineBreakInline> {
        protected override void Write(ImGuiRenderer renderer, LineBreakInline obj) {
            if (!obj.IsHard) {
                return;
            }

            ImGui.Dummy(Vector2.Zero);
            ImGui.Spacing();
        }
    }
}
