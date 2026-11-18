#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Jobs.Internal;

internal sealed record JobDefinition(string Name, Type JobType, JobSchedule Schedule, Func<IServiceProvider, IJob> Activate);
