using System.CodeDom.Compiler;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Serval.Configuration.Tests;

public sealed class BindingOptions
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Range(1, 65_535)]
    public int Port { get; set; }

    public bool Enabled { get; set; }

    public string[] Hosts { get; set; } = [];
}

[OptionsValidator]
public sealed partial class BindingOptionsValidator : IValidateOptions<BindingOptions> {}

public sealed class GeneratedOptionsTests
{
    [Theory]
    [InlineData("Server__Name=example\nServer__Port=8120")]
    [InlineData("APP_NAME=example\nAPP_PORT=8120")]
    public async Task GeneratedBindingAndValidationSupportBothKeyStyles(string settings)
    {
        var files = new TestFiles();
        files.Write(".env", settings + "\nServer__Enabled=true\nServer__Hosts__0=one\nServer__Hosts__1=two");
        var builder = files.CreateHost().UseDotEnvConfiguration(Options());
        builder.Services.AddSingleton<IValidateOptions<BindingOptions>, BindingOptionsValidator>();
        builder.Services.AddOptions<BindingOptions>().BindConfiguration("Server").ValidateOnStart();
        using var host = builder.Build();
        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<BindingOptions>>().Value;
        Assert.Equal("example", options.Name);
        Assert.Equal(8120, options.Port);
        Assert.True(options.Enabled);
        Assert.Equal(["one", "two"], options.Hosts);
        await host.StopAsync();

        var generated = typeof(BindingOptionsValidator).GetMethod("Validate")!.GetCustomAttribute<GeneratedCodeAttribute>();
        Assert.NotNull(generated);
        Assert.Equal("Microsoft.Extensions.Options.SourceGeneration", generated.Tool);
    }

    [Theory]
    [InlineData("APP_NAME=example\nAPP_PORT=0", "Port")]
    [InlineData("APP_NAME=\nAPP_PORT=8120", "Name")]
    [InlineData("APP_PORT=8120", "Name")]
    public async Task GeneratedValidationFailsAtStartup(string settings, string member)
    {
        var files = new TestFiles();
        files.Write(".env", settings);
        var builder = files.CreateHost().UseDotEnvConfiguration(Options());
        builder.Services.AddSingleton<IValidateOptions<BindingOptions>, BindingOptionsValidator>();
        builder.Services.AddOptions<BindingOptions>().BindConfiguration("Server").ValidateOnStart();
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(error.Failures, failure => failure.Contains(member, StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedGetBindsMappedSections()
    {
        using var configuration = new TestFiles().Load("APP_NAME=example\nAPP_PORT=8120", source =>
        {
            source.KeyMappings.Add("APP_NAME", "Server:Name");
            source.KeyMappings.Add("APP_PORT", "Server:Port");
        });
        var options = configuration.GetSection("Server").Get<BindingOptions>();
        Assert.NotNull(options);
        Assert.Equal("example", options.Name);
        Assert.Equal(8120, options.Port);
    }

    [Fact]
    public async Task FileChangesUpdateGeneratedOptionsMonitor()
    {
        var files = new TestFiles();
        files.Write(".env", "APP_NAME=initial\nAPP_PORT=8120");
        var builder = files.CreateHost().UseDotEnvConfiguration(Options(reload: true));
        builder.Services.AddSingleton<IValidateOptions<BindingOptions>, BindingOptionsValidator>();
        builder.Services.AddOptions<BindingOptions>().BindConfiguration("Server").ValidateOnStart();
        using var host = builder.Build();
        var monitor = host.Services.GetRequiredService<IOptionsMonitor<BindingOptions>>();
        Assert.Equal(8120, monitor.CurrentValue.Port);
        var changed = new TaskCompletionSource<BindingOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = monitor.OnChange(options =>
        {
            if (options.Port == 8121)
            {
                changed.TrySetResult(options);
            }
        });
        files.Write(".env", "APP_NAME=updated\nServer__Port=8121");
        var updated = await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("updated", updated.Name);
        Assert.Equal(8121, monitor.CurrentValue.Port);
    }

    private static DotEnvConfigurationOptions Options(bool reload = false) => new()
    {
        ReloadOnChange = reload,
        KeyMappings =
        {
            ["APP_NAME"] = "Server:Name",
            ["APP_PORT"] = "Server:Port",
        },
    };
}
