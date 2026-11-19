#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;

namespace Serval.Tests.Core.Http;

public sealed class RequestBodyStorageSecurityTests
{
    [UnixFact]
    public async Task SpilledRequestBodiesAreAccessibleOnlyToTheirOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "serval-body-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new FormOptions { MemoryThreshold = 1, TempDirectory = directory };
        await using (var storage = new RequestBodyStorage(options))
        {
            await storage.AppendAsync("private request fixture"u8.ToArray(), CancellationToken.None);
            var file = Assert.Single(Directory.GetFiles(directory));
            var permissions = File.GetUnixFileMode(file);
            Assert.Equal((UnixFileMode)0, permissions & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite));

            await using var stream = storage.OpenRead(0, storage.Length);
            using var reader = new StreamReader(stream);
            Assert.Equal("private request fixture", await reader.ReadToEndAsync());
        }

        Assert.Empty(Directory.GetFiles(directory));
    }

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
            {
                Skip = "Unix file permissions require a Unix filesystem.";
            }
        }
    }
}
