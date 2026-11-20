using System.Text;
using Microsoft.Extensions.Configuration;

namespace Serval.Configuration.Tests;

public sealed class DotEnvFileTests
{
    [Theory]
    [InlineData("VALUE=hello", "hello")]
    [InlineData("  VALUE = hello world  # comment", "hello world")]
    [InlineData("VALUE=https://example.test/#fragment", "https://example.test/#fragment")]
    [InlineData("VALUE=", "")]
    [InlineData("VALUE= # comment", "")]
    [InlineData("export VALUE=' literal # value ' # comment", " literal # value ")]
    [InlineData("export\tVALUE=ok", "ok")]
    [InlineData("VALUE=\"one\\ntwo\\tthree\\r\\\"\\\\\"", "one\ntwo\tthree\r\"\\")]
    [InlineData("VALUE='C:\\new\\test'", "C:\\new\\test")]
    [InlineData("VALUE=\"C:\\unknown\"", "C:\\unknown")]
    [InlineData("VALUE='line 1\r\nline 2'", "line 1\nline 2")]
    [InlineData("VALUE=\"line 1\nline 2\"", "line 1\nline 2")]
    [InlineData("VALUE=${OTHER} $(command)", "${OTHER} $(command)")]
    [InlineData("\n# comment\nVALUE=a=b=c\n", "a=b=c")]
    public void ReadsDotEnvSyntax(string content, string expected)
    {
        using var configuration = new TestFiles().Load(content);
        Assert.Equal(expected, configuration["value"]);
    }

    [Fact]
    public void ReadsBomAndNormalizesSectionsWithoutChangingSingleUnderscores()
    {
        var files = new TestFiles();
        var path = files.Write(".env", "");
        File.WriteAllText(path, "APP_PORT=8120\r\nServer__Port=8121\r\nLogging__LogLevel__Microsoft.Hosting.Lifetime=Warning", new UTF8Encoding(true));
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddDotEnvFile(path).Build();
        Assert.Equal("8120", configuration["APP_PORT"]);
        Assert.Null(configuration["APP:PORT"]);
        Assert.Equal("8121", configuration["server:port"]);
        Assert.Equal("Warning", configuration["Logging:LogLevel:Microsoft.Hosting.Lifetime"]);
    }

    [Theory]
    [InlineData("value", 1)]
    [InlineData("=hidden-value", 1)]
    [InlineData("BAD KEY=hidden-value", 1)]
    [InlineData("KEY=hidden-value\nkey=another", 2)]
    [InlineData("A__B=hidden-value\na:b=another", 2)]
    [InlineData("A____B=hidden-value", 1)]
    [InlineData("VALUE='hidden-value", 1)]
    [InlineData("VALUE=\"hidden-value\" trailing", 1)]
    public void RejectsMalformedFilesWithoutIncludingValues(string content, int line)
    {
        var error = Assert.Throws<InvalidDataException>(() => new TestFiles().Load(content));
        var syntax = Assert.IsType<FormatException>(error.InnerException);
        Assert.Contains($"line {line}", syntax.Message);
        Assert.DoesNotContain("hidden-value", error.ToString());
    }

    [Fact]
    public void MapsConventionalKeysAndKeepsOriginalNames()
    {
        using var configuration = new TestFiles().Load("DATABASE_URL=Data Source=catalog.db\nAPP_PORT=8120", source =>
        {
            source.KeyMappings.Add("DATABASE_URL", "ConnectionStrings:Catalog");
            source.KeyMappings.Add("APP_PORT", "Server__Port");
        });
        Assert.Equal("Data Source=catalog.db", configuration.GetConnectionString("Catalog"));
        Assert.Equal(configuration.GetConnectionString("Catalog"), configuration["DATABASE_URL"]);
        Assert.Equal("8120", configuration["Server:Port"]);
        Assert.Single(configuration.GetSection("Server").GetChildren());
    }

    [Theory]
    [InlineData("APP_PORT=8000\nServer__Port=9000", "9000")]
    [InlineData("Server__Port=9000\nAPP_PORT=8000", "9000")]
    [InlineData("APP_PORT=8000\nServer__Port=", "")]
    public void ExplicitDestinationWinsWithinFileRegardlessOfLineOrder(string content, string expected)
    {
        using var configuration = new TestFiles().Load(content, source => source.KeyMappings.Add("APP_PORT", "Server:Port"));
        Assert.Equal(expected, configuration["Server:Port"]);
    }

    [Fact]
    public void OptionalFilesCanBeMissingButRequiredFilesFail()
    {
        var files = new TestFiles();
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().SetBasePath(files.DirectoryPath).AddDotEnvFile(".env", optional: true).Build();
        Assert.Empty(configuration.AsEnumerable());
        Assert.Throws<FileNotFoundException>(() => new ConfigurationBuilder().SetBasePath(files.DirectoryPath).AddDotEnvFile(".env").Build());
    }

    [Fact]
    public void ReloadRemovesDeletedKeysAndUsesMappingSnapshot()
    {
        var files = new TestFiles();
        DotEnvConfigurationSource? captured = null;
        using var configuration = files.Load("APP_PORT=8120\nSTALE=old", source =>
        {
            source.KeyMappings.Add("APP_PORT", "Server:Port");
            captured = source;
        });
        captured!.KeyMappings["APP_PORT"] = "Changed:Port";
        files.Write(".env", "APP_PORT=8121");
        configuration.Reload();
        Assert.Equal("8121", configuration["Server:Port"]);
        Assert.Null(configuration["Changed:Port"]);
        Assert.Null(configuration["STALE"]);
    }

    [Fact]
    public void DoesNotExportValuesToProcessEnvironment()
    {
        var key = "SERVAL_CONFIGURATION_" + Guid.NewGuid().ToString("N");
        using var configuration = new TestFiles().Load($"{key}=file-value");
        Assert.Equal("file-value", configuration[key]);
        Assert.Null(Environment.GetEnvironmentVariable(key));
    }

    [Theory]
    [InlineData("APP__PORT", "Server:Port")]
    [InlineData("APP_PORT", "Server::Port")]
    [InlineData("APP_PORT", "")]
    [InlineData("APP_PORT", "app_port")]
    public void RejectsInvalidMappings(string alias, string destination)
    {
        Assert.Throws<ArgumentException>(() => new TestFiles().Load("", source => source.KeyMappings.Add(alias, destination)));
    }

    [Fact]
    public void RejectsAmbiguousAndChainedMappings()
    {
        Assert.Throws<ArgumentException>(() => new TestFiles().Load("", source =>
        {
            source.KeyMappings.Add("PORT_A", "Server:Port");
            source.KeyMappings.Add("PORT_B", "server__port");
        }));
        Assert.Throws<ArgumentException>(() => new TestFiles().Load("", source =>
        {
            source.KeyMappings.Add("PORT_A", "PORT_B");
            source.KeyMappings.Add("PORT_B", "Server:Port");
        }));
    }
}
