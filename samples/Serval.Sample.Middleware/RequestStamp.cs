#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Sample.Middleware;

public sealed class RequestStamp
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
}
