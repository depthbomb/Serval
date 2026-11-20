using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Serval.Configuration.Tests;

public sealed class HostConfigurationTests
{
    [Fact]
    public void ReplacesAppsettingsAndLoadsEnvironmentFile()
    {
        var files = new TestFiles();
        files.Write("appsettings.json", "{\"JsonOnly\":\"removed\",\"Server\":{\"Port\":1}}");
        files.Write("appsettings.Development.json", "{\"EnvironmentJsonOnly\":\"removed\"}");
        files.Write(".env", "Server__Port=8000\nBASE_ONLY=kept");
        files.Write(".env.Development", "APP_PORT=9000");
        var builder = files.CreateHost();
        builder.UseDotEnvConfiguration(Options());
        using var host = builder.Build();
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("9000", configuration["Server:Port"]);
        Assert.Equal("kept", configuration["BASE_ONLY"]);
        Assert.Null(configuration["JsonOnly"]);
        Assert.Null(configuration["EnvironmentJsonOnly"]);
        Assert.Equal(Environments.Development, builder.Environment.EnvironmentName);
        Assert.Equal(files.DirectoryPath, builder.Environment.ContentRootPath);
        Assert.Equal(Environments.Development, configuration[HostDefaults.EnvironmentKey]);
    }

    [Fact]
    public void PreservesOtherSourcesAndTheirPrecedence()
    {
        var files = new TestFiles();
        files.Write(".env", "APP_PORT=8000");
        files.Write("secrets.json", "{\"APP_PORT\":\"9000\"}");
        var builder = files.CreateHost();
        builder.Configuration.AddJsonFile("secrets.json");
        builder.UseDotEnvConfiguration(Options());
        using var host = builder.Build();
        Assert.Equal("9000", builder.Configuration["Server:Port"]);
        Assert.Single(builder.Configuration.GetSection("Server").GetChildren());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "9999" });
        Assert.Equal("9999", builder.Configuration["Server:Port"]);
    }

    [Fact]
    public void EnvironmentAndArgumentsOverrideFilesUsingEitherKeyStyle()
    {
        var alias = "SERVAL_PORT_" + Guid.NewGuid().ToString("N");
        var files = new TestFiles();
        files.Write(".env", "Server__Port=8000");
        files.Write(".env.Development", $"{alias}=8100");
        var options = new DotEnvConfigurationOptions { ReloadOnChange = false, KeyMappings = { [alias] = "Server:Port" } };
        Environment.SetEnvironmentVariable(alias, "8200");
        try
        {
            var environment = files.CreateHost().UseDotEnvConfiguration(options);
            using var environmentHost = environment.Build();
            Assert.Equal("8200", environment.Configuration["Server:Port"]);

            var arguments = files.CreateHost($"--{alias}=8300").UseDotEnvConfiguration(options);
            using var argumentHost = arguments.Build();
            Assert.Equal("8300", arguments.Configuration["Server:Port"]);

            var canonical = files.CreateHost("--Server:Port=8400", $"--{alias}=8500").UseDotEnvConfiguration(options);
            using var canonicalHost = canonical.Build();
            Assert.Equal("8400", canonical.Configuration["Server:Port"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(alias, null);
        }
    }

    [Fact]
    public void ExplicitEnvironmentKeyWinsOverAliasInSameSource()
    {
        var prefix = "SERVAL_" + Guid.NewGuid().ToString("N");
        var alias = prefix + "_PORT";
        var section = prefix + ":Port";
        Environment.SetEnvironmentVariable(alias, "8200");
        Environment.SetEnvironmentVariable(prefix + "__Port", "8300");
        try
        {
            var builder = new TestFiles().CreateHost().UseDotEnvConfiguration(new DotEnvConfigurationOptions
            {
                ReloadOnChange = false,
                KeyMappings   = { [alias] = section },
            });
            using var host = builder.Build();
            Assert.Equal("8300", builder.Configuration[section]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(alias, null);
            Environment.SetEnvironmentVariable(prefix + "__Port", null);
        }
    }

    [Fact]
    public void AllowsMissingFilesAndDisabledHostDefaults()
    {
        var files = new TestFiles();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = files.DirectoryPath,
        });
        builder.UseDotEnvConfiguration();
        using var host = builder.Build();
        Assert.Null(builder.Configuration["Server:Port"]);
    }

    [Fact]
    public void SupportsAbsolutePathsAndDisablingEnvironmentFile()
    {
        var files = new TestFiles();
        var path = files.Write("custom.env", "Server__Port=8000");
        files.Write("custom.env.Development", "Server__Port=9000");
        var builder = new TestFiles().CreateHost().UseDotEnvConfiguration(new DotEnvConfigurationOptions
        {
            Path                = path,
            Optional            = false,
            ReloadOnChange      = false,
            LoadEnvironmentFile = false,
        });
        using var host = builder.Build();
        Assert.Equal("8000", builder.Configuration["Server:Port"]);
    }

    [Fact]
    public void KeepsHostBootstrapSettingsSelectedBeforeDotEnv()
    {
        var files = new TestFiles();
        files.Write(".env", "DOTNET_ENVIRONMENT=Production");
        files.Write(".env.Development", "SELECTED=development");
        files.Write(".env.Production", "SELECTED=production");
        var builder = files.CreateHost().UseDotEnvConfiguration();
        using var host = builder.Build();
        Assert.Equal(Environments.Development, host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.Equal("development", builder.Configuration["SELECTED"]);
    }

    [Fact]
    public void RejectsRepeatRegistration()
    {
        var builder = new TestFiles().CreateHost().UseDotEnvConfiguration();
        using var host = builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.UseDotEnvConfiguration());
    }

    [Fact]
    public async Task SupportsSlimWebApplicationBuilder()
    {
        var files = new TestFiles();
        files.Write(".env", "APP_PORT=8120");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = files.DirectoryPath,
            EnvironmentName = Environments.Development,
        });
        builder.UseDotEnvConfiguration(Options());
        await using var app = builder.Build();
        Assert.Equal("8120", app.Configuration["Server:Port"]);
    }

    private static DotEnvConfigurationOptions Options() => new()
    {
        ReloadOnChange = false,
        KeyMappings = { ["APP_PORT"] = "Server:Port" },
    };
}
