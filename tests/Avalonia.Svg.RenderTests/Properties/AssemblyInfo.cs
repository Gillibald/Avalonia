using Xunit;

// The render harness is single-UI-thread (one Dispatcher, one compositor per
// test): test collections must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
