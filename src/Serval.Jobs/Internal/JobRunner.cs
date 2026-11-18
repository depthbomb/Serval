#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Serval.Jobs.Internal;

internal sealed class JobRunner
{
    private bool            _started;
    private bool            _stopped;
    private bool            _paused;
    private bool            _running;
    private bool            _scheduling;
    private long            _runCount;
    private long            _failureCount;
    private DateTimeOffset? _lastStartedAt;
    private DateTimeOffset? _lastCompletedAt;
    private string?         _lastError;
    private string?         _scheduleError;
    private JobStatus?      _status;
    private JobTimer?       _timer;
    private Task            _loop = Task.CompletedTask;
    private Task<bool>?     _execution;
    private Task?           _shutdown;

    private readonly Lock                    _gate     = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly JobDefinition           _definition;
    private readonly IServiceScopeFactory    _scopes;
    private readonly TimeProvider            _timeProvider;
    private readonly ILogger                 _logger;

    internal JobRunner(JobDefinition definition, IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger logger)
    {
        _definition   = definition;
        _scopes       = scopes;
        _timeProvider = timeProvider;
        _logger       = logger;
    }

    internal JobStatus GetStatus()
    {
        lock (_gate)
        {
            return _status ??= new JobStatus
            {
                Name            = _definition.Name,
                JobType         = _definition.JobType,
                Schedule        = _definition.Schedule,
                IsPaused        = _paused,
                IsRunning       = _running,
                IsScheduled     = _scheduling,
                ScheduleError   = _scheduleError,
                LastStartedAt   = _lastStartedAt,
                LastCompletedAt = _lastCompletedAt,
                LastError       = _lastError,
                RunCount        = _runCount,
                FailureCount    = _failureCount,
            };
        }
    }

    internal void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused != paused)
            {
                _paused = paused;
                _status = null;
            }
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped)
            {
                throw new InvalidOperationException("Job scheduling can only be started once.");
            }

            _timer      = new JobTimer(_definition.Schedule, _timeProvider);
            _started    = true;
            _scheduling = true;
            _status     = null;
            _loop       = Task.Run(RunScheduleAsync);
        }
    }

    internal Task<bool> RunAsync(bool scheduled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_started || _stopped)
            {
                if (scheduled)
                {
                    return Task.FromResult(false);
                }

                throw new InvalidOperationException("Jobs can only be triggered while the host is running.");
            }

            if (_running || (scheduled && _paused))
            {
                return Task.FromResult(false);
            }

            _lastStartedAt = _timeProvider.GetUtcNow();
            _running       = true;
            _status        = null;
            _runCount++;

            if (!scheduled)
            {
                // Manual calls must not run user code under the lock or on a controller's calling thread.
                _execution = Task.Run(() => ExecuteAsync(ct));
                return _execution;
            }
        }

        // The scheduler is already asynchronous. Its loop owns and drains this execution during shutdown.
        return ExecuteAsync(ct);
    }

    internal Task StopAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null)
            {
                return _shutdown;
            }

            _stopped  = true;
            _shutdown = Task.Run(StopCoreAsync);
            return _shutdown;
        }
    }

    private async Task RunScheduleAsync()
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                // Timer completions can run continuations inline. Leave the timer callback before invoking user code.
                await Task.Yield();
                try
                {
                    await RunAsync(true, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Scheduled job {JobName} failed. The next tick will still run.", _definition.Name);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) {}
        catch (Exception exception)
        {
            lock (_gate)
            {
                _scheduleError = exception.Message;
                _status        = null;
            }

            _logger.LogError(exception, "The timer for job {JobName} failed. Scheduling has stopped.", _definition.Name);
        }
        finally
        {
            lock (_gate)
            {
                _scheduling = false;
                _status     = null;
            }
        }
    }

    private async Task<bool> ExecuteAsync(CancellationToken ct)
    {
        string?   error          = null;
        var       stopping       = _stopping.Token;
        using var linked         = ct.CanBeCanceled && ct != stopping ? CancellationTokenSource.CreateLinkedTokenSource(ct, stopping) : null;
        var       executionToken = linked?.Token ?? stopping;
        try
        {
            executionToken.ThrowIfCancellationRequested();

            await using var scope = _scopes.CreateAsyncScope();
            var             job   = _definition.Activate(scope.ServiceProvider);
            await job.ExecuteAsync(executionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _running         = false;
                _status          = null;
                _lastCompletedAt = _timeProvider.GetUtcNow();
                _lastError       = error;
                if (error is not null)
                {
                    _failureCount++;
                }
            }
        }

        return true;
    }

    private async Task StopCoreAsync()
    {
        Exception? cancellationFailure = null;
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cancellationFailure = exception;
        }

        ExceptionDispatchInfo? timerFailure = null;
        try
        {
            _timer?.Dispose();
        }
        catch (Exception exception)
        {
            timerFailure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await Task.WhenAll(_loop, _execution ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Scheduled errors were logged; manual errors belong to the caller. Shutdown still drains both.
        }
        finally
        {
            _stopping.Dispose();
        }

        if (cancellationFailure is not null)
        {
            // Report only after draining: a failing logging provider must not abandon active scopes.
            _logger.LogError(cancellationFailure, "A cancellation callback for job {JobName} failed.", _definition.Name);
        }

        timerFailure?.Throw();
    }
}
