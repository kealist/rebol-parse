using Samples;

namespace RebolParse.Tests;

/// <summary>The sample app checks its own results; this runs every demo so the samples cannot rot.</summary>
public class SamplesTests
{
    public static TheoryData<string> DemoNames => new(Demos.All.Select(d => d.Name));

    [Theory]
    [MemberData(nameof(DemoNames))]
    public void Demo_passes_all_its_checks(string name)
    {
        var demo = Demos.All.Single(d => d.Name == name);
        var report = new Report(quiet: true);
        demo.Run(report);
        Assert.Empty(report.Failures);
        Assert.True(report.Passed > 0, "a demo should check something");
    }

    [Fact]
    public void Every_demo_has_a_unique_name() =>
        Assert.Equal(Demos.All.Count, Demos.All.Select(d => d.Name).Distinct().Count());
}
