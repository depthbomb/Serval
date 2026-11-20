using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PackageConsumer.Generated;
using Serval;
using Serval.Configuration;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http;
using Serval.Jobs;
using Serval.Routing;

namespace PackageConsumer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        foreach (var path in Directory.GetFiles(args[0], "*.snupkg"))
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".pdb", StringComparison.Ordinal)))
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                buffer.Position = 0;
                using var provider = MetadataReaderProvider.FromPortablePdbStream(buffer);
                var metadata = provider.GetMetadataReader();
                var sourceLinks = metadata.CustomDebugInformation.Select(metadata.GetCustomDebugInformation)
                    .Where(info => metadata.GetGuid(info.Kind) == new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A")).ToArray();
                if (sourceLinks.Length != 1)
                {
                    throw new InvalidDataException($"Missing Source Link in {path}: {entry.FullName}");
                }

                using var sourceLink = JsonDocument.Parse(metadata.GetBlobBytes(sourceLinks[0].Value));
                var documents = sourceLink.RootElement.GetProperty("documents").EnumerateObject().ToArray();
                var expected = $"https://raw.githubusercontent.com/depthbomb/Serval/{args[1]}/";
                if (!documents.Any(document => document.Value.GetString()!.StartsWith(expected, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Incorrect Source Link commit in {path}: {entry.FullName}");
                }
            }

            var packageId = Path.GetFileNameWithoutExtension(archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).FullName);
            _ = Assembly.Load(packageId).GetExportedTypes();
        }

        var dotenvPath = Path.Combine(AppContext.BaseDirectory, "package.env");
        await File.WriteAllTextAsync(dotenvPath, "APP_PORT=8120\nServer__Name=package-consumer");
        var builder = Host.CreateApplicationBuilder();
        builder.UseDotEnvConfiguration(new DotEnvConfigurationOptions
        {
            Path                = dotenvPath,
            Optional            = false,
            ReloadOnChange      = false,
            LoadEnvironmentFile = false,
            KeyMappings         = { ["APP_PORT"] = "Server:Port" },
        });
        builder.Services.AddSingleton<IValidateOptions<PackageOptions>, PackageOptionsValidator>();
        builder.Services.AddOptions<PackageOptions>().BindConfiguration("Server").ValidateOnStart();
        builder.Services.AddServal().AddGeneratedControllers().AddGeneratedJobs();
        using var host = builder.Build();
        var settings = host.Services.GetRequiredService<IOptions<PackageOptions>>().Value;
        if (settings.Port != 8120 || settings.Name != "package-consumer")
        {
            throw new InvalidOperationException("Dotenv configuration and generated options binding failed.");
        }

        var jobs = host.Services.GetRequiredService<JobRegistry>();
        if (jobs.GetJobs().Count != 1 || jobs.GetJob("package-check").Schedule.Period != TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("Generated job registration failed.");
        }

        var html = await Views.Page.RenderToStringAsync("<package>");
        if (!html.Contains("<p>&lt;package&gt;</p>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Generated Savanna template failed: {html}");
        }

        if (Assets.GetUrl("src/main.js") != "/build/assets/main.js")
        {
            throw new InvalidOperationException("Generated Vite manifest lookup failed.");
        }

        Console.WriteLine($"Package consumer passed on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}.");
    }
}

public sealed class PackageOptions
{
    [Range(1, 65_535)]
    public int Port { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;
}

[OptionsValidator]
public sealed partial class PackageOptionsValidator : IValidateOptions<PackageOptions> {}

[Controller]
public sealed class PackageController : ControllerBase
{
    [Get("/package-check")]
    public ValueTask<IActionResult> IndexAsync() => TextAsync("Package controller");
}

[PeriodicJob(60_000, Name = "package-check")]
public sealed class PackageJob : IJob
{
    public ValueTask ExecuteAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
