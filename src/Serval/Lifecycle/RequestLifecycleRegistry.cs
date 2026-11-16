#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Serval.Lifecycle;

internal sealed class RequestLifecycleRegistry
{
    private Type[]? _frozen;

    private readonly IServiceCollection              _services;
    private readonly List<(Type Type, int Priority)> _registrations = [];

    internal RequestLifecycleRegistry(IServiceCollection services)
    {
        _services = services;
    }

    internal void Add<THandler>(int priority) where THandler : RequestLifecycleHandler
    {
        if (_frozen is not null)
        {
            throw new InvalidOperationException("Lifecycle registration is closed after the host starts.");
        }

        var type = typeof(THandler);
        if (type.IsAbstract || type.ContainsGenericParameters)
        {
            throw new ArgumentException("A lifecycle handler must be a concrete, closed class.", nameof(THandler));
        }

        if (_registrations.Any(registration => registration.Type == type))
        {
            throw new InvalidOperationException($"Lifecycle handler '{type}' has already been registered.");
        }

        var descriptor = _services.LastOrDefault(service => service.ServiceType == type && !service.IsKeyedService);
        if (descriptor is not null && descriptor.Lifetime != ServiceLifetime.Scoped)
        {
            throw new InvalidOperationException($"Lifecycle handler '{type}' must be registered as a scoped service.");
        }

        _services.TryAddScoped<THandler>();
        _registrations.Add((type, priority));
    }

    internal Type[] Freeze()
    {
        if (_frozen is not null)
        {
            return _frozen;
        }

        foreach (var (type, _) in _registrations)
        {
            var descriptor = _services.LastOrDefault(service => service.ServiceType == type && !service.IsKeyedService);
            if (descriptor?.Lifetime != ServiceLifetime.Scoped)
            {
                throw new InvalidOperationException($"Lifecycle handler '{type}' must be registered as a scoped service.");
            }
        }

        _frozen = [.. _registrations.OrderBy(registration => registration.Priority).Select(registration => registration.Type)];
        return _frozen;
    }
}
