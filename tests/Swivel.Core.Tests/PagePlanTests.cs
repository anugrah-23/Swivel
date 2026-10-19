using Swivel.Core.Files;

namespace Swivel.Core.Tests;

public class PagePlanTests
{
    [Fact]
    public void Round_trips()
    {
        PlannedPage[] pages = [new(1, 4, 90), new(0, 0, 0), new(0, 2, 270)];
        Assert.Equal("1:4:90;0:0:0;0:2:270", PagePlan.Format(pages));
        Assert.Equal(pages, PagePlan.Parse(PagePlan.Format(pages)));
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(360, 0)]
    [InlineData(-180, 180)]
    public void Rotation_wraps_to_quarter_turns(int degrees, int expected) =>
        Assert.Equal(expected, PagePlan.Normalise(degrees));

    [Theory]
    [InlineData("1:2")]
    [InlineData("a:b:c")]
    [InlineData("-1:0:0")]
    public void Rejects_nonsense(string plan) =>
        Assert.Throws<FormatException>(() => PagePlan.Parse(plan));
}
