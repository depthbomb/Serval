#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serval.Jobs.Internal;

namespace Serval.Jobs;

/// <summary>Runtime support for generated job registration. Applications call AddGeneratedJobs on their Serval builder.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class JobRegistration
{
    private readonly JobDefinition     _definition;
    private readonly ServiceDescriptor _service;

    private JobRegistration(JobDefinition definition, ServiceDescriptor service)
    {
        _definition = definition;
        _service    = service;
    }

    /// <summary>Creates a typed registration for generated code without inspecting attributes at runtime.</summary>
    public static JobRegistration Create<TJob>(JobSchedule schedule, string? name = null) where TJob : class, IJob
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var type = typeof(TJob);
        if (type.IsAbstract || type.ContainsGenericParameters)
        {
            throw new ArgumentException("A job must be a concrete, closed class.", nameof(TJob));
        }

        name ??= type.FullName ?? type.Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new JobRegistration(new JobDefinition(name, type, schedule, static services => services.GetRequiredService<TJob>()), ServiceDescriptor.Scoped<TJob, TJob>());
    }

    /// <summary>Installs a fixed set of generated jobs, their singleton registry, and the host service.</summary>
    /// <remarks>Called by generated code once per service collection. The supplied array is snapshotted.</remarks>
    public static IServiceCollection Register(IServiceCollection services, params JobRegistration[] jobs)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jobs);

        if (services.Any(service => service.ServiceType == typeof(JobRegistry)))
        {
            throw new InvalidOperationException("Jobs have already been registered.");
        }

        var registrations = jobs.ToArray();
        var definitions   = new JobDefinition[registrations.Length];
        var names         = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < registrations.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(registrations[index]);

            var definition = registrations[index]._definition;
            if (!names.Add(definition.Name))
            {
                throw new InvalidOperationException($"Job '{definition.Name}' has already been registered.");
            }

            definitions[index] = definition;
        }

        ValidateLifetimes(services, definitions, true);

        foreach (var registration in registrations)
        {
            services.TryAdd(registration._service);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider =>
        {
            ValidateLifetimes(services, definitions, false);

            return new JobRegistry(definitions,
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILoggerFactory>());
        });
        services.AddHostedService<JobHostedService>();

        return services;
    }

    private static void ValidateLifetimes(IServiceCollection services, JobDefinition[] definitions, bool allowMissing)
    {
        foreach (var definition in definitions)
        {
            var descriptor = services.LastOrDefault(service => service.ServiceType == definition.JobType && !service.IsKeyedService);
            if ((!allowMissing || descriptor is not null) && descriptor?.Lifetime != ServiceLifetime.Scoped)
            {
                throw new InvalidOperationException($"Job '{definition.JobType}' must be registered as a scoped service.");
            }
        }
    }
}
