#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Transport.Http2.Protocol;

internal sealed class SendFlowControl
{
    private          TaskCompletionSource  _changed = NewSignal();
    private          bool                  _closed;
    private          long                  _connectionWindow    = Frames.InitialWindow;
    private          int                   _initialStreamWindow = Frames.InitialWindow;
    private readonly Lock                  _gate                = new();
    private readonly Dictionary<int, long> _streamWindows       = [];

    public void Open(int streamId)
    {
        lock (_gate)
        {
            _streamWindows.Add(streamId, _initialStreamWindow);
        }
    }

    public void Remove(int streamId)
    {
        lock (_gate)
        {
            _streamWindows.Remove(streamId);
            Signal();
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            Signal();
        }
    }

    public void UpdateInitialWindow(int value)
    {
        lock (_gate)
        {
            var difference = (long)value - _initialStreamWindow;
            // Avoid a capturing predicate and keep the dictionary's struct enumerator.
            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
            foreach (var pair in _streamWindows)
            {
                if (pair.Value + difference > int.MaxValue)
                {
                    throw new Http2ProtocolException(Http2Error.FlowControl, "The initial window update overflowed a stream window.");
                }
            }

            foreach (var id in _streamWindows.Keys.ToArray())
            {
                // A SETTINGS reduction can make a stream window negative. Later credit must pay that debt first.
                _streamWindows[id] += difference;
            }

            _initialStreamWindow = value;
            Signal();
        }
    }

    public void Update(int streamId, int increment)
    {
        if (increment == 0)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "A flow-control increment must be positive.", streamId);
        }

        lock (_gate)
        {
            if (streamId == 0)
            {
                if (_connectionWindow + increment > int.MaxValue)
                {
                    throw new Http2ProtocolException(Http2Error.FlowControl, "The connection window overflowed.");
                }

                _connectionWindow += increment;
            }
            else if (_streamWindows.TryGetValue(streamId, out var current))
            {
                if (current + increment > int.MaxValue)
                {
                    throw new Http2ProtocolException(Http2Error.FlowControl, "The stream window overflowed.", streamId);
                }

                _streamWindows[streamId] = current + increment;
            }

            Signal();
        }
    }

    public async ValueTask<int> ReserveAsync(int streamId, int maximum, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                if (_closed || !_streamWindows.TryGetValue(streamId, out var available))
                {
                    throw new OperationCanceledException("The HTTP/2 stream is closed.", cancellationToken);
                }

                var count = (int)Math.Min(maximum, Math.Min(_connectionWindow, available));
                if (count > 0)
                {
                    _connectionWindow        -= count;
                    _streamWindows[streamId] =  available - count;
                    return count;
                }

                // Capture the signal under the same lock as the window check so an update can't be missed.
                wait = _changed.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    public void Refund(int streamId, int count)
    {
        lock (_gate)
        {
            _connectionWindow += count;
            if (_streamWindows.ContainsKey(streamId))
            {
                _streamWindows[streamId] += count;
            }

            Signal();
        }
    }

    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
