using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Serval.Configuration.Tests;

internal sealed class TestFiles
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "serval-configuration-" + Guid.NewGuid().ToString("N"));

    public TestFiles()
    {
        Directory.CreateDirectory(DirectoryPath);
    }

    public string Write(string name, string content)
    {
        var path = Path.Combine(DirectoryPath, name);
        File.WriteAllText(path, content);
        return path;
    }

    public ConfigurationRoot Load(string content, Action<DotEnvConfigurationSource>? configure = null)
    {
        Write(".env", content);
        return (ConfigurationRoot)new ConfigurationBuilder().SetBasePath(DirectoryPath).AddDotEnvFile(source =>
        {
            source.Path = ".env";
            configure?.Invoke(source);
        }).Build();
    }

    public HostApplicationBuilder CreateHost(params string[] args) => Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args            = args,
        ContentRootPath = DirectoryPath,
        EnvironmentName = Environments.Development,
    });
}
