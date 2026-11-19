#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Runtime.InteropServices;
using System.Text;
using Serval.Http.Constants;
using Serval.Http.StaticFiles;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Http;

public sealed class StaticFileSecurityTests
{
    [ShortNameFact]
    public async Task ShortFileNamesCannotExposeDotFiles()
    {
        var root = CreateRoot();
        var file = Path.Combine(root, ".configuration.txt");
        await File.WriteAllTextAsync(file, "private fixture");
        var alias = ShortName(file);
        Assert.NotEqual(Path.GetFileName(file), alias);
        await using var app = new TestApplication(options => options.StaticFiles.DirectoryPath = root);
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/" + alias)).StatusCode);
        Assert.Equal(file, new StaticFileResolver(root, serveDotFiles: true).Resolve("/" + alias));
    }

    [ShortNameFact]
    public async Task ShortDirectoryNamesCannotExposeDotDirectories()
    {
        var root = CreateRoot();
        var directory = Path.Combine(root, ".configuration");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.txt"), "private fixture");
        var alias = ShortName(directory);
        Assert.NotEqual(Path.GetFileName(directory), alias);
        await using var app = new TestApplication(options => options.StaticFiles.DirectoryPath = root);
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/" + alias + "/settings.txt")).StatusCode);
    }

    [ShortNameFact]
    public async Task ShortExtensionsCannotBypassTheContentTypePolicy()
    {
        var root = CreateRoot();
        var file = Path.Combine(root, "configuration.txtprivate");
        await File.WriteAllTextAsync(file, "private fixture");
        var alias = ShortName(file);
        Assert.Equal(".TXT", Path.GetExtension(alias));
        await using var app = new TestApplication(options => options.StaticFiles.DirectoryPath = root);
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/" + alias)).StatusCode);
    }

    [Fact]
    public async Task LiteralTildesRemainUsableInPublicFileNames()
    {
        var root = CreateRoot();
        var file = Path.Combine(root, "public~1.txt");
        await File.WriteAllTextAsync(file, "public fixture");
        await using var app = new TestApplication(options => options.StaticFiles.DirectoryPath = root);
        Assert.Equal("public fixture", (await app.SendAsync("/public~1.txt")).Text);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "serval-static-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ShortName(string path)
    {
        var buffer = new StringBuilder(32_768);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        Assert.InRange(length, 1u, (uint)buffer.Capacity - 1);
        return Path.GetFileName(buffer.ToString());
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetShortPathName(string path, StringBuilder result, uint length);

    private sealed class ShortNameFactAttribute : FactAttribute
    {
        public ShortNameFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Short file names require Windows.";
                return;
            }

            var file = Path.Combine(CreateRoot(), ".configuration.txt");
            File.WriteAllText(file, "fixture");
            if (ShortName(file) == Path.GetFileName(file))
            {
                Skip = "Short file name creation is disabled on the temporary volume.";
            }
        }
    }
}
