#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;

namespace Serval.Validation;

/// <summary>Validates application input using request-scoped dependencies.</summary>
public interface IInputValidator<in T>
{
    /// <summary>Adds client-visible field errors. Honor cancellation during asynchronous work.</summary>
    ValueTask ValidateAsync(T input, ValidationErrors errors, RequestContext context, CancellationToken ct);
}

/// <summary>Collects application validation errors without changing parsing rules.</summary>
public sealed class ValidationErrors
{
    /// <summary>Whether any validator has reported an error.</summary>
    public bool HasErrors => _errors.Count != 0;

    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    /// <summary>Adds a client-visible message for a field. Use an empty field name for object-level errors.</summary>
    public void Add(string field, string message)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors.Add(field, messages);
        }

        messages.Add(message);
    }

    /// <summary>Throws a validation failure when errors have been collected.</summary>
    public void ThrowIfInvalid()
    {
        if (HasErrors)
        {
            throw new InputValidationException(this);
        }
    }

    internal IReadOnlyDictionary<string, string[]> Snapshot()
        => _errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal).AsReadOnly();
}

/// <summary>A rejected application input, rendered as a 422 problem response with field errors by default.</summary>
public sealed class InputValidationException : Exception
{
    /// <summary>Client-visible field errors captured when validation failed.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    /// <summary>Captures a nonempty set of validation errors.</summary>
    public InputValidationException(ValidationErrors errors) : base("The submitted values are invalid.")
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (!errors.HasErrors)
        {
            throw new ArgumentException("At least one validation error is required.", nameof(errors));
        }

        Errors = errors.Snapshot();
    }
}

/// <summary>Runs validators registered for an input type. Also used by generated FromBody bindings.</summary>
public static class InputValidation
{
    /// <summary>Runs all registered validators in registration order and throws when any reports errors.</summary>
    /// <remarks>No validators means no extra validation. Parsing and nullability checks remain the caller's responsibility.</remarks>
    public static async ValueTask ValidateAsync<T>(RequestContext context, T input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidationErrors? errors = null;
        foreach (var validator in context.Services.GetServices<IInputValidator<T>>())
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            errors ??= new ValidationErrors();
            await validator.ValidateAsync(input, errors, context, context.RequestAborted);
        }

        context.RequestAborted.ThrowIfCancellationRequested();
        errors?.ThrowIfInvalid();
    }
}
