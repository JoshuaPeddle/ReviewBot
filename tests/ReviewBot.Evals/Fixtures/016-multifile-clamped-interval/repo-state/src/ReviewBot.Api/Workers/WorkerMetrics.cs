using System.Diagnostics.Metrics;
using ReviewBot.Core.Metrics;

namespace ReviewBot.Api.Workers;

public sealed class WorkerMetrics
{
    private readonly Counter<long> jobsProcessed;
    private readonly ObservableGauge<int> queueDepth;

    public WorkerMetrics(Meter meter, Func<int> queueDepthProvider)
    {
        jobsProcessed = meter.CreateCounter<long>(MetricNames.JobsProcessed);
        queueDepth = meter.CreateObservableGauge(MetricNames.QueueDepth, queueDepthProvider);
    }

    public void RecordProcessed() => jobsProcessed.Add(1);
}
