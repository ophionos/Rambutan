using Rambutan.Integrations.Linux;

namespace Rambutan.Integrations.Tests.Linux;

public class PulseStreamPropertiesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WithoutAValueOfTheUsers_OursIsSet(string? existing) =>
        Assert.Equal(
            "application.name=Rambutan application.icon_name=rambutan media.role=music",
            PulseStreamProperties.ValueFor(existing));

    [Fact]
    public void TheUsersOwnValue_IsKept() =>
        Assert.Null(PulseStreamProperties.ValueFor("media.role=video"));

    [Fact]
    public void TheIcon_IsTheOneTheDesktopEntryInstalls() =>
        Assert.Contains($"application.icon_name={LinuxAppEntry.AppId} ", PulseStreamProperties.Ours);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getenv")]
    private static extern IntPtr CGetEnv(string name);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "unsetenv")]
    private static extern int CUnsetEnv(string name);

    [Fact]
    public void OnLinux_LibcSeesIt()
    {
        // What libpulse reads: the C environment, not .NET's copy.
        if (!OperatingSystem.IsLinux())
            return;
        var before = Environment.GetEnvironmentVariable(PulseStreamProperties.Variable);
        if (!string.IsNullOrWhiteSpace(before))
            return;   // the user's own value would be kept
        try
        {
            PulseStreamProperties.Apply();

            Assert.Equal(PulseStreamProperties.Ours,
                System.Runtime.InteropServices.Marshal.PtrToStringUTF8(CGetEnv(PulseStreamProperties.Variable)));
        }
        finally
        {
            CUnsetEnv(PulseStreamProperties.Variable);
        }
    }

    [Fact]
    public void OffLinux_NothingChanges()
    {
        if (OperatingSystem.IsLinux())
            return;
        var before = Environment.GetEnvironmentVariable(PulseStreamProperties.Variable);

        PulseStreamProperties.Apply();

        Assert.Equal(before, Environment.GetEnvironmentVariable(PulseStreamProperties.Variable));
    }
}
