#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Serval.Savanna.Compiler;
using Serval.Savanna.LanguageServer.Features;
using Serval.Savanna.LanguageServer.Rpc;
using Serval.Savanna.LanguageServer.Workspace;

namespace Serval.Savanna.LanguageServer;

public sealed class LanguageServer
{
    private bool _definitionLinks;
    private bool _diagnosticRefresh;
    private bool _fileWatchRegistration;
    private bool _initialized;
    private int  _refreshId;
    private bool _shutdown;

    private readonly Protocol                                              _protocol;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new();
    private readonly SavannaWorkspace                                      _workspace;

    public LanguageServer(Protocol protocol, SavannaWorkspace workspace)
    {
        _protocol  = protocol;
        _workspace = workspace;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        // One queue owns workspace state; the reader stays available to receive cancellation.
        var       queue    = Channel.CreateUnbounded<(JsonElement Message, string Method, CancellationTokenSource Cancellation)>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var       worker   = ProcessAsync();
        try
        {
            while (await _protocol.ReadAsync(lifetime.Token) is {} input)
            {
                using (input)
                {
                    var message = input.RootElement.Clone();
                    if (message.ValueKind != JsonValueKind.Object)
                    {
                        await ErrorAsync(null, -32600, "A request must be an object.");
                        continue;
                    }

                    if (!message.TryGetProperty("method", out var method))
                    {
                        continue;
                    }

                    if (method.ValueKind != JsonValueKind.String || method.GetString() is not {} methodName)
                    {
                        await ErrorAsync(message.TryGetProperty("id", out var invalidId) ? invalidId : null, -32600, "A request method must be a string.");
                        continue;
                    }

                    if (methodName == "$/cancelRequest")
                    {
                        var key = message.GetProperty("params").GetProperty("id").GetRawText();
                        if (_requests.TryGetValue(key, out var pending))
                        {
                            try
                            {
                                await pending.CancelAsync();
                            }
                            catch (ObjectDisposedException) {}
                        }

                        continue;
                    }

                    var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    if (message.TryGetProperty("id", out var id))
                    {
                        if (!_requests.TryAdd(id.GetRawText(), requestCancellation))
                        {
                            requestCancellation.Dispose();
                            await ErrorAsync(id, -32600, "Duplicate request ID.");
                            continue;
                        }
                    }

                    await queue.Writer.WriteAsync((message, methodName, requestCancellation), lifetime.Token);
                    if (methodName == "exit")
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            queue.Writer.Complete();
            await worker;
        }

        return _shutdown ? 0 : 1;

        async Task ProcessAsync()
        {
            await foreach (var (message, method, requestCancellation) in queue.Reader.ReadAllAsync(cancellationToken))
            {
                using (requestCancellation)
                {
                    var hasId = message.TryGetProperty("id", out var id);
                    try
                    {
                        requestCancellation.Token.ThrowIfCancellationRequested();
                        var result = await HandleAsync(method, message.GetValueOrDefault("params"), requestCancellation.Token);
                        if (hasId)
                        {
                            // A canceled request must not leave half a JSON-RPC frame on the shared output stream.
                            await _protocol.WriteAsync(new
                            {
                                jsonrpc = "2.0",
                                id,
                                result
                            }, CancellationToken.None);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (hasId)
                        {
                            await ErrorAsync(id, -32800, "Request cancelled.");
                        }
                    }
                    catch (RpcException error)
                    {
                        if (hasId)
                        {
                            await ErrorAsync(id, error.Code, error.Message);
                        }
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(error);
                        if (hasId)
                        {
                            await ErrorAsync(id, error is ArgumentException or KeyNotFoundException ? -32602 : -32603, error.Message);
                        }
                    }
                    finally
                    {
                        if (hasId)
                        {
                            _requests.TryRemove(id.GetRawText(), out _);
                        }
                    }
                }
            }
        }
    }

    private async Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (method == "initialize")
        {
            if (_initialized)
            {
                throw new RpcException(-32600, "The server is already initialized.");
            }

            _initialized = true;
            _definitionLinks = parameters.GetValueOrDefault("capabilities").GetValueOrDefault("textDocument")
                                         .GetValueOrDefault("definition").GetValueOrDefault("linkSupport").ValueKind == JsonValueKind.True;
            var refresh = parameters.GetValueOrDefault("capabilities").GetValueOrDefault("workspace").GetValueOrDefault("diagnostics").GetValueOrDefault("refreshSupport");
            _diagnosticRefresh     = refresh.ValueKind                                                                                                                                                         == JsonValueKind.True;
            _fileWatchRegistration = parameters.GetValueOrDefault("capabilities").GetValueOrDefault("workspace").GetValueOrDefault("didChangeWatchedFiles").GetValueOrDefault("dynamicRegistration").ValueKind == JsonValueKind.True;
            return new
            {
                capabilities = new
                {
                    positionEncoding = "utf-16",
                    textDocumentSync = new
                    {
                        openClose = true,
                        change    = 2,
                        save      = true
                    },
                    completionProvider = new
                    {
                        triggerCharacters = new[] { ".", " ", "\"", "'", "/", "{", "#", "<" }
                    },
                    hoverProvider              = true,
                    definitionProvider         = true,
                    documentFormattingProvider = true,
                    semanticTokensProvider = new
                    {
                        legend = new
                        {
                            tokenTypes     = SemanticTokens.Types,
                            tokenModifiers = Array.Empty<string>()
                        },
                        full = true
                    },
                    diagnosticProvider = new
                    {
                        identifier            = "Serval.Savanna",
                        interFileDependencies = true,
                        workspaceDiagnostics  = false
                    },
                },
                serverInfo = new
                {
                    name    = "Serval.Savanna",
                    version = "0.2.1"
                },
            };
        }

        if (method == "initialized")
        {
            if (_fileWatchRegistration)
            {
                await _protocol.WriteAsync(new
                {
                    jsonrpc = "2.0",
                    id      = "savanna-watch",
                    method  = "client/registerCapability",
                    @params = new
                    {
                        registrations = new[]
                        {
                            new
                            {
                                id     = "savanna-project-files",
                                method = "workspace/didChangeWatchedFiles",
                                registerOptions = new
                                {
                                    watchers = new[]
                                    {
                                        new
                                        {
                                            globPattern = "**/*.{savanna,cs,csproj,props,targets,json,editorconfig}",
                                            kind        = 7
                                        }
                                    }
                                },
                            },
                        },
                    },
                }, CancellationToken.None);
            }

            return null;
        }

        if (method is "exit" or "$/setTrace")
        {
            return null;
        }

        if (!_initialized)
        {
            throw new RpcException(-32002, "Initialize the language server first.");
        }

        if (_shutdown)
        {
            throw new RpcException(-32600, "The language server has shut down.");
        }

        if (method == "shutdown")
        {
            _shutdown = true;
            return null;
        }

        if (method == "workspace/didChangeWatchedFiles")
        {
            var changed = parameters.GetProperty("changes").EnumerateArray().Select(change => DocumentUri.ToPath(change.GetRequiredString("uri")))
                                    .Where(path =>
                                    {
                                        var normalized = path.Replace('\\', '/');
                                        return !normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) && !normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                                               || normalized.EndsWith("/project.assets.json", StringComparison.OrdinalIgnoreCase);
                                    }).ToArray();
            _workspace.InvalidateTemplates(changed);
            if (changed.Any(path => !path.EndsWith(".svn", StringComparison.OrdinalIgnoreCase)))
            {
                _workspace.InvalidateProjects();
            }

            if (changed.Length > 0)
            {
                await RefreshAsync();
            }

            return null;
        }

        if (method == "workspace/didChangeConfiguration")
        {
            _workspace.InvalidateProjects();
            await RefreshAsync();
            return null;
        }

        if (!method.StartsWith("textDocument/", StringComparison.Ordinal))
        {
            throw new RpcException(-32601, "Unsupported method: " + method);
        }

        var textDocument = parameters.GetProperty("textDocument");
        var path         = DocumentUri.ToPath(textDocument.GetRequiredString("uri"));
        switch (method)
        {
            case "textDocument/didOpen":
                _workspace.Open(path, textDocument.GetRequiredString("text"), textDocument.GetProperty("version").GetInt32());
                await RefreshAsync();
                return null;
            case "textDocument/didChange":
                _workspace.Change(path, parameters.GetProperty("contentChanges"), textDocument.GetProperty("version").GetInt32());
                await RefreshAsync();
                return null;
            case "textDocument/didClose":
                _workspace.Close(path);
                await RefreshAsync();
                return null;
            case "textDocument/didSave":
                _workspace.InvalidateProjects();
                await RefreshAsync();
                return null;
        }

        var snapshot = await _workspace.SnapshotAsync(path, cancellationToken);
        if (method == "textDocument/formatting")
        {
            return TemplateFormatter.Format(snapshot.Document);
        }

        var csharp = _workspace.GetCSharpFeatures(snapshot);
        if (method == "textDocument/semanticTokens/full")
        {
            return SemanticTokens.Encode(snapshot.Document, csharp is null ? [] : await csharp.ClassifyAsync(cancellationToken));
        }

        if (method == "textDocument/diagnostic")
        {
            var compilation = SavannaCompiler.Compile(snapshot.Templates, "Serval.Savanna.Editor", cancellationToken);
            var diagnostics = compilation.Diagnostics.Where(diagnostic => diagnostic.Path == snapshot.Document.Path).Select(diagnostic => (object)new
            {
                range    = Position.Range(snapshot.Document.Text, diagnostic.Span),
                severity = 1,
                code     = diagnostic.Code,
                source   = "Savanna",
                message  = diagnostic.Message,
            }).ToList();
            if (csharp is not null)
            {
                diagnostics.AddRange(await csharp.DiagnosticsAsync(cancellationToken));
            }

            if (snapshot.ProjectError is not null)
            {
                diagnostics.Add(new
                {
                    range    = Position.Range(snapshot.Document.Text, default),
                    severity = 2,
                    code     = "SAVANNAIDE001",
                    source   = "Savanna",
                    message  = snapshot.ProjectError
                });
            }

            return new
            {
                kind  = "full",
                items = diagnostics
            };
        }

        var offset = Position.Read(parameters.GetProperty("position")).Offset(snapshot.Document.Text);
        if (method == "textDocument/completion")
        {
            var items = TemplateFeatures.Complete(snapshot, offset) ?? (csharp is null ? [] : await csharp.CompleteAsync(offset, cancellationToken));
            return new
            {
                isIncomplete = items.Length >= 300,
                items
            };
        }

        if (method == "textDocument/definition")
        {
            var target = TemplateFeatures.Definition(snapshot, offset) ?? (csharp is null ? null : await csharp.DefinitionAsync(offset, cancellationToken));
            return DefinitionResponse.Create(target, snapshot.Document, offset, _definitionLinks);
        }

        return method switch
        {
            "textDocument/hover" => TemplateFeatures.Hover(snapshot, offset) ?? (csharp is null ? null : await csharp.HoverAsync(offset, cancellationToken)),
            _                    => throw new RpcException(-32601, "Unsupported method: " + method),
        };
    }

    private Task ErrorAsync(JsonElement? id, int code, string message) => _protocol.WriteAsync(new
    {
        jsonrpc = "2.0",
        id,
        error = new
        {
            code,
            message
        }
    });

    private Task RefreshAsync() => _diagnosticRefresh
        ? _protocol.WriteAsync(new
        {
            jsonrpc = "2.0",
            id      = "savanna-refresh-" + ++_refreshId,
            method  = "workspace/diagnostic/refresh"
        })
        : Task.CompletedTask;

    private sealed class RpcException : Exception
    {
        public int Code { get; }

        public RpcException(int code, string message) : base(message)
        {
            Code = code;
        }
    }
}

internal static class JsonExtensions
{
    extension(JsonElement element)
    {
        public JsonElement GetValueOrDefault(string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

        public string GetRequiredString(string property)
        {
            var value = element.GetValueOrDefault(property);
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not {} text)
            {
                throw new ArgumentException($"'{property}' must be a string.", nameof(property));
            }

            return text;
        }
    }
}
