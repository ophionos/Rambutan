using Rambutan.Integrations.Linux;

namespace Rambutan.Integrations.Tests;

/// <summary>
/// Against a real Secret Service. These write keyring items, so they run only
/// when <c>RAMBUTAN_TEST_SECRET_SERVICE=1</c> is set, inside a private bus with
/// a throwaway keyring:
/// <c>dbus-run-session -- bash -c 'echo -n pw | gnome-keyring-daemon --unlock --components=secrets; RAMBUTAN_TEST_SECRET_SERVICE=1 dotnet test …'</c>.
/// Never against the keyring of a real session.
/// </summary>
public sealed class SecretServiceStoreTests
{
    private static async Task<SecretServiceStore> ConnectAsync()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RAMBUTAN_TEST_SECRET_SERVICE") == "1",
            "Needs a throwaway Secret Service (RAMBUTAN_TEST_SECRET_SERVICE=1).");
        var store = await SecretServiceStore.ConnectAsync("Test keyring");
        Assert.NotNull(store);
        return store;
    }

    private static Dictionary<string, string> Attributes() => new()
    {
        ["application"] = "rambutan-tests",
        ["kind"] = "test",
        ["data-dir"] = "/tmp/" + Guid.NewGuid().ToString("N"),
    };

    [Fact]
    public async Task WritesReadsReplacesAndDeletes()
    {
        using var store = await ConnectAsync();
        var attributes = Attributes();

        Assert.Null(store.Read(attributes));

        store.Write(attributes, "Rambutan test", "SAPISID=first; ü");
        Assert.Equal("SAPISID=first; ü", store.Read(attributes));

        store.Write(attributes, "Rambutan test", "SAPISID=second");
        Assert.Equal("SAPISID=second", store.Read(attributes));

        store.Delete(attributes);
        Assert.Null(store.Read(attributes));
    }

    [Fact]
    public async Task ItemsAreFoundByAllTheirAttributes()
    {
        using var store = await ConnectAsync();
        var mine = Attributes();
        var other = new Dictionary<string, string>(mine) { ["data-dir"] = mine["data-dir"] + "-other" };

        store.Write(mine, "Rambutan test", "mine");
        store.Write(other, "Rambutan test", "other");
        store.Delete(other);

        Assert.Equal("mine", store.Read(mine));
        store.Delete(mine);
    }
}
