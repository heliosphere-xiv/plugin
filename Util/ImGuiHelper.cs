using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Heliosphere.Ui;
using ImGuiNET;
using ImGuiScene;
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

    internal static void Tooltip(string text) {
        if (!ImGui.IsItemHovered()) {
            return;
        }

        var w = ImGui.CalcTextSize("m").X;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(w * 40);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
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
        var font = size == 0 ? null : Plugin.GameFont[size];
        if (font != null) {
            ImGui.PushFont(font.ImFont);
        }

        ImGui.TextUnformatted(text);

        if (font != null) {
            ImGui.PopFont();
        }
    }

    internal static void ImageFullWidth(TextureWrap wrap, float maxHeight = 0f, bool centred = false) {
        // get the available area
        var contentAvail = ImGui.GetContentRegionAvail();

        // set max height to image height if unspecified
        if (maxHeight == 0f) {
            maxHeight = wrap.Height;
        }

        // clamp height at the actual image height
        maxHeight = Math.Min(wrap.Height, maxHeight);

        // for the width, either use the whole space available
        // or the actual image's width, whichever is smaller
        var width = Math.Min(contentAvail.X, wrap.Width);
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

        if (centred && width < contentAvail.X) {
            var cursor = ImGui.GetCursorPos();
            ImGui.SetCursorPos(cursor with {
                X = contentAvail.X / 2 - width / 2,
            });
        }

        ImGui.Image(wrap.ImGuiHandle, new Vector2(width, height));
    }

    internal static void TextUnformattedCentred(string text, uint size = 0) {
        var widthAvail = ImGui.GetContentRegionAvail().X;
        var titleFont = size == 0 ? null : Plugin.GameFont[size];
        if (titleFont != null) {
            ImGui.PushFont(titleFont.ImFont);
        }

        var textSize = ImGui.CalcTextSize(text);
        if (textSize.X < widthAvail) {
            var cursor = ImGui.GetCursorPos();
            ImGui.SetCursorPos(cursor with {
                X = widthAvail / 2 - textSize.X / 2,
            });
        }

        ImGui.TextUnformatted(text);

        if (titleFont != null) {
            ImGui.PopFont();
        }
    }

    [SuppressMessage("ReSharper", "AccessToModifiedClosure")]
    internal static unsafe void WrapText(string csText, float lineWidth, Action? onClick = null, Action? onHover = null) {
        if (csText.Length == 0) {
            return;
        }

        foreach (var part in csText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)) {
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

    internal static bool InputTextVertical(string title, string id, ref string input, uint max, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None) {
        ImGui.TextUnformatted(title);
        ImGui.SetNextItemWidth(-1);
        return ImGui.InputText(id, ref input, max, flags);
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
                    renderer.WriteChildren(item);
                }
            } else {
                foreach (var item in obj) {
                    ImGui.TextUnformatted("• ");
                    renderer._lastWasInline = true;
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
            const int range = PluginUi.TitleSize - 16;
            var fontSize = Math.Max(16, (uint) (16.0 + (float) range / obj.Level));
            var font = Plugin.GameFont[fontSize];
            if (font != null) {
                ImGui.PushFont(font.ImFont);
            }

            try {
                renderer.WriteLeafInline(obj);
            } finally {
                if (font != null) {
                    ImGui.PopFont();
                }
            }
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
            var font = Plugin.GameFont[16, true];
            if (font != null) {
                ImGui.PushFont(font.ImFont);
            }

            try {
                renderer.WriteChildren(obj);
            } finally {
                if (font != null) {
                    ImGui.PopFont();
                }
            }
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
            internal TextureWrap? Wrap { get; set; }
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
            if (obj is { IsImage: true, Url: { } }) {
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
                                var bytes = await Plugin.Client.GetByteArrayAsync(obj.Url);
                                var image = await Plugin.PluginInterface.UiBuilder.LoadImageAsync(bytes);
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
