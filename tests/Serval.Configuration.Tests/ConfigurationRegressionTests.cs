using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;

namespace Serval.Configuration.Tests;

public sealed class ConfigurationRegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidFileLeavesHostSourcesAndValuesIntact(bool missing)
    {
        var files = new TestFiles();
        if (!missing)
        {
            files.Write(".env", "INVALID");
        }
        var builder = files.CreateHost();
        using var configuration = builder.Configuration;
        configuration["Existing:Value"] = "preserved";
        var sources = configuration.Sources.ToArray();

        Assert.ThrowsAny<Exception>(() => builder.UseDotEnvConfiguration(new DotEnvConfigurationOptions { Optional = false }));
        Assert.Equal(sources, configuration.Sources);
        Assert.Equal("preserved", configuration["Existing:Value"]);
    }

    [Fact]
    public void PreservesProgrammaticConfigurationValues()
    {
        var builder = new TestFiles().CreateHost();
        using var configuration = builder.Configuration;
        configuration["APP_PORT"] = "8120";
        builder.UseDotEnvConfiguration(new DotEnvConfigurationOptions
        {
            ReloadOnChange = false,
            KeyMappings = { ["APP_PORT"] = "Server:Port" },
        });
        Assert.Equal("8120", configuration["Server:Port"]);
    }

    [Fact]
    public void InvalidEnvironmentFileLeavesHostUntouchedAndReleasesPreparedWatchers()
    {
        var builder = new TestFiles().CreateHost();
        using var configuration = builder.Configuration;
        var files = new CountingFileProvider();
        builder.Environment.ContentRootFileProvider = files;
        var sources = configuration.Sources.ToArray();
        Assert.Throws<InvalidDataException>(() => builder.UseDotEnvConfiguration());
        Assert.Equal(sources, configuration.Sources);
        Assert.Equal(0, files.ActiveRegistrations);
    }

    [Fact]
    public void MappedSectionsTrackReloadsAndSortArrayIndices()
    {
        var files = new TestFiles();
        files.Write("aliases.json", "{\"FIRST\":\"a\",\"SECOND\":\"b\",\"LAST\":\"c\"}");
        var builder = files.CreateHost();
        using var configuration = builder.Configuration;
        configuration.AddJsonFile("aliases.json");
        builder.UseDotEnvConfiguration(new DotEnvConfigurationOptions
        {
            ReloadOnChange = false,
            KeyMappings =
            {
                ["FIRST"] = "Services:Catalog:Hosts:0",
                ["SECOND"] = "Services:Catalog:Hosts:2",
                ["LAST"] = "Services:Catalog:Hosts:10",
                ["MISSING"] = "Services:Absent:Name",
            },
        });
        Assert.Equal(["Catalog"], configuration.GetSection("services").GetChildren().Select(child => child.Key));
        Assert.Equal(["0", "2", "10"], configuration.GetSection("Services:Catalog:Hosts").GetChildren().Select(child => child.Key));
        Assert.Empty(configuration.GetSection("Service").GetChildren());

        files.Write("aliases.json", "{\"MISSING\":\"added\"}");
        ((IConfigurationRoot)configuration).Reload();
        Assert.Equal(["Absent"], configuration.GetSection("services").GetChildren().Select(child => child.Key));
        Assert.Equal("added", configuration["Services:Absent:Name"]);
        Assert.Empty(configuration.GetSection("Services:Catalog:Hosts").GetChildren());
    }

    [Theory]
    [InlineData("A__B", true)]
    [InlineData("A___B", true)]
    [InlineData("A_:B", true)]
    [InlineData("A__0__B", true)]
    [InlineData("A____B", false)]
    [InlineData("A:__B", false)]
    [InlineData("A__", false)]
    [InlineData("__A", false)]
    public void SectionValidationHandlesAdjacentUnderscores(string key, bool valid)
    {
        if (valid)
        {
            using var configuration = new TestFiles().Load($"{key}=value");
            Assert.Equal("value", configuration[key.Replace("__", ":", StringComparison.Ordinal)]);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => new TestFiles().Load($"{key}=value"));
        }
    }

    [Fact]
    public void DuplicateMultilineValueReportsAssignmentLine()
    {
        var error = Assert.Throws<InvalidDataException>(() => new TestFiles().Load("VALUE=first\nVALUE='second\nline'"));
        Assert.Contains("line 2:", error.InnerException!.Message);
    }

    [Theory]
    [InlineData("APP_PORT=8120", "9000")]
    [InlineData("", "9000")]
    [InlineData("APP_PORT=8120\nServer__Port=8500", "8500")]
    public void FileMappingsFollowAliasAssignmentsWithoutOverridingExplicitKeys(string content, string expected)
    {
        using var configuration = new TestFiles().Load(content, source => source.KeyMappings.Add("APP_PORT", "Server:Port"));
        configuration["APP_PORT"] = "9000";
        Assert.Equal(expected, configuration["Server:Port"]);
        configuration["Server:Port"] = "9500";
        configuration["APP_PORT"] = "9999";
        Assert.Equal("9500", configuration["Server:Port"]);
    }

    [Fact]
    public void FileDeletionClearsExplicitMappingPrecedence()
    {
        var files = new TestFiles();
        using var configuration = files.Load("Server__Port=8120", source =>
        {
            source.Optional = true;
            source.KeyMappings.Add("APP_PORT", "Server:Port");
        });
        File.Delete(Path.Combine(files.DirectoryPath, ".env"));
        configuration.Reload();
        configuration["APP_PORT"] = "9000";
        configuration["APP_PORT"] = "9001";
        Assert.Equal("9001", configuration["Server:Port"]);
    }

    [Fact]
    public void ReadsGeneratedQuotedValuesAcrossBufferBoundaries()
    {
        var random = new Random(1847);
        var characters = "aZ09 #$()=:_-\\\"'\t\n\ré";
        using var provider = new DotEnvConfigurationProvider(new DotEnvConfigurationSource
        {
            Path = "generated.env",
            FileProvider = new NullFileProvider(),
        });
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var value = new string(Enumerable.Range(0, random.Next(0, 4_096)).Select(_ => characters[random.Next(characters.Length)]).ToArray());
            var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"VALUE=\"{escaped}\" # comment"));
            provider.Load(stream);
            Assert.True(provider.TryGet("VALUE", out var actual));
            Assert.Equal(value, actual);
        }
    }

    [Fact]
    public void PhysicalFileProviderRetainsPollingSettings()
    {
        using var files = new PhysicalFileProvider(new TestFiles().DirectoryPath, ExclusionFilters.None)
        {
            UsePollingFileWatcher = true,
            UseActivePolling = true,
        };
        using var provider = new DotEnvConfigurationProvider(new DotEnvConfigurationSource
        {
            Path = ".env",
            FileProvider = files,
        });
        var physical = Assert.IsType<PhysicalFileProvider>(provider.Source.FileProvider);
        Assert.True(physical.UsePollingFileWatcher);
        Assert.True(physical.UseActivePolling);
    }

    [Fact]
    public void RespectsCustomPhysicalFileProviderImplementation()
    {
        using var files = new VirtualPhysicalFileProvider(new TestFiles().DirectoryPath);
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddDotEnvFile(source =>
        {
            source.Path = ".env";
            source.FileProvider = files;
        }).Build();
        Assert.Equal("8120", configuration["Server:Port"]);
    }

    private sealed class VirtualPhysicalFileProvider : PhysicalFileProvider, IFileProvider
    {
        public VirtualPhysicalFileProvider(string root) : base(root) {}
        IFileInfo IFileProvider.GetFileInfo(string subpath) => new TextFileInfo();
        IChangeToken IFileProvider.Watch(string filter) => NullChangeToken.Singleton;
    }

    private sealed class TextFileInfo : IFileInfo
    {
        public bool Exists => true;
        public long Length => 17;
        public string? PhysicalPath => null;
        public string Name => ".env";
        public DateTimeOffset LastModified => DateTimeOffset.MinValue;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(Encoding.UTF8.GetBytes("Server__Port=8120"));
    }

    private sealed class CountingFileProvider : IFileProvider, IChangeToken
    {
        public int ActiveRegistrations { get; private set; }
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => true;

        public IFileInfo GetFileInfo(string subpath) => subpath == ".env" ? new TextFileInfo() : new InvalidFileInfo();
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IChangeToken Watch(string filter) => this;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
        {
            ActiveRegistrations++;
            return new Registration(this);
        }

        private sealed class Registration : IDisposable
        {
            private readonly CountingFileProvider _owner;
            public Registration(CountingFileProvider owner) => _owner = owner;
            public void Dispose() => _owner.ActiveRegistrations--;
        }
    }

    private sealed class InvalidFileInfo : IFileInfo
    {
        public bool Exists => true;
        public long Length => 7;
        public string? PhysicalPath => null;
        public string Name => ".env.Development";
        public DateTimeOffset LastModified => DateTimeOffset.MinValue;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(Encoding.UTF8.GetBytes("INVALID"));
    }
}
