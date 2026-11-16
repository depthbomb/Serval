#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http;

public abstract partial class Response
{
    internal Func<CancellationToken, ValueTask>? LifecycleStarting { get; set; }
    internal bool                                IsAborted         { get; private set; }
    internal bool                                ReceivingRequest  { get; set; }

    internal bool HasCompletionCallbacks => _completedCallbacks is { Count: > 0 };
    internal bool HasResources           => _resources is { Count         : > 0 };

    private bool _starting;
    private bool _completed;
    private bool _ended;
    private bool _disposing;

    private List<Func<CancellationToken, ValueTask>>? _startingCallbacks;
    private List<Func<CancellationToken, ValueTask>>? _completedCallbacks;
    private List<IAsyncDisposable>?                   _resources;

    /// <summary>Whether the response has completed or been aborted.</summary>
    protected bool HasEnded => _ended;

    /// <summary>Runs a callback before final headers are frozen, in reverse registration order.</summary>
    /// <remarks>Callbacks may change headers and status but must not write or start the response. Each runs at most once.</remarks>
    public void OnStarting(Func<ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        OnStarting(_ => callback());
    }

    /// <summary>Registers a startup callback with the token used to start the response.</summary>
    /// <remarks>The token may differ from RequestAborted when an error response is being written.</remarks>
    public void OnStarting(Func<CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (HasStarted || _starting || _ended)
        {
            throw new InvalidOperationException("Response startup has already begun.");
        }

        (_startingCallbacks ??= []).Add(callback);
    }

    /// <summary>Runs a callback after request processing ends, before request services and uploads are disposed.</summary>
    /// <remarks>
    ///     Callbacks run once in reverse registration order, including on aborted requests. Failures are logged
    ///     independently.
    /// </remarks>
    public void OnCompleted(Func<ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        OnCompleted(_ => callback());
    }

    /// <summary>Registers a completion callback with the separate cooperative completion token.</summary>
    /// <remarks>Callbacks run in reverse registration order before lifecycle completion hooks and resource disposal.</remarks>
    public void OnCompleted(Func<CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (_completed)
        {
            throw new InvalidOperationException("The request has already completed.");
        }

        (_completedCallbacks ??= []).Add(callback);
    }

    /// <summary>Transfers ownership of a resource to the request for asynchronous disposal after completion hooks.</summary>
    /// <remarks>
    ///     Resources are disposed in reverse registration order, before the service scope. Do not register resources
    ///     already owned by dependency injection. Each registration is disposed once, including on failures and aborts.
    /// </remarks>
    public void RegisterForDisposeAsync(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_ended || _disposing)
        {
            throw new InvalidOperationException("Request resource registration has closed.");
        }

        (_resources ??= []).Add(resource);
    }

    internal void EndProcessing(bool aborted = false)
    {
        _ended    =  true;
        IsAborted |= aborted;
        Headers.Freeze();
    }

    internal async ValueTask RunCompletedAsync(Action<Exception> report, CancellationToken ct)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        for (var index = (_completedCallbacks?.Count ?? 0) - 1; index >= 0; index--)
        {
            try
            {
                await _completedCallbacks![index](ct);
            }
            catch (Exception exception)
            {
                report(exception);
            }
        }

        _completedCallbacks = null;
        _startingCallbacks  = null;
        LifecycleStarting   = null;
    }

    internal async ValueTask DisposeResourcesAsync(Action<Exception> report)
    {
        if (_disposing)
        {
            return;
        }

        _disposing = true;
        for (var index = (_resources?.Count ?? 0) - 1; index >= 0; index--)
        {
            try
            {
                await _resources![index].DisposeAsync();
            }
            catch (Exception exception)
            {
                report(exception);
            }
        }

        _resources = null;
    }

    private async ValueTask RunStartingAsync(CancellationToken ct)
    {
        if (_starting)
        {
            throw new InvalidOperationException("A response startup callback cannot start or write the response.");
        }

        _starting = true;
        try
        {
            while (_startingCallbacks is { Count: > 0 })
            {
                var callback = _startingCallbacks[^1];
                _startingCallbacks.RemoveAt(_startingCallbacks.Count - 1);
                await callback(ct);
            }

            if (LifecycleStarting is {} starting)
            {
                await starting(ct);
            }
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>Rejects writes after completion or abortion and writes from receiving or starting hooks.</summary>
    protected void EnsureCanWrite()
    {
        if (_ended)
        {
            throw new InvalidOperationException("The response has completed or been aborted.");
        }

        if (ReceivingRequest)
        {
            throw new InvalidOperationException("Request-received lifecycle hooks cannot write the response. Use middleware to short-circuit requests.");
        }

        if (_starting)
        {
            throw new InvalidOperationException("A response startup callback cannot start, write, or flush the response.");
        }
    }

    /// <summary>Records successful transport completion and freezes any remaining headers.</summary>
    protected void MarkCompleted() => EndProcessing();

    /// <summary>Records transport abortion and prevents further response writes.</summary>
    protected void MarkAborted()
    {
        if (ReceivingRequest || _starting || _completed)
        {
            throw new InvalidOperationException("Lifecycle hooks and completion callbacks cannot abort the response.");
        }

        EndProcessing(true);
    }
}
