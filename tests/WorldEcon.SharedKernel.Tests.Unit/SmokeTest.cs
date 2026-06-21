using FluentAssertions;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class SmokeTest
{
    [Test]
    public void Toolchain_Runs()
    {
        (1 + 1).Should().Be(2);
    }
}
