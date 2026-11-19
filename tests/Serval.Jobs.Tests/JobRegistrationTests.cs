#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Caprine.CronTimer;
using Microsoft.Extensions.DependencyInjection;

namespace Serval.Jobs.Tests;

public sealed class JobRegistrationTests
{
    [Fact]
    public void DuplicateRegistrationsAndInvalidNamesAreRejected()
    {
        var services = new ServiceCollection();
        var job      = JobRegistration.Create<EmptyJob>(JobSchedule.Every(TimeSpan.FromSeconds(1)), "job");
        Assert.Throws<InvalidOperationException>(() => JobRegistration.Register(services, job, job));
        Assert.Empty(services);
        JobRegistration.Register(services, job);
        Assert.Throws<InvalidOperationException>(() => JobRegistration.Register(services, job));
        Assert.Throws<ArgumentException>(() => JobRegistration.Create<EmptyJob>(JobSchedule.Every(TimeSpan.FromSeconds(1)), " "));
        Assert.Throws<ArgumentNullException>(() => JobRegistration.Create<EmptyJob>(null!));
        Assert.Throws<ArgumentException>(() => JobRegistration.Create<AbstractJob>(JobSchedule.Every(TimeSpan.FromSeconds(1))));
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Transient)]
    public void JobsMustBeScoped(ServiceLifetime lifetime)
    {
        IServiceCollection services = new ServiceCollection();
        services.Add(new ServiceDescriptor(typeof(EmptyJob), typeof(EmptyJob), lifetime));
        Assert.Throws<InvalidOperationException>(() => JobRegistration.Register(services, JobRegistration.Create<EmptyJob>(JobSchedule.Every(TimeSpan.FromSeconds(1)))));
    }

    [Fact]
    public async Task LifetimeOverridesAfterRegistrationAreCaughtWhenResolvingTheRegistry()
    {
        var services = new ServiceCollection().AddLogging();
        JobRegistration.Register(services, JobRegistration.Create<EmptyJob>(JobSchedule.Every(TimeSpan.FromSeconds(1))));
        services.AddSingleton<EmptyJob>();
        await using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<JobRegistry>);
    }

    [Fact]
    public async Task RegistrationSnapshotsDefinitionsAndExposesOneSingletonRegistry()
    {
        var services    = new ServiceCollection().AddLogging();
        var first       = JobRegistration.Create<EmptyJob>(JobSchedule.Every(TimeSpan.FromSeconds(1)), "first");
        var definitions = new[] { first };
        JobRegistration.Register(services, definitions);
        definitions[0] = JobRegistration.Create<EmptyJob>(JobSchedule.Every(TimeSpan.FromSeconds(2)), "changed");
        await using var provider = services.BuildServiceProvider();
        var             registry = provider.GetRequiredService<JobRegistry>();
        using var       scope    = provider.CreateScope();
        Assert.Equal("first", Assert.Single(registry.GetJobs()).Name);
        Assert.Same(registry, scope.ServiceProvider.GetRequiredService<JobRegistry>());
    }

    [Fact]
    public async Task RemovingTheScopedJobRegistrationIsCaughtAtStartup()
    {
        var services = new ServiceCollection().AddLogging();
        JobRegistration.Register(services, JobRegistration.Create<EmptyJob>(JobSchedule.Every(TimeSpan.FromSeconds(1))));
        services.Remove(services.Single(service => service.ServiceType == typeof(EmptyJob)));
        await using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<JobRegistry>);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.9)]
    [InlineData(4_294_967_295)]
    public void UnsupportedPeriodicIntervalsAreRejected(double milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JobSchedule.Every(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void SchedulesValidateImmediatelyAndReuseParsedCronExpressions()
    {
        Assert.Throws<FormatException>(() => JobSchedule.Cron("not a schedule"));
        Assert.Throws<ArgumentNullException>(() => JobSchedule.Cron((CronExpression)null!));
        Assert.Equal(TimeSpan.FromMilliseconds(1), JobSchedule.Every(TimeSpan.FromMilliseconds(1)).Period);
        Assert.Equal(TimeSpan.FromMilliseconds(4_294_967_294), JobSchedule.Every(TimeSpan.FromMilliseconds(4_294_967_294)).Period);
        var expression = new CronExpression("*/5 * * * *");
        var schedule   = JobSchedule.Cron(expression);
        Assert.Same(expression, schedule.Expression);
        Assert.Same(TimeZoneInfo.Utc, schedule.TimeZone);
    }

    public sealed class EmptyJob : IJob
    {
        public ValueTask ExecuteAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    public abstract class AbstractJob : IJob
    {
        public abstract ValueTask ExecuteAsync(CancellationToken ct);
    }
}
