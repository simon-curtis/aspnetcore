// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using System.Text;
using Components.TestServer.RazorComponents;
using Microsoft.AspNetCore.Components.E2ETest.Infrastructure;
using Microsoft.AspNetCore.Components.E2ETest.Infrastructure.ServerFixtures;
using Microsoft.AspNetCore.E2ETesting;
using OpenQA.Selenium;
using TestServer;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.Components.E2ETests.ServerRenderingTests;

public class StreamingRenderingTest : ServerTestBase<BasicTestAppServerSiteFixture<RazorComponentEndpointsStartup<App>>>
{
    public StreamingRenderingTest(
        BrowserFixture browserFixture,
        BasicTestAppServerSiteFixture<RazorComponentEndpointsStartup<App>> serverFixture,
        ITestOutputHelper output)
        : base(browserFixture, serverFixture, output)
    {
    }

    public override Task InitializeAsync()
        => InitializeAsync(BrowserFixture.StreamingContext);

    [Fact]
    public void CanRenderNonstreamingPageWithoutInjectingStreamingMarkers()
    {
        Navigate(ServerPathBase);

        Browser.Equal("Hello", () => Browser.Exists(By.TagName("h1")).Text);

        Assert.DoesNotContain("<blazor-ssr", Browser.PageSource);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CanPerformStreamingRendering(bool useLargeItems, bool duringEnhancedNavigation)
    {
        IWebElement originalH1Elem;
        var linkText = useLargeItems ? "Large Streaming" : "Streaming";
        var url = useLargeItems ? "large-streaming" : "streaming";

        if (duringEnhancedNavigation)
        {
            Navigate($"{ServerPathBase}/nav");
            originalH1Elem = Browser.Exists(By.TagName("h1"));
            Browser.Equal("Hello", () => originalH1Elem.Text);
            Browser.Exists(By.TagName("nav")).FindElement(By.LinkText(linkText)).Click();
        }
        else
        {
            Navigate($"{ServerPathBase}/{url}");
            originalH1Elem = Browser.Exists(By.TagName("h1"));
        }

        // Initial "waiting" state
        Browser.Equal("Streaming Rendering", () => originalH1Elem.Text);
        var getStatusText = () => Browser.Exists(By.Id("status"));
        var getDisplayedItems = () => Browser.FindElements(By.TagName("li"));
        Assert.Equal("Waiting for more...", getStatusText().Text);
        Assert.Empty(getDisplayedItems());

        // Can add items
        for (var i = 1; i <= 3; i++)
        {
            // Each time we click, there's another streaming render batch and the UI is updated
            Browser.FindElement(By.Id("add-item-link")).Click();
            Browser.Collection(getDisplayedItems, Enumerable.Range(1, i).Select<int, Action<IWebElement>>(index =>
            {
                return useLargeItems
                    ? actualItem => Assert.StartsWith($"Large Item {index}", actualItem.Text)
                    : actualItem => Assert.Equal($"Item {index}", actualItem.Text);
            }).ToArray());
            Assert.Equal("Waiting for more...", getStatusText().Text);

            // These are insta-removed so they don't pollute anything
            Browser.DoesNotExist(By.TagName("blazor-ssr"));
        }

        // Can finish the response
        Browser.FindElement(By.Id("end-response-link")).Click();
        Browser.Equal("Finished", () => getStatusText().Text);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RetainsDomNodesDuringStreamingRenderingUpdates(bool useLargeItems, bool duringEnhancedNavigation)
    {
        IWebElement originalH1Elem;
        var linkText = useLargeItems ? "Large Streaming" : "Streaming";
        var url = useLargeItems ? "streaming" : "large-streaming";

        if (duringEnhancedNavigation)
        {
            Navigate($"{ServerPathBase}/nav");
            originalH1Elem = Browser.Exists(By.TagName("h1"));
            Browser.Equal("Hello", () => originalH1Elem.Text);
            Browser.Exists(By.TagName("nav")).FindElement(By.LinkText(linkText)).Click();
        }
        else
        {
            Navigate($"{ServerPathBase}/{url}");
            originalH1Elem = Browser.Exists(By.TagName("h1"));
        }

        // Initial "waiting" state
        var originalStatusElem = Browser.Exists(By.Id("status"));
        Assert.Equal("Streaming Rendering", originalH1Elem.Text);
        Assert.Equal("Waiting for more...", originalStatusElem.Text);

        // Add an item; see the old elements were retained
        Browser.FindElement(By.Id("add-item-link")).Click();
        var originalLi = Browser.Exists(By.TagName("li"));
        Assert.Equal(originalH1Elem.Location, Browser.Exists(By.TagName("h1")).Location);
        Assert.Equal(originalStatusElem.Location, Browser.Exists(By.Id("status")).Location);

        // Make a further change; see elements (including dynamically added ones) are still retained
        // even if their text was updated
        Browser.FindElement(By.Id("end-response-link")).Click();
        Browser.Equal("Finished", () => originalStatusElem.Text);
        Assert.Equal(originalLi.Location, Browser.Exists(By.TagName("li")).Location);
    }

    [Fact]
    public async Task DisplaysErrorThatOccursWhileStreaming()
    {
        Navigate($"{ServerPathBase}/nav");
        Browser.Equal("Hello", () => Browser.Exists(By.TagName("h1")).Text);

        Browser.Exists(By.TagName("nav")).FindElement(By.LinkText("Error while streaming")).Click();
        await Task.Delay(3000); // Doesn't matter if this duration is too short or too long. It's just so the assertions don't start unnecessarily early.

        // Note that the tests always run with detailed errors off, so we only see this generic message
        Browser.Contains("There was an unhandled exception on the current request.", () => Browser.Exists(By.TagName("html")).Text);

        // See that 'back' still works
        Browser.Navigate().Back();
        Browser.Equal("Hello", () => Browser.Exists(By.TagName("h1")).Text);
        Assert.EndsWith("/subdir/nav", Browser.Url);
    }

    [Fact]
    public async Task HandlesOverlappingStreamingBatches()
    {
        // We're not using the browser in this test, but this call is simply to ensure
        // the server is running
        Navigate($"{ServerPathBase}/nav");

        // Perform a bandwidth-limited get request. It's difficult to pick parameters that surface the
        // problem reported in #50198 but these ones did so (until the implementation was fixed).
        var itemCount = 100000;
        var url = new Uri(new Uri(Browser.Url), $"{ServerPathBase}/overlapping-streaming?count={itemCount}");
        var response = await BandwidthThrottledGet(url.ToString(), 1024 * 10, 1);

        // Verify initial synchronous output
        var initialContent = ExtractContent(response, "<div id=\"content-to-verify\">", "</div>");
        Assert.Equal(ExpectedContent("Initial", itemCount), initialContent);

        // Verify there was exactly one <blazor-ssr> block with the expected content
        var ssrBlockCount = response.Split("<blazor-ssr>").Length - 1;
        Assert.Equal(1, ssrBlockCount);
        var streamingBlock = ExtractContent(response, "<blazor-ssr>", "</blazor-ssr>");
        var streamingContent = ExtractContent(streamingBlock, "<div id=\"content-to-verify\">", "</div>");
        Assert.Equal(ExpectedContent("Modified", itemCount), streamingContent);

        static string ExtractContent(string html, string startMarker, string endMarker)
        {
            var startPos = html.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.True(startPos > 0);
            var endPos = html.IndexOf(endMarker, startPos, StringComparison.Ordinal);
            var content = html.Substring(startPos + startMarker.Length, endPos - startPos - startMarker.Length);
            Assert.True(content.Length > 0);
            return content;
        }

        static string ExpectedContent(string message, int itemCount)
            => string.Join("", Enumerable.Range(0, itemCount).Select(i => $"<span>{i}: {message}</span>&#xD;&#xA;"));

        static async Task<string> BandwidthThrottledGet(string url, int chunkLength, int delayPerChunkMs)
        {
            var httpClient = new HttpClient { MaxResponseContentBufferSize = chunkLength };
            var responseStream = await httpClient.GetStreamAsync(url);
            var receiveBuffer = new byte[chunkLength];

            var resultBuilder = new StringBuilder();
            while (true)
            {
                var bytesRead = await responseStream.ReadAsync(receiveBuffer, 0, receiveBuffer.Length);
                if (bytesRead == 0)
                {
                    return resultBuilder.ToString();
                }

                resultBuilder.Append(Encoding.UTF8.GetString(receiveBuffer, 0, bytesRead));
                await Task.Delay(delayPerChunkMs);
            }
        }
    }
}
