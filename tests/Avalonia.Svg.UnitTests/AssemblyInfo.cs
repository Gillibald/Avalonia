using Xunit;

// Some tests spin up a UnitTestApplication, which mutates the global
// AvaloniaLocator; running tests in parallel corrupts that shared state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
