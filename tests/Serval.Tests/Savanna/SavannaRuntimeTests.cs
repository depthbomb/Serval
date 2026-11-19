#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Savanna;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Savanna;

public sealed class SavannaRuntimeTests
{
    [Fact]
    public async Task WriterEncodesAcrossBufferBoundariesAndPreservesOutputOrder()
    {
        var       text = string.Concat(Enumerable.Repeat("<&\"'🙂résumé", 2000));
        using var body = new MemoryStream();
        using var writer = new SavannaWriter(async (bytes, token) =>
        {
            await Task.Yield();
            await body.WriteAsync(bytes, token);
        }, 1024);
        await writer.WriteLiteralAsync("before"u8.ToArray());
        await writer.WriteValueAsync(text, new SavannaRenderContext());
        await writer.WriteLiteralAsync("after"u8.ToArray());
        await writer.FlushAsync();
        Assert.Equal("before" + HtmlEncoder.Default.Encode(text) + "after", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task RenderToStringLimitsOutputAndUsesExplicitCulture()
    {
        var view = new SavannaView<decimal>("price", static (writer, model, context) => writer.WriteValueAsync(model, context));
        Assert.Equal("12,5", await view.RenderToStringAsync(12.5m, new SavannaRenderContext(formatProvider: CultureInfo.GetCultureInfo("fr-FR"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderToStringAsync(12345m, maxBytes: 2).AsTask());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData("//other.example/path")]
    [InlineData("\\other.example")]
    [InlineData(" https://example.com")]
    public void UrlsRejectExecutableOrAmbiguousSchemes(string value)
    {
        Assert.Throws<ArgumentException>(() => SavannaUrl.From(value));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/products/one?q=a&b=2")]
    [InlineData("/products/one:two?return=https://example.com")]
    [InlineData("https://example.com/a")]
    [InlineData("#part")]
    [InlineData("products/one")]
    public void UrlsAcceptExplicitWebAndRelativePaths(string value)
    {
        Assert.Equal(value, SavannaUrl.From(value).Value);
    }

    [Fact]
    public async Task CancelledRenderingDoesNotCommitOutput()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var view = new SavannaView<string>("cancel", static (writer, model, context) => writer.WriteValueAsync(model, context));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.RenderToStringAsync("text", new SavannaRenderContext(cancellationToken: cancelled.Token)).AsTask());
    }

    [Fact]
    public async Task BufferedFailuresLeaveResponseUncommittedAndStreamingFailuresCanStartIt()
    {
        await using var app = new TestApplication();
        var view = new SavannaView<int>("fails", static async (writer, _, context) =>
        {
            await writer.WriteLiteralAsync(new byte[32 * 1024], context.CancellationToken);
            throw new InvalidOperationException("render failure");
        });
        var buffered  = new MemoryResponse();
        var streaming = new MemoryResponse();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SavannaResult<int>(view, 0, bufferLimit: 64 * 1024).ExecuteAsync(Context(app, buffered)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SavannaResult<int>(view, 0).ExecuteAsync(Context(app, streaming)).AsTask());
        Assert.False(buffered.HasStarted);
        Assert.Empty(buffered.Body.ToArray());
        Assert.True(streaming.HasStarted);
    }

    [Fact]
    public async Task SampleUsesLayoutsPartialsAndScopedComponentThroughTheRequestPipeline()
    {
        await using var sample   = new SampleApplication("Savanna");
        var             response = await sample.SendAsync("/");
        Assert.Equal(StatusCodes.Ok, response.StatusCode);
        Assert.Equal(MimeTypes.Html, response.Headers[HeaderNames.ContentType]);
        Assert.Contains("<title>Serval.Savanna | Catalog | Field notes</title>", response.Text);
        Assert.Contains("Notebook &lt;A5&gt;", response.Text);
        Assert.Contains("Ink &amp; pen", response.Text);
        Assert.Contains("<footer>Rendered by Serval.Savanna.</footer>", response.Text);
        Assert.Contains("href=\"/products/notebook\"", response.Text);
        Assert.Contains("<!-- Savanna view: Products/Index.svn", response.Text);
        Assert.Contains("<!-- Savanna partial: Products/Card.svn", response.Text);
        Assert.Contains("<!-- Savanna layout:", response.Text);
        Assert.Contains("<!-- Savanna component: FooterComponent", response.Text);
        Assert.Contains("; renderer=compiled; minify=html; blocks=2; partials=1; components=0", response.Text);
        Assert.Contains("<html lang=en>", response.Text);
        Assert.Contains("<meta charset=utf-8>", response.Text);
        Assert.DoesNotContain("\n", response.Text);
        Assert.DoesNotContain("\r", response.Text);
        Assert.Contains("</a> <span>", response.Text);
        Assert.DoesNotMatch(@">\s+<", response.Text.Replace("</a> <span>", "</a><span>"));
        Assert.EndsWith("</body></html>", response.Text);
    }

    [Fact]
    public async Task TrustedMarkupRequiresTheExplicitWriterMethod()
    {
        var view = new SavannaView<SavannaHtml>("raw", static (writer, model, context) => writer.WriteHtmlAsync(model, context));
        Assert.Equal("<strong>trusted</strong>", await view.RenderToStringAsync(SavannaHtml.Trusted("<strong>trusted</strong>")));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1020, true)]
    [InlineData(1021, true)]
    [InlineData(1022, true)]
    [InlineData(1023, true)]
    public async Task TrustedMarkupPreservesUtf8AcrossBufferBoundaries(int prefixLength, bool asyncWrites)
    {
        var       prefix = new string('x', prefixLength);
        var       value  = "🙂é中文\ud800broken\udfff" + string.Concat(Enumerable.Repeat("abc🙂\ud800", 2000));
        using var output = new MemoryStream();
        using var writer = new SavannaWriter(async (bytes, token) =>
        {
            if (asyncWrites)
            {
                await Task.Yield();
            }

            await output.WriteAsync(bytes, token);
        }, 1024);
        await writer.WriteLiteralAsync(Encoding.UTF8.GetBytes(prefix));
        await writer.WriteHtmlAsync(SavannaHtml.Trusted(value), new SavannaRenderContext());
        await writer.FlushAsync();
        Assert.Equal(Encoding.UTF8.GetBytes(prefix + value), output.ToArray());
    }

    [Fact]
    public async Task EncodedValuesMatchThePlatformEncoderAcrossBufferBoundaries()
    {
        var random = new Random(12345);
        var samples = new List<string>
        {
            "",
            new('x', 5000),
            string.Concat(Enumerable.Range(0, 128).Select(value => (char)value)),
            "\ud800broken\udfff🙂\0<&>\"'`+",
            "a&b<c>d\"e'f",
            "中文 café 🙂",
        };
        for (var index = 0; index < 30; index++)
        {
            samples.Add(new string(Enumerable.Range(0, 300).Select(_ => (char)random.Next(65536)).ToArray()));
        }

        using var body   = new MemoryStream();
        using var writer = new SavannaWriter(body.WriteAsync, 1024);
        foreach (var sample in samples)
        {
            await writer.WriteEncodedAsync(sample);
        }

        await writer.FlushAsync();
        Assert.Equal(string.Concat(samples.Select(HtmlEncoder.Default.Encode)), Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public void WhitespaceScopesRestoreStateAfterExceptionsAndDefaultDisposal()
    {
        using var writer = new SavannaWriter(static (_, _) => ValueTask.CompletedTask);
        default(SavannaWriter.WhitespaceScope).Dispose();
        using (writer.PreserveWhitespace())
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var nested = writer.PreserveWhitespace();
                throw new InvalidOperationException();
            }));
            Assert.True(writer.PreservesWhitespace);
        }

        Assert.False(writer.PreservesWhitespace);
    }

    private static RequestContext Context(TestApplication app, MemoryResponse response)
        => new(new Request(HttpMethods.Get, new Uri("http://localhost/"), HttpVersion.Version11, new Headers(), Stream.Null, 1024),
            response, app.Services, app.Services.GetRequiredService<IUrlGenerator>(), CancellationToken.None);
}
