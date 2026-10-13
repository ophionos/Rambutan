using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AssemblyFixture(typeof(Rambutan.App.Tests.HeadlessFirst))]

namespace Rambutan.App.Tests;

/// <summary>Bare application for control tests: stock theme, no app services.</summary>
public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// One headless Avalonia session per process (Avalonia allows one app per process),
/// with one application shared by every test, and a helper that runs a test body on
/// its UI thread.
/// </summary>
internal static class Headless
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(TestApp), AvaloniaTestIsolationLevel.PerAssembly));

    /// <summary>Builds the application on the session's thread and waits for it.</summary>
    public static void Start() => Run(() => { });

    public static void Run(Action body) =>
        Session.Value.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>
/// Starts the headless session before any test runs. Avalonia's UI dispatcher belongs
/// to the first thread that touches it, and plain view-model tests touch it too: their
/// events Post to it from an xUnit thread. Started lazily, the session lost that race
/// now and then, and a control test failed with "a different thread owns it". Starting
/// early is half the fix; the other half is building the application once
/// (<see cref="AvaloniaTestIsolationLevel.PerAssembly"/>), because the default rebuilds
/// it for every test and reopens the race each time. A module initializer can't do
/// the early start: the session thread would wait on the module still loading.
/// An assembly fixture is created before the first test in the assembly runs.
/// </summary>
public sealed class HeadlessFirst
{
    public HeadlessFirst() => Headless.Start();
}
