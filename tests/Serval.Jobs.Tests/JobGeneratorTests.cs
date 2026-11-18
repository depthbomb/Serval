#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Reflection;
using Caprine.CronTimer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Serval.Hosting;
using Serval.Jobs.Generators;

namespace Serval.Jobs.Tests;

public sealed class JobGeneratorTests
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp15);
    private static readonly MetadataReference[] References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                                                                       .Concat([typeof(IJob).Assembly.Location, typeof(ServalBuilder).Assembly.Location, typeof(CronExpression).Assembly.Location, typeof(IServiceCollection).Assembly.Location])
                                                                       .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path))
    ];

    [Fact]
    public async Task EscapedIdentifiersUseTheSameDefaultNameAsManualRegistration()
    {
        var (compilation, diagnostics) = Generate("""
                                                  using System.Threading;
                                                  using System.Threading.Tasks;
                                                  using Serval.Jobs;
                                                  namespace @namespace;
                                                  [PeriodicJob(1000)]
                                                  public sealed class @event : IJob
                                                  {
                                                      public ValueTask ExecuteAsync(CancellationToken ct) => default;
                                                  }
                                                  """);
        Assert.Empty(diagnostics);
        using var stream = new MemoryStream();
        var       result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());
        var register = assembly.GetType("Serval.Generated.JobGeneratorTestJobs")!.GetMethod("Register")!.CreateDelegate<Func<ServalBuilder, ServalBuilder>>();
        var services = new ServiceCollection().AddLogging();
        var builder  = services.AddServal();
        Assert.Same(builder, register(builder));
        await using var provider = services.BuildServiceProvider();
        var             job      = Assert.Single(provider.GetRequiredService<JobRegistry>().GetJobs());
        Assert.Equal(job.JobType.FullName, job.Name);
    }

    [Fact]
    public void EmptyAssembliesDoNotGetRegistrationMethods()
    {
        var (compilation, diagnostics) = Generate("public class Empty {}");
        Assert.Empty(diagnostics);
        Assert.Single(compilation.SyntaxTrees);
    }

    [Theory]
    [InlineData("internal class Example : IJob", "SERVALJOB001")]
    [InlineData("public abstract class Example : IJob", "SERVALJOB001")]
    [InlineData("public class Example<T> : IJob", "SERVALJOB001")]
    [InlineData("public class Example", "SERVALJOB001")]
    [InlineData("file class Example : IJob", "SERVALJOB001")]
    [InlineData("public struct Example : IJob", "SERVALJOB001")]
    public void UnsupportedTypesProduceDiagnostics(string declaration, string id)
    {
        var (_, diagnostics) = Generate($$"""
                                          using System.Threading;
                                          using System.Threading.Tasks;
                                          using Serval.Jobs;
                                          [PeriodicJob(1000)]
                                          {{declaration}}
                                          {
                                              public ValueTask ExecuteAsync(CancellationToken ct) => default;
                                          }
                                          """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Theory]
    [InlineData("[PeriodicJob(0)]")]
    [InlineData("[PeriodicJob(double.NaN)]")]
    [InlineData("[PeriodicJob(double.PositiveInfinity)]")]
    [InlineData("[PeriodicJob(0.5)]")]
    [InlineData("[PeriodicJob(4294967295D)]")]
    [InlineData("[PeriodicJob(1, Name = \" \")]")]
    [InlineData("[PeriodicJob(1), CronJob(\"* * * * *\")]")]
    [InlineData("[CronJob(\" \")]")]
    [InlineData("[CronJob(\"* * * * *\", TimeZoneId = \" \")]")]
    [InlineData("[CronJob(\"* * * * *\", Options = (Caprine.CronTimer.CronExpressionOptions)16)]")]
    public void InvalidAttributeArgumentsProduceDiagnostics(string attribute)
    {
        var (_, diagnostics) = Generate($$"""
                                          using System.Threading;
                                          using System.Threading.Tasks;
                                          using Serval.Jobs;
                                          {{attribute}}
                                          public class Example : IJob
                                          {
                                              public ValueTask ExecuteAsync(CancellationToken ct) => default;
                                          }
                                          """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SERVALJOB002");
    }

    [Fact]
    public void DuplicateNamesProduceDiagnostics()
    {
        var (_, diagnostics) = Generate("""
                                        using System.Threading;
                                        using System.Threading.Tasks;
                                        using Serval.Jobs;
                                        [PeriodicJob(1000, Name = "same")]
                                        public class One : IJob { public ValueTask ExecuteAsync(CancellationToken ct) => default; }
                                        [CronJob("* * * * *", Name = "same")]
                                        public class Two : IJob { public ValueTask ExecuteAsync(CancellationToken ct) => default; }
                                        """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SERVALJOB003");
    }

    [Fact]
    public void AssemblyNamesStartingWithDigitsProduceValidRegistrationIdentifiers()
    {
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Serval.Jobs;
                              [PeriodicJob(1000)]
                              public class Example : IJob { public ValueTask ExecuteAsync(CancellationToken ct) => default; }
                              """;
        var             compilation = CSharpCompilation.Create("123.Jobs", [CSharpSyntaxTree.ParseText(source, ParseOptions)], References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver      = CSharpGeneratorDriver.Create([new JobGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.DoesNotContain(generated.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task GeneratedRegistrationCompilesAndRegistersBothScheduleKinds()
    {
        var (compilation, diagnostics) = Generate("""
                                                  using System.Threading;
                                                  using System.Threading.Tasks;
                                                  using Serval.Jobs;
                                                  using Serval.Generated;
                                                  using Caprine.CronTimer;
                                                  using Microsoft.Extensions.DependencyInjection;
                                                  using Serval.Hosting;
                                                  namespace Example;
                                                  [PeriodicJob(1500, Name = "periodic\"job")]
                                                  public partial class Periodic : IJob
                                                  {
                                                      public ValueTask ExecuteAsync(CancellationToken ct) => default;
                                                  }
                                                  public partial class Periodic { }
                                                  [CronJob("*/2 * * * * *", Options = CronExpressionOptions.IncludeSeconds, TimeZoneId = "UTC")]
                                                  public sealed class Calendar : IJob
                                                  {
                                                      public ValueTask ExecuteAsync(CancellationToken ct) => default;
                                                  }
                                                  public static class Registration
                                                  {
                                                      public static void Register(IServiceCollection services) => services.AddServal().AddGeneratedJobs();
                                                  }
                                                  """);
        Assert.Empty(diagnostics);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = compilation.SyntaxTrees.Last().ToString();
        Assert.Contains("extension(global::Serval.Hosting.ServalBuilder builder)", generated);
        Assert.Contains("public static class @JobGeneratorTestJobs", generated);
        Assert.DoesNotContain("GetTypes", generated);
        using var stream = new MemoryStream();
        var       result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());
        var register = assembly.GetType("Example.Registration")!.GetMethod("Register")!.CreateDelegate<Action<IServiceCollection>>();
        var services = new ServiceCollection().AddLogging();
        register(services);
        await using var provider = services.BuildServiceProvider();
        var             registry = provider.GetRequiredService<JobRegistry>();
        Assert.Equal(2, registry.GetJobs().Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), registry.GetJob("periodic\"job").Schedule.Period);
        Assert.Equal(CronExpressionOptions.IncludeSeconds, registry.GetJob("Example.Calendar").Schedule.Expression!.Options);
    }

    private static (Compilation Compilation, IEnumerable<Diagnostic> Diagnostics) Generate(string source)
    {
        var             compilation = CSharpCompilation.Create("JobGeneratorTest", [CSharpSyntaxTree.ParseText(source, ParseOptions)], References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver      = CSharpGeneratorDriver.Create([new JobGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        return (generated, diagnostics);
    }
}
