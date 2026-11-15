#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serval.Savanna;
using Serval.Routing;

namespace Serval.Tests.Savanna;

public sealed class SavannaMarkupRuntimeTests
{
    [Fact]
    public async Task ApplicationUrlPolicyIsOptionalAndRejectsBeforeWritingAttributes()
    {
        using var stream  = new MemoryStream();
        using var writer  = new SavannaWriter(stream.WriteAsync);
        var       context = new SavannaRenderContext();
        await writer.WriteUrlAttributeAsync("href", "custom:open?a=1&b=2", context);
        await writer.FlushAsync();
        Assert.Equal(" href=\"custom:open?a=1&amp;b=2\"", Encoding.UTF8.GetString(stream.ToArray()));
        var before     = stream.Length;
        var restricted = new SavannaRenderContext(urlPolicy: new RelativeOnly());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteUrlAttributeAsync("href", "https://example.test", restricted).AsTask());
        await writer.FlushAsync();
        Assert.Equal(before, stream.Length);
        await writer.WriteUrlAttributeAsync("href", new Uri("/local?a=1&b=2", UriKind.Relative), restricted);
        await writer.FlushAsync();
        Assert.EndsWith(" href=\"/local?a=1&amp;b=2\"", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void RenderContextUsesFrameworkServicesAndRetainsPoliciesInSlotScopes()
    {
        var       urls     = new Urls();
        var       policy   = new RelativeOnly();
        using var services = new ServiceCollection().AddSingleton<IUrlGenerator>(urls).AddSingleton<IUrlPolicy>(policy).BuildServiceProvider();
        var       context  = new SavannaRenderContext(services);
        var       nested   = context.WithSlots(new SavannaSlots(new Dictionary<string, SavannaRenderFragment>()));
        Assert.Same(urls, nested.Urls);
        Assert.Same(policy, nested.UrlPolicy);
        Assert.Equal("/products/a%20b", nested.Urls.Path("products", new Dictionary<string, object?> { ["id"] = "a b" }));
        Assert.Same(context, context.WithSlots(SavannaSlots.Empty));
    }

    [Fact]
    public void ClassListsExcludeFalseEntriesAndRemoveDuplicates()
    {
        Assert.Equal("button active large", Css.Classes("button", ("hidden", false), ("active", true), "button large", null));
    }

    [Fact]
    public async Task JsonRawConvertersCannotTerminateTheScriptElement()
    {
        using var stream = new MemoryStream();
        using var writer = new SavannaWriter(stream.WriteAsync);
        await writer.WriteJsonAsync("state", new RawJson(), new SavannaRenderContext());
        await writer.FlushAsync();
        var output = Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("<img", output);
        Assert.Contains("\\u003C/script>", output);
        Assert.EndsWith("</script>", output);
    }

    [JsonConverter(typeof(RawConverter))]
    private sealed class RawJson { }

    private sealed class RawConverter : JsonConverter<RawJson>
    {
        public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
            => writer.WriteRawValue("\"</script><img src=x onerror=alert(1)>\"");
    }

    private sealed class RelativeOnly : IUrlPolicy
    {
        public bool IsAllowed(string url) => url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal);
    }

    private sealed class Urls : IUrlGenerator
    {
        public string Path(string routeName, IReadOnlyDictionary<string, object?>? values = null, IReadOnlyDictionary<string, object?>? query = null)
            => "/" + routeName + "/" + Uri.EscapeDataString(values!["id"]!.ToString()!);
    }
}
