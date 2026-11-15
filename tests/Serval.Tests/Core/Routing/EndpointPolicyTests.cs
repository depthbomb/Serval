using Microsoft.Extensions.DependencyInjection;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Results;
using Serval.Middleware;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Routing;

public sealed class EndpointPolicyTests
{
    [Theory]
    [InlineData("items")]
    [InlineData("/items")]
    public async Task GroupsJoinRelativeAndRootedActionTemplates(string template)
    {
        await using var app = new TestApplication(configureBuilder: builder => builder.AddGroup("/api/", group =>
            group.AddController<object>("GET", template, null, (_, _) => ValueTask.FromResult<IActionResult>(new TextResult("ok")))));
        Assert.Equal("ok", (await app.SendAsync("/api/items")).Text);
    }

    [Fact]
    public async Task NestedGroupsApplyConventionsAndExposeMetadataBeforeEndpointMiddleware()
    {
        Endpoint? selected = null;
        await using var app = new TestApplication(configureBuilder: builder =>
        {
            builder.AddGroup("/api", group =>
            {
                group.AddGroup("/v2", nested => nested.AddController<object>("GET", "/items/{id}", "items", (_, context) =>
                {
                    selected = context.Endpoint;
                    return ValueTask.FromResult<IActionResult>(new TextResult(context.RouteValues["id"]));
                }));
                group.ConfigureEndpoints(endpoint =>
                {
                    endpoint.Metadata.Add(new Label("group"));
                    endpoint.Middleware.Add(new MetadataMiddleware());
                });
            }, "api.");
            builder.ConfigureEndpoints(endpoint => endpoint.Metadata.Add(new Label("root")));
        });
        var response = await app.SendAsync("/api/v2/items/12");
        Assert.Equal("12", response.Text);
        Assert.Equal("group", response.Headers["X-Policy"]);
        Assert.NotNull(selected);
        Assert.Equal("api.items", selected.Name);
        Assert.Equal("/api/v2/items/{id}", selected.Template);
        Assert.Equal(typeof(object), selected.ControllerType);
        Assert.Equal("/api/v2/items/34", app.Services.GetRequiredService<IUrlGenerator>().Path("api.items", new Dictionary<string, object?> { ["id"] = 34 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointLimitsApplyWithAndWithoutContentLength(bool declared)
    {
        await using var app = new TestApplication(options => options.MaxRequestBodyBytes = 1024, configureBuilder: builder =>
        {
            builder.AddController<object>("POST", "/small", null, Read);
            builder.AddController<object>("POST", "/large", null, Read);
            builder.ConfigureEndpoints(endpoint =>
            {
                if (endpoint.Template == "/small")
                {
                    endpoint.MaxRequestBodyBytes = 4;
                }
            });
        });
        Assert.Equal(413, (await app.SendAsync("/small", "POST", "12345", configureHeaders: headers =>
        {
            if (declared)
            {
                headers["Content-Length"] = "5";
            }
        })).StatusCode);
        Assert.Equal("12345", (await app.SendAsync("/large", "POST", "12345")).Text);

        static async ValueTask<IActionResult> Read(object _, RequestContext context)
        {
            return new TextResult(await context.Request.ReadTextAsync());
        }
    }

    [Fact]
    public async Task EndpointFormSettingsAreIsolatedAndSnapshotted()
    {
        EndpointBuilder? captured = null;
        await using var app = new TestApplication(configureBuilder: builder =>
        {
            builder.AddController<object>("POST", "/one", null, Read);
            builder.AddController<object>("POST", "/many", null, Read);
            builder.ConfigureEndpoints(endpoint =>
            {
                if (endpoint.Template == "/one")
                {
                    endpoint.Forms.MaxFields = 1;
                    captured                 = endpoint;
                }
            });
        });
        _                         = app.Services.GetRequiredService<IRequestHandler>();
        captured!.Forms.MaxFields = 100;
        Assert.Equal(413, (await app.SendAsync("/one", "POST", "a=1&b=2", "application/x-www-form-urlencoded")).StatusCode);
        Assert.Equal(200, (await app.SendAsync("/many", "POST", "a=1&b=2", "application/x-www-form-urlencoded")).StatusCode);

        static async ValueTask<IActionResult> Read(object _, RequestContext context)
        {
            await context.Request.ReadFormAsync();
            return new TextResult("ok");
        }
    }

    [Fact]
    public async Task EndpointDeadlineOverridesTheApplicationDefault()
    {
        await using var app = new TestApplication(options => options.RequestTimeout = TimeSpan.FromSeconds(10), configureBuilder: builder =>
        {
            builder.AddController<object>("GET", "/deadline", null, async (_, context) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted);
                return new TextResult("late");
            });
            builder.ConfigureEndpoints(endpoint => endpoint.RequestTimeout = TimeSpan.FromMilliseconds(30));
        });
        Assert.Equal(408, (await app.SendAsync("/deadline")).StatusCode);
    }

    [Fact]
    public async Task PoliciesCannotRaiseTheTransportCeiling()
    {
        await using var app = new TestApplication(options => options.MaxRequestBodyBytes = 100, configureBuilder: builder => builder.ConfigureEndpoints(endpoint => endpoint.MaxRequestBodyBytes = 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => app.Services.GetRequiredService<IRequestHandler>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PoliciesRejectNullMetadataAndMiddleware(bool addMiddleware)
    {
        await using var app = new TestApplication(configureBuilder: builder => builder.ConfigureEndpoints(endpoint =>
        {
            // Simulate a caller bypassing the collections' non-null annotations.
            if (addMiddleware)
            {
                endpoint.Middleware.Add(null!);
            }
            else
            {
                endpoint.Metadata.Add(null!);
            }
        }));
        var exception = Assert.Throws<ArgumentException>(() => app.Services.GetRequiredService<IRequestHandler>());
        Assert.Equal("Endpoint metadata and middleware cannot contain null entries.", exception.Message);
    }

    private sealed record Label(string Text);

    private sealed class MetadataMiddleware : MiddlewareAttribute
    {
        public override ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        {
            context.Response.Headers["X-Policy"] = context.Endpoint!.GetMetadata<Label>()!.Text;
            return next(context);
        }
    }
}
