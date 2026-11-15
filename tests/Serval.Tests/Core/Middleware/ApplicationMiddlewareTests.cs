using Microsoft.Extensions.DependencyInjection;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Middleware;

public sealed class ApplicationMiddlewareTests
{
    [Theory]
    [InlineData("/test/task", "GET", 200)]
    [InlineData("/missing", "GET", 404)]
    [InlineData("/test/task", "OPTIONS", 204)]
    [InlineData("/test/task", "POST", 405)]
    [InlineData("/test/crash", "GET", 500)]
    [InlineData("/test/partial", "GET", 200)]
    public async Task CallbacksCoverRoutingErrorsAndAborts(string path, string method, int status)
    {
        var          events = new List<string>();
        ScopedProbe? probe  = null;
        await using var app = new TestApplication(configureBuilder: builder => builder.Use(async (context, next) =>
        {
            events.Add("enter");
            probe = context.Services.GetRequiredService<ScopedProbe>();
            context.Response.OnStarting(() =>
            {
                events.Add("start");
                context.Response.Headers["X-Global"] = "yes";
                return ValueTask.CompletedTask;
            });
            context.Response.OnCompleted(() =>
            {
                Assert.False(probe.Disposed);
                events.Add("completed");
                return ValueTask.CompletedTask;
            });
            await next(context);
        }));

        var response = await app.SendAsync(path, method);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("yes", response.Headers["X-Global"]);
        Assert.Equal(["enter", "start", "completed"], events);
        Assert.True(probe!.Disposed);
    }

    [Fact]
    public async Task MiddlewareCoversFilesAndCanonicalRedirects()
    {
        var root = Directory.CreateTempSubdirectory("serval-global-").FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "hello");
        var count = 0;
        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath = root;
            options.TrailingSlashes           = TrailingSlashBehavior.RedirectToWithoutSlash;
        }, configureBuilder: builder => builder.Use(async (context, next) =>
        {
            count++;
            await next(context);
        }));
        Assert.Equal("hello", (await app.SendAsync("/hello.txt")).Text);
        Assert.Equal(308, (await app.SendAsync("/hello.txt/")).StatusCode);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task MiddlewareAndCallbacksHaveDeterministicOrderAndCanShortCircuit()
    {
        var events = new List<string>();
        await using var app = new TestApplication(configureBuilder: builder =>
        {
            foreach (var name in new[] { "outer", "inner" })
            {
                builder.Use(async (context, next) =>
                {
                    events.Add(name + " enter");
                    context.Response.OnStarting(() =>
                    {
                        events.Add(name + " start");
                        return ValueTask.CompletedTask;
                    });
                    context.Response.OnCompleted(() =>
                    {
                        events.Add(name + " complete");
                        return ValueTask.CompletedTask;
                    });
                    await next(context);
                    events.Add(name + " exit");
                });
            }

            builder.Use((context, _) => context.Response.WriteTextAsync("intercepted"));
        });
        Assert.Equal("intercepted", (await app.SendAsync("/missing")).Text);
        Assert.Equal(["outer enter", "inner enter", "inner start", "outer start", "inner exit", "outer exit", "inner complete", "outer complete"], events);
    }

    [Fact]
    public async Task CallbackFailuresDoNotRepeatOrPreventOtherCompletionCallbacks()
    {
        var starts      = 0;
        var completions = 0;
        await using var app = new TestApplication(configureBuilder: builder => builder.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                starts++;
                throw new InvalidOperationException("startup");
            });
            context.Response.OnCompleted(() =>
            {
                completions++;
                return ValueTask.CompletedTask;
            });
            context.Response.OnCompleted(() => throw new InvalidOperationException("completion"));
            await next(context);
        }));
        var response = await app.SendAsync("/test/task");
        Assert.Equal(500, response.StatusCode);
        Assert.Equal(1, starts);
        Assert.Equal(1, completions);
    }
}
