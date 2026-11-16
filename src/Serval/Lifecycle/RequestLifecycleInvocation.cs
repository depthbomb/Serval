#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;

namespace Serval.Lifecycle;

internal sealed class RequestLifecycleInvocation
{
    private int _startingIndex;

    private readonly RequestContext             _context;
    private readonly Type[]                     _types;
    private readonly RequestLifecycleHandler?[] _handlers;

    internal RequestLifecycleInvocation(RequestContext context, Type[] types)
    {
        _context  = context;
        _types    = types;
        _handlers = new RequestLifecycleHandler?[types.Length];
    }

    internal async ValueTask ReceiveAsync(CancellationToken ct)
    {
        // Keep successfully resolved handlers available for error responses and completion if activation fails.
        for (var index = 0; index < _types.Length; index++)
        {
            _handlers[index] = (RequestLifecycleHandler)_context.Services.GetRequiredService(_types[index]);
        }

        foreach (var handler in _handlers)
        {
            await handler!.OnRequestReceivedAsync(_context, ct);
        }
    }

    internal async ValueTask StartAsync(CancellationToken ct)
    {
        while (_startingIndex < _handlers.Length)
        {
            // Advance before invoking: a failed hook must not repeat while rendering the error response.
            var handler = _handlers[_startingIndex++];
            if (handler is not null)
            {
                await handler.OnResponseStartingAsync(_context, ct);
            }
        }
    }

    internal async ValueTask CompleteAsync(RequestCompletion completion, Action<Exception> report, CancellationToken ct)
    {
        foreach (var handler in _handlers)
        {
            if (handler is null)
            {
                continue;
            }

            try
            {
                await handler.OnRequestCompletedAsync(_context, completion, ct);
            }
            catch (Exception exception)
            {
                report(exception);
            }
        }
    }
}
