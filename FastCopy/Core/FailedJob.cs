namespace FastCopy.Core;

/// <summary>
/// Represents a failed copy job with its associated error information.
/// </summary>
public readonly struct FailedJob
{
    public readonly CopyJob Job;
    public readonly string ExceptionMessage;

    public FailedJob(CopyJob job, string exceptionMessage)
    {
        Job = job;
        ExceptionMessage = exceptionMessage;
    }
}
