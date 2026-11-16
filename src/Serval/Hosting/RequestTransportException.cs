#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Hosting;

/// <summary>Signals a transport failure that must bypass application error handling.</summary>
public class RequestTransportException : IOException
{
    /// <summary>Creates an exception describing a transport failure.</summary>
    /// <param name="message">A description of the protocol or connection failure.</param>
    public RequestTransportException(string message) : base(message) {}
}
