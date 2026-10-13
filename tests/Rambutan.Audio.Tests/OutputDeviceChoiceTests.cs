namespace Rambutan.Audio.Tests;

/// <summary>A device chosen by name is used while it is present; otherwise the
/// output follows the system default.</summary>
public class OutputDeviceChoiceTests
{
    private static readonly string?[] Devices = ["EDIFIER M60 Analog Stereo", null, "Arctis Pro Wireless Game"];

    [Fact]
    public void NoChoice_FollowsTheDefault() =>
        Assert.Null(OutputDeviceChoice.Find(Devices, null));

    [Fact]
    public void APresentDevice_IsFoundByName() =>
        Assert.Equal(2, OutputDeviceChoice.Find(Devices, "Arctis Pro Wireless Game"));

    [Theory]
    [InlineData("Unplugged Headset")]
    [InlineData("edifier m60 analog stereo")]
    [InlineData("")]
    public void AnAbsentOrMismatchedName_FollowsTheDefault(string preferred) =>
        Assert.Null(OutputDeviceChoice.Find(Devices, preferred));
}
