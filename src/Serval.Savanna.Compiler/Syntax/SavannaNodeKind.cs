#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Savanna.Compiler.Syntax;

/// <summary>Describes parsed content and control-flow markers in a Savanna document.</summary>
public enum SavannaNodeKind
{
    /// <summary>Literal markup or verbatim text.</summary>
    Text,

    /// <summary>A value expression written with HTML escaping.</summary>
    Expression,

    /// <summary>An explicitly trusted HTML expression.</summary>
    Html,

    /// <summary>A call to another concrete view.</summary>
    Include,

    /// <summary>A call to a registered Savanna component.</summary>
    Component,

    /// <summary>An inherited block implementation placeholder.</summary>
    Parent,

    /// <summary>A named layout block placeholder.</summary>
    Block,

    /// <summary>The start of a conditional branch.</summary>
    If,

    /// <summary>An alternative conditional branch.</summary>
    ElseIf,

    /// <summary>The fallback branch of a condition.</summary>
    Else,

    /// <summary>The end of a conditional group.</summary>
    EndIf,

    /// <summary>The start of a synchronous foreach loop.</summary>
    ForEach,

    /// <summary>The end of a synchronous or asynchronous foreach loop.</summary>
    EndForEach,

    /// <summary>The start of a C# for loop.</summary>
    For,

    /// <summary>The end of a for loop.</summary>
    EndFor,

    /// <summary>The start of an asynchronous foreach loop.</summary>
    AwaitForEach,

    /// <summary>A local variable declaration.</summary>
    Let,

    /// <summary>The fallback body for a loop with no iterations.</summary>
    Empty,

    /// <summary>A non-executable JSON script element.</summary>
    Json,

    /// <summary>The start of a whitespace-preservation region.</summary>
    Whitespace,

    /// <summary>The end of a whitespace-preservation region.</summary>
    EndWhitespace,

    /// <summary>A slot declaration or supplied fragment, depending on context.</summary>
    Slot,

    /// <summary>The end of a paired slot.</summary>
    EndSlot,

    /// <summary>The end of an include that supplies slots.</summary>
    EndInclude,

    /// <summary>The end of a component that supplies slots.</summary>
    EndComponent,

    /// <summary>The start of a branch whose C# condition is negated.</summary>
    Unless,

    /// <summary>The start of content compiled only when DEBUG is defined.</summary>
    Debug,

    /// <summary>The start of content compiled only when DEBUG is not defined.</summary>
    Release,

    /// <summary>The end of a debug or release compilation region.</summary>
    EndBuild,

    /// <summary>The start of a C# switch statement.</summary>
    Switch,

    /// <summary>A switch label containing a C# constant or pattern.</summary>
    Case,

    /// <summary>The fallback label of a switch statement.</summary>
    Default,

    /// <summary>Exits the nearest enclosing switch or loop, optionally when a C# condition is true.</summary>
    Break,

    /// <summary>The end of a switch statement.</summary>
    EndSwitch,

    /// <summary>Skips to the next iteration of the nearest loop, optionally when a C# condition is true.</summary>
    Continue,
}
