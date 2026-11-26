namespace MatOS.Core.Entities;

/// <summary>
/// A background task (install, update, backup, ...). Long-running Docker work never runs inside a web request:
/// it is queued as a job, and the UI follows its <see cref="Log"/> while it runs.
/// </summary>
public class Job : AuditableEntity
{
    public JobKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>The app the job is about (kept as slug as well, so the history survives an uninstall).</summary>
    public long? AppId { get; set; }

    public string? AppSlug { get; set; }

    /// <summary>The captured output, one line per entry. Trimmed at <see cref="MaxLogLength"/> characters.</summary>
    public string Log { get; set; } = string.Empty;

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? Error { get; set; }

    public const int MaxLogLength = 200_000;
}
