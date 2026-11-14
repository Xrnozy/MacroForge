using Xunit;

// Vision, benchmark and live-input tests are CPU/hardware sensitive: running them concurrently makes timings and cursor checks flaky.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
