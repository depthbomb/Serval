#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Transport.Http2.Protocol;

namespace Serval.Transport.Http2;

internal sealed class Http2RequestStream : IDisposable
{
    public           int                     Id                   { get; }
    public           RequestHead             Head                 { get; }
    public           Http2BodyStream         Body                 { get; }
    public           CancellationToken       Token                => _lifetime.Token;
    public           bool                    IsReset              => Volatile.Read(ref _reset) != 0;
    public           bool                    RemoteEnded          { get; set; }
    public           bool                    HandlerCompleted     { get; set; }
    public           int                     ReceiveWindow        { get; set; } = Frames.InitialWindow;
    public           int                     PendingReceiveCredit { get; set; }
    public           long                    ReceivedBytes        { get; set; }
    public           TaskCompletionSource    Completion           { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private          int                     _disposed;
    private          int                     _reset;
    private readonly CancellationTokenSource _lifetime;

    public Http2RequestStream(int                                     id,
                              RequestHead                             head,
                              Func<int, CancellationToken, ValueTask> consumed,
                              CancellationToken                       connectionClosed)
    {
        Id        = id;
        Head      = head;
        Body      = new Http2BodyStream(consumed);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(connectionClosed);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Body.Dispose();
        _lifetime.Dispose();
    }

    public async ValueTask<bool> ResetAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _reset, 1) != 0)
        {
            return false;
        }

        Body.Complete(new OperationCanceledException("The peer or server reset this HTTP/2 stream."));
        try
        {
            await _lifetime.CancelAsync();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        return true;
    }
}
