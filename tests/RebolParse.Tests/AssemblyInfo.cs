// The allocation tests measure bytes per call, and the JIT and other tests running at the same time can allocate on
// the measuring thread under coverage instrumentation. The whole suite runs in a few seconds, so run it serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
