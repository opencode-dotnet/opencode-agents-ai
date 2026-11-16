using System.Diagnostics;

namespace OpenCode.Agents.AI.Tests.Support;

internal sealed class TelemetryCapture : IDisposable
{
    private readonly ActivityListener _listener;

    public TelemetryCapture()
    {
        Source = new ActivitySource("telemetry-test");
        _listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public ActivitySource Source { get; }

    public void Dispose()
    {
        _listener.Dispose();
        Source.Dispose();
    }
}
