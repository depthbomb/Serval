#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Savanna;
using Serval.Sample.Savanna.Generated;

namespace Serval.Sample.Savanna;

public sealed class FooterComponent : ISavannaComponent<string>
{
    public ValueTask RenderAsync(SavannaWriter writer, string arguments, SavannaRenderContext context)
        => Views.Shared.Footer.RenderAsync(writer, arguments, context);
}
