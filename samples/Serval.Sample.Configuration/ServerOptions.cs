#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Serval.Sample.Configuration;

public sealed class ServerOptions
{
    // Generated options binding populates an existing instance, so these properties need setters.
    [Required]
    public string Name { get; set; } = string.Empty;

    [Range(1, 65_535)]
    public int Port { get; set; }

    public bool Diagnostics { get; set; }
}

[OptionsValidator]
public sealed partial class ServerOptionsValidator : IValidateOptions<ServerOptions> {}
