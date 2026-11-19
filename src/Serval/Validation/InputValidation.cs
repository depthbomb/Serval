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

    /// <summary>Copies the current errors for application inspection without throwing.</summary>
    public IReadOnlyDictionary<string, string[]> Snapshot()
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
    /// <summary>Runs registered validators and returns an immutable result without rejecting the request.</summary>
    public static async ValueTask<InputValidationResult> EvaluateAsync<T>(RequestContext context, T input)
    {
        ArgumentNullException.ThrowIfNull(context);
        var errors = new ValidationErrors();
        foreach (var validator in context.Services.GetServices<IInputValidator<T>>())
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            await validator.ValidateAsync(input, errors, context, context.RequestAborted);
        }

        context.RequestAborted.ThrowIfCancellationRequested();
        return new InputValidationResult(errors);
    }

    /// <summary>Runs all registered validators in registration order and throws when any reports errors.</summary>
    /// <remarks>No validators means no extra validation. Parsing and nullability checks remain the caller's responsibility.</remarks>
    public static async ValueTask ValidateAsync<T>(RequestContext context, T input)
        => (await EvaluateAsync(context, input)).ThrowIfInvalid();
}

/// <summary>An immutable set of field errors, independent of HTTP response policy.</summary>
public sealed class InputValidationResult
{
    /// <summary>Whether all validators accepted the input.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Client-visible errors with read-only message collections.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Errors { get; }

    /// <summary>Snapshots the supplied errors.</summary>
    public InputValidationResult(ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors.Snapshot().ToDictionary(pair => pair.Key, IReadOnlyList<string> (pair) => Array.AsReadOnly(pair.Value), StringComparer.Ordinal).AsReadOnly();
    }

    /// <summary>Uses the framework's exception-based rejection policy when errors exist.</summary>
    public void ThrowIfInvalid()
    {
        if (IsValid)
        {
            return;
        }

        var errors = new ValidationErrors();
        foreach (var (field, messages) in Errors)
        {
            foreach (var message in messages)
            {
                errors.Add(field, message);
            }
        }

        throw new InputValidationException(errors);
    }
}
