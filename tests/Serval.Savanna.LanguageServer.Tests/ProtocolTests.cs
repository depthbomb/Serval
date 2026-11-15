#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Text.Json;
using Serval.Savanna.LanguageServer.Rpc;
using Serval.Savanna.LanguageServer.Workspace;

namespace Serval.Savanna.LanguageServer.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void DecodesRiderUrisOnceIncludingEncodedDriveColons()
    {
        var path = OperatingSystem.IsWindows() ? @"c:\Views\a %20 #.svn" : "/tmp/Views/a %20 #.svn";
        var uri  = new Uri(path).AbsoluteUri.Replace("c:", "c%3A", StringComparison.Ordinal);
        Assert.Equal(path, DocumentUri.ToPath(uri));
        Assert.Throws<ArgumentException>(() => DocumentUri.ToPath("https://example.test/Index.svn"));
    }

    [Theory]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: 16777217\r\n\r\n")]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}")]
    public async Task RejectsInvalidOrUnboundedFrames(string message)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(message));
        await Assert.ThrowsAsync<InvalidDataException>(() => new Protocol(input, Stream.Null).ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8192)]
    public async Task FragmentedAndCoalescedFramesPreserveLargeUtf8Bodies(int chunkSize)
    {
        using var bytes  = new MemoryStream();
        var       writer = new Protocol(Stream.Null, bytes);
        object[]  values = ["🌿 héllo", new string('x', 20000) + "🙂", true];
        foreach (var value in values)
        {
            await writer.WriteAsync(new { value });
        }

        using var input  = new ChunkedStream(bytes.ToArray(), chunkSize);
        var       reader = new Protocol(input, Stream.Null);
        foreach (var value in values)
        {
            using var message = await reader.ReadAsync(CancellationToken.None);
            Assert.Equal(JsonSerializer.Serialize(value), message!.RootElement.GetProperty("value").GetRawText());
        }

        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Content-Length: 2\r\n")]
    [InlineData("Content-Length: 2\r\n\r\n{")]
    public async Task TruncatedFramesFailInsteadOfReturningPartialMessages(string message)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(message));
        await Assert.ThrowsAsync<EndOfStreamException>(() => new Protocol(input, Stream.Null).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HeaderSizeLimitIncludesTheTerminator()
    {
        const string prefix  = "Content-Length: 2\r\nX-Extra: ";
        var          header  = prefix + new string('x', 8192 - prefix.Length - 4) + "\r\n\r\n";
        using var    valid   = new MemoryStream(Encoding.ASCII.GetBytes(header + "{}"));
        using var    message = await new Protocol(valid, Stream.Null).ReadAsync(CancellationToken.None);
        Assert.NotNull(message);
        using var invalid = new MemoryStream(Encoding.ASCII.GetBytes(header.Replace("X-Extra", "X-Extras") + "{}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new Protocol(invalid, Stream.Null).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void IncrementalChangesUseUtf16AndIgnoreStaleVersions()
    {
        using var workspace = new SavannaWorkspace();
        var       path      = Path.Combine(Path.GetTempPath(), "SavannaEditorTests", "Index.svn");
        workspace.Open(path, "😀 title\r\nother", 1);
        var changes = JsonSerializer.SerializeToElement(new[] { new { range = new { start = new { line = 0, character = 3 }, end = new { line = 0, character = 8 } }, text = "name" } });
        Assert.True(workspace.Change(path, changes, 2));
        Assert.False(workspace.Change(path, changes, 1));
        Assert.Equal("😀 name\r\nother", Assert.Single(workspace.OpenDocuments).Text.ToString());
        Assert.Equal(2, Assert.Single(workspace.OpenDocuments).Version);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Position(0, 100).Offset(SourceText.From("small")));
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "42")]
    [InlineData(true, "42")]
    public void InvalidEditTextDoesNotChangeDocumentOrVersion(bool incremental, string value)
    {
        using var workspace = new SavannaWorkspace();
        var       path      = Path.Combine(Path.GetTempPath(), "SavannaEditorTests", "Index.svn");
        workspace.Open(path, "original", 1);
        var       range   = incremental ? "\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":3}}," : "";
        using var changes = JsonDocument.Parse("[{\"text\":\"first edit\"},{" + range + "\"text\":" + value + "}]");
        Assert.Throws<ArgumentException>(() => workspace.Change(path, changes.RootElement, 2));
        Assert.Equal("original", Assert.Single(workspace.OpenDocuments).Text.ToString());
        Assert.Equal(1, Assert.Single(workspace.OpenDocuments).Version);

        var deletion = JsonSerializer.SerializeToElement(new[] { new { text = "" } });
        Assert.True(workspace.Change(path, deletion, 2));
        Assert.Equal("", Assert.Single(workspace.OpenDocuments).Text.ToString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"id\":7,\"method\":null}")]
    [InlineData("{\"id\":7,\"method\":42}")]
    public async Task InvalidRequestStringsAreRejectedWithoutStoppingTheServer(string json)
    {
        using var input   = new MemoryStream();
        using var output  = new MemoryStream();
        var       writer  = new Protocol(Stream.Null, input);
        using var invalid = JsonDocument.Parse(json);
        await writer.WriteAsync(invalid.RootElement);
        await writer.WriteAsync(new { id     = 1, method = "initialize" });
        await writer.WriteAsync(new { id     = 2, method = "shutdown" });
        await writer.WriteAsync(new { method = "exit" });
        input.Position = 0;

        using var workspace = new SavannaWorkspace();
        Assert.Equal(0, await new LanguageServer(new Protocol(input, output), workspace).RunAsync());
        output.Position = 0;
        var       reader    = new Protocol(output, Stream.Null);
        using var rejection = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(-32600, rejection!.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        using var initialized = await reader.ReadAsync(CancellationToken.None);
        Assert.True(initialized!.RootElement.TryGetProperty("result", out _));
        using var shutdown = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(2, shutdown!.RootElement.GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("uri", "null")]
    [InlineData("uri", "42")]
    [InlineData("text", "null")]
    [InlineData("text", "42")]
    public async Task InvalidDocumentStringsProduceInvalidParamsWithoutOpeningDocument(string property, string value)
    {
        using var input  = new MemoryStream();
        using var output = new MemoryStream();
        var       writer = new Protocol(Stream.Null, input);
        await writer.WriteAsync(new { id = 1, method = "initialize" });
        var       uri        = property == "uri" ? value : JsonSerializer.Serialize(new Uri(Path.Combine(Path.GetTempPath(), "Index.svn")).AbsoluteUri);
        var       text       = property == "text" ? value : "\"valid\"";
        using var parameters = JsonDocument.Parse("{\"textDocument\":{\"uri\":" + uri + ",\"text\":" + text + ",\"version\":1}}");
        await writer.WriteAsync(new { id     = 2, method = "textDocument/didOpen", @params = parameters.RootElement });
        await writer.WriteAsync(new { id     = 3, method = "shutdown" });
        await writer.WriteAsync(new { method = "exit" });
        input.Position = 0;

        using var workspace = new SavannaWorkspace();
        Assert.Equal(0, await new LanguageServer(new Protocol(input, output), workspace).RunAsync());
        Assert.Empty(workspace.OpenDocuments);
        output.Position = 0;
        var       reader      = new Protocol(output, Stream.Null);
        using var initialized = await reader.ReadAsync(CancellationToken.None);
        using var rejection   = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(2, rejection!.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32602, rejection.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    private sealed class ChunkedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
