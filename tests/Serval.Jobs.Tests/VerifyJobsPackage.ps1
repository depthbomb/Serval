param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('Serval.Jobs.Package-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $workspace 'feed'
$artifacts = Join-Path $workspace 'artifacts'
$consumer = Join-Path $workspace 'consumer with spaces'
New-Item -ItemType Directory -Path $feed -Force | Out-Null
New-Item -ItemType Directory -Path $consumer -Force | Out-Null

# A unique stamp catches stale analyzer paths when packaging to a custom output directory.
$stamp = 'JobsPackageVerification-' + [Guid]::NewGuid().ToString('N')
& dotnet pack (Join-Path $repo 'src/Serval.Jobs/Serval.Jobs.csproj') --configuration $Configuration --artifacts-path $artifacts --disable-build-servers -p:GeneratePackageOnBuild=false "-p:InformationalVersion=$stamp"
if ($LASTEXITCODE -ne 0)
{
    throw "Jobs package build failed in $workspace."
}

$packages = @(Get-ChildItem -LiteralPath $artifacts -Recurse -Filter 'Serval.Jobs.*.nupkg')
$generators = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'bin') -Recurse -Filter 'Serval.Jobs.Generators.dll')
if ($packages.Count -ne 1 -or $generators.Count -ne 1)
{
    throw 'Expected one jobs package and one built generator.'
}

$archive = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try
{
    $entry = $archive.GetEntry('analyzers/dotnet/cs/Serval.Jobs.Generators.dll')
    if ($null -eq $entry)
    {
        throw 'The jobs package is missing its generator.'
    }
    $stream = $entry.Open()
    try
    {
        $packedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
    }
    finally
    {
        $stream.Dispose()
    }
    if ($packedHash -ne (Get-FileHash -LiteralPath $generators[0].FullName -Algorithm SHA256).Hash)
    {
        throw 'The jobs package contains a stale generator.'
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('Serval.Jobs.nuspec').Open())
    try
    {
        $nuspec = [xml]$reader.ReadToEnd()
        $version = $nuspec.package.metadata.version
    }
    finally
    {
        $reader.Dispose()
    }
}
finally
{
    $archive.Dispose()
}
Copy-Item -LiteralPath $packages[0].FullName -Destination $feed

& dotnet pack (Join-Path $repo 'src/Serval/Serval.csproj') --configuration $Configuration --artifacts-path $artifacts --no-build --no-restore -p:GeneratePackageOnBuild=false -o $feed
if ($LASTEXITCODE -ne 0)
{
    throw 'Could not package the core dependency from the same build.'
}

$escapedFeed = [Security.SecurityElement]::Escape($feed)
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <LangVersion>15.0</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RestoreSources>$escapedFeed;https://api.nuget.org/v3/index.json</RestoreSources>
    <RestorePackagesPath>packages</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Serval.Jobs" Version="$version"/>
    <PackageReference Include="Caprine.CronTimer" Version="0.1.0"/>
    <PackageReference Include="Caprine.Durations" Version="0.1.0"/>
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj') -Encoding utf8

@'
using Caprine.CronTimer;
using Caprine.Durations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serval.Generated;
using Serval.Hosting;
using Serval.Jobs;

public static class Program
{
    public static async Task Main()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<CounterService>();
        builder.Services.AddServal().AddGeneratedJobs();
        var host = builder.Build();
        await using var lifetime = (IAsyncDisposable)host;
        var registry = host.Services.GetRequiredService<JobRegistry>();
        if (registry.GetJob("periodic").Schedule.Period != Schedules.Interval()
            || !ReferenceEquals(registry.GetJob("cron").Schedule.Expression, Schedules.Calendar()))
        {
            throw new Exception("Generated schedule members were not used.");
        }
        foreach (var job in registry.GetJobs())
        {
            registry.Pause(job.Name);
        }
        await host.StartAsync();
        foreach (var job in registry.GetJobs())
        {
            if (!await registry.RunAsync(job.Name))
            {
                throw new Exception("A manual run was rejected.");
            }
        }
        await host.StopAsync();
        if (registry.GetJobs().Count != 2 || host.Services.GetRequiredService<CounterService>().Count != 2)
        {
            throw new Exception("Package registration or execution failed.");
        }
        Console.WriteLine("Package consumer passed: generated Serval registration, Caprine duration and cron members, DI, and host lifecycle.");
    }
}

public sealed class CounterService
{
    public int Count;
}

internal static partial class Schedules
{
    [GeneratedDuration("1m", DurationParseOptions.Strict)]
    internal static partial TimeSpan Interval();

    [GeneratedCronExpression("0 0 * * *")]
    internal static partial CronExpression Calendar();
}

[PeriodicJob(typeof(Schedules), nameof(Schedules.Interval), Name = "periodic")]
public sealed class PeriodicJob : IJob
{
    private readonly CounterService _counter;
    public PeriodicJob(CounterService counter) => _counter = counter;
    public ValueTask ExecuteAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _counter.Count);
        return ValueTask.CompletedTask;
    }
}

[CronJob(typeof(Schedules), nameof(Schedules.Calendar), Name = "cron")]
public sealed class CalendarJob : IJob
{
    private readonly CounterService _counter;
    public CalendarJob(CounterService counter) => _counter = counter;
    public ValueTask ExecuteAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _counter.Count);
        return ValueTask.CompletedTask;
    }
}

'@ | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs') -Encoding utf8

& dotnet build (Join-Path $consumer 'Consumer.csproj') --configuration Release --disable-build-servers
if ($LASTEXITCODE -ne 0)
{
    throw "Package consumer build failed in $workspace."
}

& dotnet (Join-Path $consumer 'bin/Release/net11.0/Consumer.dll')
if ($LASTEXITCODE -ne 0)
{
    throw "Package consumer execution failed in $workspace."
}
