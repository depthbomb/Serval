#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text;
using Serval.Http;

namespace Serval.Tests.Support;

internal sealed class MemoryResponse : Response
{
    public MemoryStream Body    { get; } = new();
    public bool         Aborted { get; private set; }
    public string       Text    => Encoding.UTF8.GetString(Body.ToArray());

    public override ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanWrite();
        return ValueTask.CompletedTask;
    }

    public override async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (HasEnded)
        {
            return;
        }

        await StartAsync(cancellationToken);
        MarkCompleted();
    }

    public override ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        MarkAborted();
        Aborted = true;
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    protected override ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => Body.WriteAsync(bytes, cancellationToken);
}
