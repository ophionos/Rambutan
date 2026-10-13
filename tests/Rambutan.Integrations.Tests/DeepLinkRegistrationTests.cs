namespace Rambutan.Integrations.Tests;

/// <summary>The Windows scheme registration, against a fake registry.
/// Tests never touch the real one.</summary>
public class DeepLinkRegistrationTests
{
    private const string Exe = @"C:\Users\u\AppData\Local\Rambutan.Desktop\Rambutan.App.exe";

    private sealed class FakeKeys : IRegistryKeys
    {
        public Dictionary<(string Key, string Name), string> Values { get; } = [];

        public void SetValue(string subKey, string name, string value) => Values[(subKey, name)] = value;

        public string? GetValue(string subKey, string name) =>
            Values.TryGetValue((subKey, name), out var value) ? value : null;

        public void DeleteTree(string subKey)
        {
            foreach (var key in Values.Keys.Where(k => k.Key == subKey || k.Key.StartsWith(subKey + @"\")).ToList())
                Values.Remove(key);
        }
    }

    [Fact]
    public void Register_WritesAPerUserUrlScheme_ThatPassesTheLinkLast()
    {
        var keys = new FakeKeys();

        DeepLinkRegistration.Register(keys, Exe);

        Assert.Equal("", keys.GetValue(@"Software\Classes\rambutan", "URL Protocol"));
        Assert.Equal("URL:Rambutan link", keys.GetValue(@"Software\Classes\rambutan", ""));
        Assert.Equal($"\"{Exe}\" --deep-link \"%1\"",
            keys.GetValue(@"Software\Classes\rambutan\shell\open\command", ""));
        Assert.Equal($"\"{Exe}\",0", keys.GetValue(@"Software\Classes\rambutan\DefaultIcon", ""));
    }

    [Fact]
    public void Unregister_RemovesTheSchemeThatOpensThisInstall()
    {
        var keys = new FakeKeys();
        DeepLinkRegistration.Register(keys, Exe);

        Assert.True(DeepLinkRegistration.Unregister(keys, [Exe, Current]));

        Assert.Empty(keys.Values);
    }

    private const string Current = @"C:\Users\u\AppData\Local\Rambutan.Desktop\current\Rambutan.App.exe";

    [Fact]
    public void Unregister_MatchesTheCurrentExe_WhenTheStubWasMissingAtInstall()
    {
        var keys = new FakeKeys();
        DeepLinkRegistration.Register(keys, Current);

        Assert.True(DeepLinkRegistration.Unregister(keys, [Exe, Current]));

        Assert.Empty(keys.Values);
    }

    [Fact]
    public void Unregister_LeavesASchemeThatOpensSomethingElse()
    {
        var keys = new FakeKeys();
        DeepLinkRegistration.Register(keys, @"D:\Other\Rambutan.App.exe");

        Assert.False(DeepLinkRegistration.Unregister(keys, [Exe, Current]));
        Assert.False(DeepLinkRegistration.Unregister(new FakeKeys(), [Exe]));

        Assert.NotEmpty(keys.Values);
    }
}
