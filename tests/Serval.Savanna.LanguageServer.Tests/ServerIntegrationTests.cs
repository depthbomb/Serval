#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Serval.Savanna.LanguageServer.Rpc;

namespace Serval.Savanna.LanguageServer.Tests;

public sealed class ServerIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StdioServerLoadsRealProjectAndHandlesUnsavedTemplates(bool linkSupport)
    {
        var root          = FindRoot();
        var configuration = typeof(ServerIntegrationTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var server = Environment.GetEnvironmentVariable("SAVANNA_TEST_SERVER")
                     ?? Path.Combine(root, "src", "Serval.Savanna.LanguageServer", "bin", configuration, "net11.0", "Serval.Savanna.LanguageServer.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = root,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        start.ArgumentList.Add(server);
        using var process     = Process.Start(start)!;
        var       errorOutput = process.StandardError.ReadToEndAsync();
        using var timeout     = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var       protocol    = new Protocol(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var       id          = 0;
        try
        {
            var initialized = await Request("initialize", new
            {
                capabilities = new
                {
                    textDocument = new
                    {
                        definition = new
                        {
                            linkSupport,
                        },
                    },
                },
            });
            Assert.Equal("utf-16", initialized.GetProperty("capabilities").GetProperty("positionEncoding").GetString());
            Assert.Contains(initialized.GetProperty("capabilities").GetProperty("semanticTokensProvider").GetProperty("legend").GetProperty("tokenTypes").EnumerateArray(), token => token.GetString() == "property");
            await Notify("initialized", new
                {});
            var          path   = Path.Combine(root, "samples", "Serval.Sample.Savanna", "Views", "Products", "Index.svn");
            var          uri    = new Uri(path).AbsoluteUri.Replace("file:///C:", "file:///c%3A", StringComparison.Ordinal);
            const string header = "{% model Serval.Sample.Savanna.ProductIndexModel %}\n";
            await Notify("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri,
                    languageId = "savanna",
                    version    = 1,
                    text       = header + "{{ Model.",
                },
            });
            var completion = await Request("textDocument/completion", new
            {
                textDocument = new
                {
                    uri,
                },
                position = new
                {
                    line      = 1,
                    character = 9,
                },
            });
            Assert.Contains(completion.GetProperty("items").EnumerateArray(), item => item.GetProperty("label").GetString() == "Title");

            await Change(header + "😀 {{ Model.Title }} {{ Model.Missing }}", 2);
            var hover = await Request("textDocument/hover", new
            {
                textDocument = new
                {
                    uri,
                },
                position = new
                {
                    line      = 1,
                    character = 13,
                },
            });
            Assert.Contains("Title", hover.GetProperty("contents").GetProperty("value").GetString());
            var definition = await Request("textDocument/definition", new
            {
                textDocument = new
                {
                    uri,
                },
                position = new
                {
                    line      = 1,
                    character = 13,
                },
            });
            Assert.EndsWith("CatalogController.cs", Assert.Single(definition.EnumerateArray()).GetProperty(linkSupport ? "targetUri" : "uri").GetString());
            var diagnostics = await Request("textDocument/diagnostic", new
            {
                textDocument = new
                {
                    uri,
                },
            });
            Assert.Contains(diagnostics.GetProperty("items").EnumerateArray(), item => item.GetProperty("code").GetString() == "CS1061");

            // A delayed edit must not replace the newer unsaved buffer.
            await Change(header + "{{ Model.Title }}", 1);
            var stale = await Request("textDocument/diagnostic", new
            {
                textDocument = new
                {
                    uri,
                },
            });
            Assert.Contains(stale.GetProperty("items").EnumerateArray(), item => item.GetProperty("code").GetString() == "CS1061");

            await Change(header + "{{ Model.Title }}", 3);
            var fixedDiagnostics = await Request("textDocument/diagnostic", new
            {
                textDocument = new
                {
                    uri,
                },
            });
            Assert.DoesNotContain(fixedDiagnostics.GetProperty("items").EnumerateArray(), item => item.GetProperty("source").GetString() == "C#");
            await Notify("textDocument/didClose", new
            {
                textDocument = new
                {
                    uri,
                },
            });

            var cardPath = Path.Combine(root, "samples", "Serval.Sample.Savanna", "Views", "Products", "Card.svn");
            var card     = await File.ReadAllTextAsync(cardPath, timeout.Token);
            var cardUri  = new Uri(cardPath).AbsoluteUri.Replace("file:///C:", "file:///c%3A", StringComparison.Ordinal);
            var cardText = SourceText.From(card);
            await Notify("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri        = cardUri,
                    languageId = "savanna",
                    version    = 1,
                    text       = card,
                },
            });
            foreach (var name in new[] { "Product", "Model", "Url", "ToString" })
            {
                var position = cardText.Lines.GetLinePosition(card.IndexOf(name, StringComparison.Ordinal) + 1);
                var target = await Request("textDocument/definition", new
                {
                    textDocument = new
                    {
                        uri = cardUri,
                    },
                    position = new
                    {
                        line      = position.Line,
                        character = position.Character,
                    },
                });
                var location   = Assert.Single(target.EnumerateArray());
                var targetPath = new Uri(location.GetProperty(linkSupport ? "targetUri" : "uri").GetString()!).LocalPath;
                Assert.True(File.Exists(targetPath), targetPath);
                Assert.EndsWith(name == "ToString" ? "Decimal.cs" : "CatalogController.cs", targetPath);
                if (linkSupport)
                {
                    var origin      = location.GetProperty("originSelectionRange");
                    var startOffset = Position.Read(origin.GetProperty("start")).Offset(cardText);
                    var endOffset   = Position.Read(origin.GetProperty("end")).Offset(cardText);
                    Assert.Equal(name, card[startOffset..endOffset]);
                }
            }

            var semantic = await Request("textDocument/semanticTokens/full", new
            {
                textDocument = new
                {
                    uri = cardUri,
                },
            });
            Assert.NotEmpty(semantic.GetProperty("data").EnumerateArray());
            await Notify("textDocument/didClose", new
            {
                textDocument = new
                {
                    uri = cardUri,
                },
            });

            var importsPath = Path.Combine(root, "samples", "Serval.Sample.Savanna", "Views", "_Imports.svn");
            var imports     = await File.ReadAllTextAsync(importsPath, timeout.Token);
            var importsUri  = new Uri(importsPath).AbsoluteUri;
            await Notify("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri        = importsUri,
                    languageId = "savanna",
                    version    = 1,
                    text       = imports,
                },
            });
            var importsDiagnostics = await Request("textDocument/diagnostic", new
            {
                textDocument = new
                {
                    uri = importsUri,
                },
            });
            Assert.Empty(importsDiagnostics.GetProperty("items").EnumerateArray());
            await Notify("textDocument/didClose", new
            {
                textDocument = new
                {
                    uri = importsUri,
                },
            });

            var customRoot = Path.Combine(Path.GetTempPath(), "ServalSavannaServerTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(customRoot, "Templates", "Layouts"));
            var projectFile = Path.Combine(customRoot, "Custom.csproj");
            await File.WriteAllTextAsync(projectFile, new XElement("Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net11.0"), new XElement("SavannaRoot", "Templates")),
                new XElement("Import", new XAttribute("Project", Path.Combine(root, "src", "Serval.Savanna", "build", "Serval.Savanna.targets")))).ToString(), timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(customRoot, "Templates", "Layouts", "Base.svn"), "{% model string %}{% layout %}", timeout.Token);
            var restoreStart = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory       = customRoot,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            foreach (var argument in new[] { "restore", projectFile, "--disable-build-servers", "--verbosity", "quiet" })
            {
                restoreStart.ArgumentList.Add(argument);
            }

            using var restore       = Process.Start(restoreStart)!;
            var       restoreOutput = restore.StandardOutput.ReadToEndAsync(timeout.Token);
            var       restoreErrors = restore.StandardError.ReadToEndAsync(timeout.Token);
            await restore.WaitForExitAsync(timeout.Token);
            Assert.True(restore.ExitCode == 0, await restoreOutput + await restoreErrors);
            var customUri = new Uri(Path.Combine(customRoot, "Templates", "Page.svn")).AbsoluteUri;
            await Notify("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri        = customUri,
                    languageId = "savanna",
                    version    = 1,
                    text       = "{% model string %}\n{% extends \"Layouts/",
                },
            });
            var paths = await Request("textDocument/completion", new
            {
                textDocument = new
                {
                    uri = customUri,
                },
                position = new
                {
                    line      = 1,
                    character = 20,
                },
            });
            Assert.Contains(paths.GetProperty("items").EnumerateArray(), item => item.GetProperty("label").GetString() == "Layouts/Base.svn");
            await Notify("textDocument/didClose", new
            {
                textDocument = new
                {
                    uri = customUri,
                },
            });
            await Request("shutdown", new
                {});
            await Notify("exit", new
                {});
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await errorOutput);

            Task Change(string text, int version)
            {
                return Notify("textDocument/didChange", new
                {
                    textDocument = new
                    {
                        uri,
                        version,
                    },
                    contentChanges = new[]
                    {
                        new
                        {
                            text,
                        },
                    },
                });
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        return;

        async Task<JsonElement> Request(string method, object parameters)
        {
            var requestId = ++id;
            await protocol.WriteAsync(new
            {
                jsonrpc = "2.0",
                id      = requestId,
                method,
                @params = parameters,
            }, timeout.Token);
            using var response = await protocol.ReadAsync(timeout.Token);
            Assert.True(response is not null, process.HasExited ? await errorOutput : "The server closed its output stream.");
            var message = response.RootElement;
            Assert.Equal(requestId, message.GetProperty("id").GetInt32());
            Assert.False(message.TryGetProperty("error", out _), message.ToString());
            return message.GetProperty("result").Clone();
        }

        Task Notify(string method, object parameters)
        {
            return protocol.WriteAsync(new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
            }, timeout.Token);
        }
    }

    private static string FindRoot()
    {
        for (var directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Serval.slnx")))
            {
                return directory;
            }
        }

        throw new DirectoryNotFoundException("Cannot find the Serval solution.");
    }
}
