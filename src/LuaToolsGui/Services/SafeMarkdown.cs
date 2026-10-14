using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Wpf.Inlines;
using Markdig.Syntax.Inlines;
using Markdig.Wpf;

namespace LuaToolsGui.Services;

/// <summary>Preserves the MarkdownViewer pipeline while routing image fetching through AppHttp.</summary>
public static class SafeMarkdown
{
    public static MarkdownPipeline Pipeline { get; } = CreatePipeline();

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseSupportedExtensions();
        builder.Extensions.Add(new SafeImagesExtension());
        return builder.Build();
    }

    internal static bool IsSafeBrowserLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host);

    private sealed class SafeImagesExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) { }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not WpfRenderer wpf) return;
            for (int i = 0; i < wpf.ObjectRenderers.Count; i++)
                if (wpf.ObjectRenderers[i] is LinkInlineRenderer)
                    wpf.ObjectRenderers[i] = new SafeLinkRenderer();
        }
    }

    private sealed class SafeLinkRenderer : LinkInlineRenderer
    {
        protected override void Write(WpfRenderer renderer, LinkInline link)
        {
            if (!link.IsImage)
            {
                base.Write(renderer, link);
                return;
            }
            string? url = link.GetDynamicUrl?.Invoke() ?? link.Url;
            var image = new FrameworkElementFactory(typeof(Image));
            image.SetValue(SafeImageLoader.SourceProperty, url);
            image.SetResourceReference(FrameworkContentElement.StyleProperty, Styles.ImageStyleKey);
            var button = new Button
            {
                Template = new ControlTemplate { VisualTree = image },
                Command = Commands.Image,
                CommandParameter = url,
            };
            renderer.WriteInline(new InlineUIContainer(button));
        }
    }
}
