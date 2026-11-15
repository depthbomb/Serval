#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Serval.Binding;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Middleware;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Binding;

public sealed class GeneratedBindingTests
{
    [Theory]
    [InlineData("/binding/scalars/12", "12:3:missing:Monday:0")]
    [InlineData("/binding/scalars/12?limit=8&name=&day=friday&tag=2&tag=5", "12:8::Friday:7")]
    [InlineData("/binding/optional", "7")]
    [InlineData("/binding/optional/9", "9")]
    [InlineData("/binding/nullable", "missing")]
    [InlineData("/binding/default", "4")]
    public async Task ScalarsArraysAliasesAndMissingValuesUseTheirDeclaredContracts(string path, string expected)
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync(path);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(expected, response.Text);
    }

    [Theory]
    [InlineData("/binding/scalars/no")]
    [InlineData("/binding/scalars/2?limit=oops")]
    [InlineData("/binding/scalars/2?limit=")]
    [InlineData("/binding/scalars/2?limit=1&limit=2")]
    [InlineData("/binding/scalars/2?name=a&name=b")]
    [InlineData("/binding/scalars/2?day=not-a-day")]
    [InlineData("/binding/scalars/2?tag=1&tag=oops")]
    [InlineData("/binding/required")]
    public async Task InvalidOrRepeatedScalarsAndInvalidArrayElementsReturn400(string path)
    {
        await using var app = new TestApplication();
        Assert.Equal(400, (await app.SendAsync(path)).StatusCode);
    }

    [Fact]
    public async Task HeadersAndServicesUseTheRequestScope()
    {
        await using var app = new TestApplication(configureServices: services => services.AddSingleton(typeof(int?), 42));
        var response = await app.SendAsync("/binding/headers", configureHeaders: headers =>
        {
            headers["X-Number"] = "4";
            headers.Append("X-Tag", "a,b");
            headers.Append("X-Tag", "c");
        });
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("4:a,b|c:True:True:True:42", response.Text);
        Assert.Equal(400, (await app.SendAsync("/binding/headers")).StatusCode);
        Assert.Equal(400, (await app.SendAsync("/binding/headers", configureHeaders: headers =>
        {
            headers.Append("X-Number", "1");
            headers.Append("X-Number", "2");
        })).StatusCode);
    }

    [Theory]
    [InlineData("{\"displayName\":\"hello\",\"count\":2}", "application/json", 200)]
    [InlineData("{\"displayName\":\"hello\",\"count\":2}", "application/vnd.sample+json", 200)]
    [InlineData("null", "application/json", 400)]
    [InlineData("", "application/json", 400)]
    [InlineData("{broken", "application/json", 400)]
    [InlineData("{}", "text/plain", 415)]
    public async Task JsonBodiesAndResultsUseRegisteredGeneratedMetadata(string body, string contentType, int status)
    {
        await using var app      = JsonApplication();
        var             response = await app.SendAsync("/binding/body", "POST", body, contentType);
        Assert.Equal(status, response.StatusCode);
        if (status == 200)
        {
            Assert.Equal("{\"displayName\":\"hello\",\"count\":2}", response.Text);
            Assert.Equal("True", response.Headers["X-Cancellation"]);
        }
    }

    [Fact]
    public async Task BodyBindingKeepsLimitsAndRunsInsideMiddleware()
    {
        await using var app = JsonApplication(options => options.MaxRequestBodyBytes = 8);
        Assert.Equal(413, (await app.SendAsync("/binding/body", "POST", "{\"displayName\":\"large\"}", MimeTypes.Json)).StatusCode);
        var rejected = await app.SendAsync("/binding/body", "POST", "invalid", MimeTypes.Json, headers => headers["X-Reject"] = "yes");
        Assert.Equal(403, rejected.StatusCode);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var canceled = await app.SendAsync("/binding/body", "POST", "{}", MimeTypes.Json, cancellationToken: cancellation.Token);
        Assert.True(canceled.Aborted);
    }

    [Fact]
    public async Task JsonContextsComposeAndDoNotSilentlyUseReflection()
    {
        await using var app     = JsonApplication();
        var             options = app.Services.GetRequiredService<ServalOptions>();
        _ = app.Services.GetRequiredService<IUrlGenerator>();
        Assert.Equal("{\"text\":\"message\"}", JsonSerializer.Serialize(new Message("message"), options.Json));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new UnregisteredPayload("value"), options.Json));
        var explicitMetadata = await app.SendAsync("/binding/explicit");
        Assert.Equal("{\"DisplayName\":\"explicit\",\"Count\":1}", explicitMetadata.Text);
        Assert.Throws<InvalidOperationException>(() => options.Json.TypeInfoResolverChain.Add(BindingJsonContext.Default));
    }

    [Fact]
    public async Task JsonContextRegistrationHonorsOptionsConvertersAndExistingResolvers()
    {
        await using var app = JsonApplication(options =>
        {
            options.Json.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            options.Json.Converters.Add(new CountConverter());
        });
        var response = await app.SendAsync("/binding/body", "POST", "{\"display_name\":\"hello\",\"count\":\"n:3\"}", MimeTypes.Json);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"display_name\":\"hello\",\"count\":\"n:3\"}", response.Text);

        var services = new ServiceCollection();
        var resolver = new DefaultJsonTypeInfoResolver();
        var builder  = services.AddServal(options => options.Json.TypeInfoResolverChain.Add(resolver));
        Assert.Same(builder, builder.AddJsonContext(BindingJsonContext.Default));
        Assert.Same(resolver, builder.Options.Json.TypeInfoResolverChain[0]);
        Assert.Same(BindingJsonContext.Default, builder.Options.Json.TypeInfoResolverChain[1]);
        Assert.Throws<ArgumentNullException>(() => builder.AddJsonContext(null!));
    }

    private static TestApplication JsonApplication(Action<ServalOptions>? configure = null)
        => new(configure, configureBuilder: builder => builder.AddJsonContext(BindingJsonContext.Default).AddJsonContext(TestJsonContext.Default));

    private sealed record UnregisteredPayload(string Value);

    private sealed class CountConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => int.Parse(reader.GetString()![2..], CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
            => writer.WriteStringValue("n:" + value.ToString(CultureInfo.InvariantCulture));
    }
}

public sealed record BindingPayload(string DisplayName, int Count);

[JsonSerializable(typeof(BindingPayload))]
internal partial class BindingJsonContext : JsonSerializerContext;

[Controller] [Route("binding")]
public sealed class BindingController : ControllerBase
{
    [Get("scalars/{id}")]
    public Task<TextResult> Scalars(RequestContext               context,
                                    [FromRoute("ID")]  int       number,
                                    [FromQuery("tag")] int[]     tags,
                                    [FromQuery]        string?   name,
                                    [FromQuery]        int       limit = 3,
                                    [FromQuery]        DayOfWeek day   = DayOfWeek.Monday)
        => Task.FromResult(Text($"{number}:{limit}:{name ?? "missing"}:{day}:{tags.Sum()}"));

    [Get("optional/{value:int?}")]
    public ValueTask<IActionResult> Optional(RequestContext context, [FromRoute] int value = 7) => TextAsync(value.ToString());

    [Get("nullable/{value:int?}")]
    public ValueTask<IActionResult> Nullable(RequestContext context, [FromRoute] int? value) => TextAsync(value?.ToString() ?? "missing");

    [Get("default/{value:int=4}")]
    public ValueTask<IActionResult> Default(RequestContext context, [FromRoute] int value) => TextAsync(value.ToString());

    [Get("required")]
    public ValueTask<IActionResult> Required(RequestContext context, [FromQuery] string name) => TextAsync(name);

    [Get("headers")]
    public ValueTask<IActionResult> Headers(RequestContext                         context,
                                            [FromHeader("X-Number")] int           number,
                                            [FromHeader("X-Tag")]    string[]      tags,
                                            [FromServices]           ScopedProbe   probe,
                                            [FromServices]           TimeProvider? missing,
                                            [FromQuery]              int[]?        absent,
                                            [FromServices]           int?          configured)
        => TextAsync($"{number}:{string.Join('|', tags)}:{ReferenceEquals(probe, context.Services.GetRequiredService<ScopedProbe>())}:{missing is null}:{absent is null}:{configured}");

    [Post("body")] [RejectBody]
    public Task<JsonResult<BindingPayload>> Body(RequestContext context, [FromBody] BindingPayload payload, CancellationToken cancellationToken)
    {
        context.Response.Headers["X-Cancellation"] = (cancellationToken == context.RequestAborted).ToString();
        return Task.FromResult(Json(payload));
    }

    [Get("explicit")]
    public ValueTask<IActionResult> Explicit(RequestContext context)
        => JsonAsync(new BindingPayload("explicit", 1), BindingJsonContext.Default.BindingPayload);
}

public sealed class RejectBodyAttribute : MiddlewareAttribute
{
    public override ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        => context.Request.Headers["X-Reject"] == "yes" ? ValueTask.FromException(new HttpErrorException(403)) : next(context);
}
