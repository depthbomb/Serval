#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Serval.Tests.Support;

internal sealed class SampleLogProvider : ILoggerProvider
{
    public           ConcurrentQueue<string> Messages { get; } = new();
    private readonly string                  _prefix;

    private SampleLogProvider(string prefix)
    {
        _prefix = prefix;
    }

    public ILogger CreateLogger(string categoryName) => new SampleLogger(this, categoryName);

    public void Dispose() {}

    internal void AssertEvents(params string[] expected)
    {
        Assert.Equal(expected.Length, Messages.Count);
        foreach (var (message, fragment) in Messages.Zip(expected))
        {
            Assert.Contains(fragment, message);
        }
    }

    internal static SampleLogProvider Capture(SampleApplication app, string sample)
    {
        var logs = new SampleLogProvider($"Serval.Sample.{sample}.");
        app.Host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        return logs;
    }

    private sealed class SampleLogger : ILogger
    {
        private readonly SampleLogProvider _provider;
        private readonly string            _category;

        public SampleLogger(SampleLogProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _category.StartsWith(_provider._prefix, StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                _provider.Messages.Enqueue(formatter(state, exception));
            }
        }
    }
}
