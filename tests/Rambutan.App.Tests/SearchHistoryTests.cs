using Rambutan.App.Services;

namespace Rambutan.App.Tests;

public sealed class SearchHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void NewestComesFirst_AndARepeatMovesUp_InItsLatestSpelling()
    {
        var history = new SearchHistory(null);
        history.Add("daft punk");
        history.Add("justice");
        history.Add("  Daft Punk ");

        Assert.Equal(["Daft Punk", "justice"], history.Queries);
    }

    [Fact]
    public void BlankQueries_AreNotKept()
    {
        var history = new SearchHistory(null);
        history.Add("   ");

        Assert.Empty(history.Queries);
    }

    [Fact]
    public void KeepsAtMostTheCap_DroppingTheOldest()
    {
        var history = new SearchHistory(null);
        for (var i = 0; i < SearchHistory.MaxKept + 5; i++)
            history.Add($"q{i}");

        Assert.Equal(SearchHistory.MaxKept, history.Queries.Count);
        Assert.Equal($"q{SearchHistory.MaxKept + 4}", history.Queries[0]);
        Assert.DoesNotContain("q0", history.Queries);
    }

    [Fact]
    public void RoundTripsThroughTheFile()
    {
        var store = new SearchHistoryStore(_dir);
        var history = new SearchHistory(store);
        history.Add("daft punk");
        history.Add("justice");
        history.Remove("daft punk");

        Assert.Equal(["justice"], new SearchHistory(store).Queries);
        Assert.Contains("\"version\":1", File.ReadAllText(store.Path));
    }

    [Fact]
    public void Clear_EmptiesTheFileToo()
    {
        var store = new SearchHistoryStore(_dir);
        var history = new SearchHistory(store);
        history.Add("daft punk");
        history.Clear();

        Assert.Empty(new SearchHistory(store).Queries);
    }

    [Theory]
    [InlineData("""{"version":2,"queries":["from the future"]}""")]
    [InlineData("not json")]
    public void ANewerOrCorruptFile_ReadsAsEmpty(string content)
    {
        Directory.CreateDirectory(_dir);
        var store = new SearchHistoryStore(_dir);
        File.WriteAllText(store.Path, content);

        Assert.Empty(new SearchHistory(store).Queries);
    }

    [Fact]
    public void AHandEditedFile_IsTidiedOnLoad()
    {
        Directory.CreateDirectory(_dir);
        var store = new SearchHistoryStore(_dir);
        File.WriteAllText(store.Path, """{"version":1,"queries":["a"," ","A","b"]}""");

        Assert.Equal(["a", "b"], new SearchHistory(store).Queries);
    }
}
