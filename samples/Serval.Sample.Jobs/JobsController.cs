#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Binding;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Jobs;
using Serval.Routing;

namespace Serval.Sample.Jobs;

[Controller]
public sealed class JobsController : ControllerBase
{
    private readonly JobRegistry _jobs;

    public JobsController(JobRegistry jobs)
    {
        _jobs = jobs;
    }

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync() => TextAsync("""
                                                              Recurring jobs (loopback demo)
                                                              GET  /jobs                  Inspect the fixed registry and execution state.
                                                              POST /jobs/pulse/pause       Pause the five-second PeriodicTimer job.
                                                              POST /jobs/pulse/resume      Resume scheduled runs.
                                                              POST /jobs/pulse/run         Run it now, even while paused. Returns 409 if already running.
                                                              POST /jobs/calendar/run     Run the cron job now. It also runs every 15 seconds in UTC.

                                                              Replace pulse with calendar in the pause/resume endpoints to control that job.
                                                              Watch the console for executions. Pulse takes one second so you can try overlapping manual runs.
                                                              Pausing does not cancel an execution already in progress. Shutdown cancels running jobs.
                                                              Manual runs await completion; disconnecting the request cancels that run.
                                                              This local demo exposes controls without authentication. Protect them in a deployed application.
                                                              """);

    [Get("/jobs")]
    public ValueTask<IActionResult> ListAsync() => JsonAsync(_jobs.GetJobs().Select(job => new
    {
        job.Name,
        type     = job.JobType.FullName,
        period   = job.Schedule.Period,
        cron     = job.Schedule.Expression?.Expression,
        timeZone = job.Schedule.TimeZone?.Id,
        job.IsPaused,
        job.IsRunning,
        job.IsScheduled,
        job.ScheduleError,
        job.LastStartedAt,
        job.LastCompletedAt,
        job.LastError,
        job.RunCount,
        job.FailureCount,
    }));

    [Post("/jobs/{name}/pause")]
    public ValueTask<IActionResult> PauseAsync([FromRoute] string name)
    {
        RequireJob(name);

        _jobs.Pause(name);
        return ValueTask.FromResult<IActionResult>(StatusCode(StatusCodes.NoContent));
    }

    [Post("/jobs/{name}/resume")]
    public ValueTask<IActionResult> ResumeAsync([FromRoute] string name)
    {
        RequireJob(name);

        _jobs.Resume(name);
        return ValueTask.FromResult<IActionResult>(StatusCode(StatusCodes.NoContent));
    }

    [Post("/jobs/{name}/run")]
    public async ValueTask<IActionResult> RunAsync([FromRoute] string name, CancellationToken ct)
    {
        RequireJob(name);

        var ran = await _jobs.RunAsync(name, ct);
        return StatusCode(ran ? StatusCodes.NoContent : StatusCodes.Conflict);
    }

    private void RequireJob(string name)
    {
        AbortIf(_jobs.GetJobs().All(job => job.Name != name), StatusCodes.NotFound, "Unknown job.");
    }
}
