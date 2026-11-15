#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Transport.Http3.Protocol;

namespace Serval.Tests.Transports.Http3;

public sealed class Http3HeaderTests
{
    [Theory]
    [InlineData(":authority", "example.com/unexpected")]
    [InlineData(":authority", "user@example.com")]
    [InlineData(":authority", "example.com?query")]
    [InlineData(":path", "//evil.example/path")]
    [InlineData(":path", "/some\\path")]
    [InlineData(":path", "/some path")]
    [InlineData(":method", "GET /other")]
    [InlineData(":scheme", "http")]
    public void MalformedPseudoHeadersAreRejected(string name, string value)
    {
        var fields = ValidHeaders().Select(field => field.Name == name ? (field.Name, value) : field).ToList();
        Assert.Throws<Http3ProtocolException>(() => RequestHeaders.Parse(fields));
    }

    [Theory]
    [InlineData("connection", "keep-alive")]
    [InlineData("transfer-encoding", "chunked")]
    [InlineData("te", "gzip")]
    [InlineData("content-length", "-1")]
    [InlineData("content-length", "+1")]
    [InlineData("Upper-Case", "value")]
    [InlineData("host", "different.example")]
    [InlineData("x-field", " value")]
    [InlineData("x-field", "value ")]
    [InlineData("x-field", "value\r\ninjection")]
    public void InvalidOrdinaryHeadersAreRejected(string name, string value)
    {
        var fields = ValidHeaders();
        fields.Add((name, value));
        Assert.Throws<Http3ProtocolException>(() => RequestHeaders.Parse(fields));
    }

    [Fact]
    public void PseudoHeadersMustBeUniqueAndPrecedeOrdinaryHeaders()
    {
        var duplicate = ValidHeaders();
        duplicate.Add((":path", "/other"));
        Assert.Throws<Http3ProtocolException>(() => RequestHeaders.Parse(duplicate));
        var misplaced = ValidHeaders();
        misplaced.Insert(0, ("accept", "*/*"));
        Assert.Throws<Http3ProtocolException>(() => RequestHeaders.Parse(misplaced));
    }

    private static List<(string Name, string Value)> ValidHeaders() => [(":method", "GET"), (":scheme", "https"), (":authority", "example.com"), (":path", "/test")];
}
