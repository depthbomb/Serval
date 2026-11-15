#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Serval.Sample.EntityFramework;

public sealed class NotesInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopes;

    public NotesInitializer(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Hosted services are singletons. Resolve the scoped context inside
        // a scope, and finish initialization before the HTTP listener starts.
        await using var scope    = _scopes.CreateAsyncScope();
        var             database = scope.ServiceProvider.GetRequiredService<NotesDbContext>();

        // A fixed-schema demo can use EnsureCreated. Use migrations from the
        // start when building an application whose schema will evolve.
        await database.Database.EnsureCreatedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
