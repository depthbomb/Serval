#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Cryptography;
using System.Text;

namespace Serval.Authentication;

/// <summary>Synchronizer tokens bound to application-owned browser state, including anonymous state.</summary>
/// <remarks>Load expected state through a trusted browser/session binding. Never use submitted form data as the expected state.</remarks>
public static class CsrfTokens
{
    /// <summary>The state key used by the cookie-session adapter.</summary>
    public const string PropertyName = "Serval.CsrfToken";

    /// <summary>Generates a token for storage in trusted browser state and submission in a form or header.</summary>
    public static string Create()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Replaces a state's token. Rotate when the browser's authentication state changes.</summary>
    public static string Rotate(IDictionary<string, string> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state[PropertyName] = Create();
    }

    /// <summary>Compares a submitted token with a token loaded from trusted state in constant time.</summary>
    public static bool Validate(string? expected, string? submitted)
        => expected is { Length: 43 }     && submitted is { Length: 43 }     &&
           expected.All(IsTokenCharacter) && submitted.All(IsTokenCharacter) &&
           CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(submitted));

    /// <summary>Validates a submitted token against application-owned browser state.</summary>
    public static bool Validate(IReadOnlyDictionary<string, string> state, string? submitted)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.TryGetValue(PropertyName, out var expected) && Validate(expected, submitted);
    }

    private static bool IsTokenCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
}
