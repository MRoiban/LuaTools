using System.Windows.Controls;
using System.Windows.Documents;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SafeMarkdownTests
{
    [Theory]
    [InlineData("https://example.com/help", true)]
    [InlineData("https://example.com/help#section", true)]
    [InlineData("https://user:password@example.com/", false)]
    [InlineData("http://example.com/", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData(@"\\server\share\program.exe", false)]
    [InlineData("steam://run/123", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative", false)]
    public void OnlyAbsoluteHttpsLinksWithoutCredentialsOpen(string url, bool allowed) =>
        Assert.Equal(allowed, SafeMarkdown.IsSafeBrowserLink(url));

    [Theory]
    [InlineData("![alt](https://127.0.0.1/image.png)")]
    [InlineData("![alt][image]\n\n[image]: file://server/share/image.png")]
    public void MarkdownImagesUsePolicyLoaderBeforeAnyDecode(string markdown)
    {
        RunSta(() =>
        {
            var document = Markdig.Wpf.Markdown.ToFlowDocument(markdown, SafeMarkdown.Pipeline);
            var paragraph = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            var container = Assert.IsType<InlineUIContainer>(paragraph.Inlines.FirstInline);
            var button = Assert.IsType<Button>(container.Child);
            var image = Assert.IsType<Image>(button.Template.LoadContent());
            Assert.NotNull(SafeImageLoader.GetSource(image));
            Assert.Null(image.Source); // The blocked destination is never handed to BitmapImage.
        });
    }

    [Fact]
    public void NormalFormattingAndLinksRemainRendered()
    {
        RunSta(() =>
        {
            var document = Markdig.Wpf.Markdown.ToFlowDocument("**bold** [help](https://example.com/)", SafeMarkdown.Pipeline);
            var paragraph = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            Assert.IsType<Bold>(paragraph.Inlines.FirstInline);
            Assert.IsType<Hyperlink>(paragraph.Inlines.LastInline);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
