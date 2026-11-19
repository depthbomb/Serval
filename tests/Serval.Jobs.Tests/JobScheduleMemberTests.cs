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

public sealed class JobScheduleMemberTests
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp15);
    private static readonly MetadataReference[] References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                                                                       .Concat([typeof(IJob).Assembly.Location, typeof(ServalBuilder).Assembly.Location, typeof(CronExpression).Assembly.Location, typeof(IServiceCollection).Assembly.Location])
                                                                       .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)),
    ];

    [Theory]
    [InlineData("public static TimeSpan Missing => default;")]
    [InlineData("private static TimeSpan Value => default;")]
    [InlineData("protected static TimeSpan Value => default;")]
    [InlineData("public TimeSpan Value => default;")]
    [InlineData("public static TimeSpan Value;")]
    [InlineData("public static int Value => 1000;")]
    [InlineData("public static TimeSpan? Value => null;")]
    [InlineData("public static TimeSpan Value { get; set; }")]
    [InlineData("public static TimeSpan Value { private get; set; }")]
    [InlineData("public static TimeSpan Value(int count = 1) => default;")]
    [InlineData("public static TimeSpan Value<T>() => default;")]
    [InlineData("public static Task<TimeSpan> Value() => Task.FromResult(default(TimeSpan));")]
    [InlineData("private static TimeSpan _value; public static ref TimeSpan Value() => ref _value;")]
    [InlineData("private static TimeSpan _value; public static ref readonly TimeSpan Value => ref _value;")]
    public void UnsupportedMembersProduceScheduleDiagnostics(string member)
    {
        var (_, diagnostics) = Generate($$"""
                                          using System;
                                          using System.Threading;
                                          using System.Threading.Tasks;
                                          using Serval.Jobs;
                                          public class Schedules { {{member}} }
                                          [PeriodicJob(typeof(Schedules), "Value")]
                                          public sealed class Example : IJob
                                          {
                                              public ValueTask ExecuteAsync(CancellationToken ct) => default;
                                          }
                                          """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SERVALJOB002");
    }

    [Theory]
    [InlineData("null", "Value")]
    [InlineData("typeof(Schedules<>)", "Value")]
    [InlineData("typeof(LocalSchedules)", "Value")]
    [InlineData("typeof(Schedules<int>)", " ")]
    [InlineData("typeof(ISchedules)", "Value")]
    public void InvalidScheduleTypesAndNamesProduceDiagnostics(string type, string member)
    {
        var (_, diagnostics) = Generate($$"""
                                          using System;
                                          using System.Threading;
                                          using System.Threading.Tasks;
                                          using Serval.Jobs;
                                          public class Schedules<T> { public static TimeSpan Value => TimeSpan.FromSeconds(1); }
                                          file class LocalSchedules { public static TimeSpan Value => TimeSpan.FromSeconds(1); }
                                          public interface ISchedules { static abstract TimeSpan Value { get; } }
                                          [PeriodicJob({{type}}, "{{member}}")]
                                          public sealed class Example : IJob
                                          {
                                              public ValueTask ExecuteAsync(CancellationToken ct) => default;
                                          }
                                          """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SERVALJOB002");
    }

    [Theory]
    [InlineData("Options = CronExpressionOptions.None")]
    [InlineData("Options = CronExpressionOptions.IncludeSeconds")]
    [InlineData("TimeZoneId = \" \"")]
    public void CronMemberOptionsAndEmptyTimeZonesProduceDiagnostics(string arguments)
    {
        var (_, diagnostics) = Generate($$"""
                                          using System.Threading;
                                          using System.Threading.Tasks;
                                          using Caprine.CronTimer;
                                          using Serval.Jobs;
                                          public static class Schedules { public static CronExpression Value => new("* * * * *"); }
                                          [CronJob(typeof(Schedules), nameof(Schedules.Value), {{arguments}})]
                                          public sealed class Example : IJob
                                          {
                                              public ValueTask ExecuteAsync(CancellationToken ct) => default;
                                          }
                                          """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SERVALJOB002");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MembersAreCalledDirectlyOnceAndPreserveTheCronExpression(bool periodicMethod, bool cronMethod)
    {
        var period = periodicMethod ? "internal static TimeSpan @event()" : "internal static TimeSpan @event { get";
        var cron   = cronMethod ? "internal static CronExpression Cron()" : "internal static CronExpression Cron { get";
        var (compilation, diagnostics) = Generate($$"""
                                                    using System;
                                                    using System.Threading;
                                                    using System.Threading.Tasks;
                                                    using Caprine.CronTimer;
                                                    using Serval.Jobs;
                                                    namespace Example;
                                                    internal static class @class
                                                    {
                                                        public static int PeriodReads, CronReads;
                                                        public static readonly CronExpression Cached = new("*/15 * * * * *", CronExpressionOptions.IncludeSeconds);
                                                        {{period}} { PeriodReads++; return TimeSpan.FromMinutes(5); } {{(periodicMethod ? "" : "}")}}
                                                        {{cron}} { CronReads++; return Cached; } {{(cronMethod ? "" : "}")}}
                                                    }
                                                    [PeriodicJob(typeof(@class), nameof(@class.@event), Name = "periodic")]
                                                    public sealed class Pulse : IJob { public ValueTask ExecuteAsync(CancellationToken ct) => default; }
                                                    [CronJob(typeof(@class), nameof(@class.Cron), Name = "cron", TimeZoneId = "UTC")]
                                                    public sealed class Calendar : IJob { public ValueTask ExecuteAsync(CancellationToken ct) => default; }
                                                    """);
        Assert.Empty(diagnostics);
        var generated = compilation.SyntaxTrees.Last().ToString();
        Assert.Contains("global::Example.@class.@event" + (periodicMethod ? "()" : ""), generated);
        Assert.DoesNotContain("GetMethod", generated);
        var assembly = Compile(compilation);
        var register = assembly.GetType("Serval.Generated.MemberTestsJobs")!.GetMethod("Register")!.CreateDelegate<Func<ServalBuilder, ServalBuilder>>();
        var services = new ServiceCollection().AddLogging();
        register(services.AddServal());
        await using var provider = services.BuildServiceProvider();
        var             registry = provider.GetRequiredService<JobRegistry>();
        Assert.Equal(TimeSpan.FromMinutes(5), registry.GetJob("periodic").Schedule.Period);
        Assert.Equal(CronExpressionOptions.IncludeSeconds, registry.GetJob("cron").Schedule.Expression!.Options);
        Assert.Equal(TimeZoneInfo.Utc, registry.GetJob("cron").Schedule.TimeZone);
        var schedules = assembly.GetType("Example.class")!;
        Assert.Same(schedules.GetField("Cached")!.GetValue(null), registry.GetJob("cron").Schedule.Expression);
        Assert.Equal(1, schedules.GetField("PeriodReads")!.GetValue(null));
        Assert.Equal(1, schedules.GetField("CronReads")!.GetValue(null));
    }

    [Theory]
    [InlineData("public", false, true)]
    [InlineData("internal", false, false)]
    [InlineData("internal", true, true)]
    public void ReferencedAssemblyMembersRespectAccessibility(string visibility, bool friend, bool valid)
    {
        var source = $$"""
                       using System;
                       {{(friend ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"MemberTests\")]" : "")}}
                       public static class Schedules
                       {
                           {{visibility}} static TimeSpan Value => TimeSpan.FromSeconds(1);
                       }
                       """;
        var       library = CSharpCompilation.Create("ScheduleLibrary", [CSharpSyntaxTree.ParseText(source, ParseOptions)], References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream  = new MemoryStream();
        var       emit    = library.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var (compilation, diagnostics) = Generate("""
                                                  using System.Threading;
                                                  using System.Threading.Tasks;
                                                  using Serval.Jobs;
                                                  [PeriodicJob(typeof(Schedules), "Value")]
                                                  public sealed class Example : IJob { public ValueTask ExecuteAsync(CancellationToken ct) => default; }
                                                  """, MetadataReference.CreateFromImage(stream.ToArray()));
        if (valid)
        {
            Assert.Empty(diagnostics);
            Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }
        else
        {
            Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SERVALJOB002");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4_294_967_295D)]
    public void MemberValuesStillValidateBeforeServicesAreRegistered(double milliseconds)
    {
        var (compilation, diagnostics) = Generate($$"""
                                                    using System;
                                                    using System.Threading;
                                                    using System.Threading.Tasks;
                                                    using Serval.Jobs;
                                                    public static class Schedules<T>
                                                    {
                                                        public static TimeSpan Value => TimeSpan.FromMilliseconds({{milliseconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}D);
                                                    }
                                                    [PeriodicJob(typeof(Schedules<int>), nameof(Schedules<int>.Value))]
                                                    public sealed class Example : IJob { public ValueTask ExecuteAsync(CancellationToken ct) => default; }
                                                    """);
        Assert.Empty(diagnostics);
        var assembly = Compile(compilation);
        var register = assembly.GetType("Serval.Generated.MemberTestsJobs")!.GetMethod("Register")!.CreateDelegate<Func<ServalBuilder, ServalBuilder>>();
        var services = new ServiceCollection().AddLogging();
        var builder  = services.AddServal();
        var count    = services.Count;
        Assert.Throws<ArgumentOutOfRangeException>(() => register(builder));
        Assert.Equal(count, services.Count);
    }

    private static Assembly Compile(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var       emit   = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }

    private static (Compilation Compilation, IEnumerable<Diagnostic> Diagnostics) Generate(string source, MetadataReference? additionalReference = null)
    {
        var             references  = additionalReference is null ? References : [.. References, additionalReference];
        var             compilation = CSharpCompilation.Create("MemberTests", [CSharpSyntaxTree.ParseText(source, ParseOptions)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver      = CSharpGeneratorDriver.Create([new JobGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        return (generated, diagnostics);
    }
}
