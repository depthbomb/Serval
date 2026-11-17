using Microsoft.Extensions.DependencyInjection;
using Serval.Hosting;
using Serval.Http;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Routing;

public sealed class CompositionTests
{
    [Fact]
    public async Task MatchingDoesNotExecuteAndAppliedPoliciesCoverCustomDispatch()
    {
        var executed = 0;
        await using var app = new TestApplication(configureBuilder: builder =>
        {
            builder.AddGroup("/custom", group =>
            {
                group.ConfigureEndpoints(endpoint =>
                {
                    endpoint.Metadata.Add("selected");
                    endpoint.MaxRequestBodyBytes = 3;
                });
                group.AddEndpoint("POST", "/{id}", async context =>
                {
                    executed++;
                    await context.Response.WriteTextAsync(await context.Request.ReadTextAsync());
                }, "raw");
            });
            builder.DispatchWith(async context =>
            {
                var match = context.Services.GetRequiredService<IEndpointMatcher>().Match(context);
                Assert.Equal(0, executed);
                Assert.Null(context.Endpoint);
                Assert.False(context.Response.HasStarted);
                Assert.Null(match.Endpoint!.ControllerType);
                match.Apply(context);
                Assert.Equal("selected", context.Endpoint!.GetMetadata<string>());
                Assert.Equal("42", context.RouteValues["id"]);
                await match.ExecuteAsync(context);
            });
        });
        Assert.Equal(413, (await app.SendAsync("/custom/42", "POST", "large")).StatusCode);
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task DelegateEndpointsUseGroupsNamesAndAutomaticMethodResponses()
    {
        await using var app = new TestApplication(configureBuilder: builder => builder.AddGroup("/delegate", group =>
            group.AddEndpoint("GET", "/{name}", context => context.Response.WriteTextAsync(context.RouteValues["name"]), "hello")));
        Assert.Equal("alice", (await app.SendAsync("/delegate/alice")).Text);
        Assert.Equal("/delegate/bob", app.Services.GetRequiredService<IUrlGenerator>().Path("hello", new Dictionary<string, object?>
        {
            ["name"] = "bob"
        }));
        Assert.Equal(204, (await app.SendAsync("/delegate/alice", "OPTIONS")).StatusCode);
        Assert.Equal(405, (await app.SendAsync("/delegate/alice", "DELETE")).StatusCode);
    }

    [Fact]
    public async Task CustomRoutingCanContinueUnmatchedPathsButPreservesAction404()
    {
        await using var app = new TestApplication(configureBuilder: builder =>
        {
            builder.AddEndpoint("GET", "/owned", context =>
            {
                context.Response.StatusCode = 404;
                return context.Response.WriteTextAsync("owned");
            });
            builder.DispatchWith(context => new RoutingMiddleware(context.Services.GetRequiredService<IEndpointMatcher>()).InvokeAsync(context,
                unmatched => unmatched.Response.WriteTextAsync("fallback")));
        });
        Assert.Equal("fallback", (await app.SendAsync("/absent")).Text);
        var owned = await app.SendAsync("/owned");
        Assert.Equal(404, owned.StatusCode);
        Assert.Equal("owned", owned.Text);
    }

    [Fact]
    public async Task CustomDispatchCannotBypassFramingValidation()
    {
        var called = false;
        await using var app = new TestApplication(configureBuilder: builder => builder.DispatchWith(_ =>
        {
            called = true;
            return ValueTask.CompletedTask;
        }));
        Assert.Equal(400, (await app.SendAsync("/", configureHeaders: headers => headers["Content-Length"] = "invalid")).StatusCode);
        Assert.False(called);
    }
}
