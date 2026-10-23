using System.Text;
using Rambutan.Core.Text;

namespace Rambutan.Core.Tests;

public class FileTextTests
{
    private const string Line = "[00:01.00]Café";

    static FileTextTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void Valid_utf8_is_utf8_whatever_the_legacy_page() =>
        Assert.Equal(Line, FileText.Decode(Encoding.UTF8.GetBytes(Line), Encoding.GetEncoding(936)));

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    public void A_byte_order_mark_names_the_encoding(string name)
    {
        var encoding = Encoding.GetEncoding(name);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(Line)];

        Assert.Equal(Line, FileText.Decode(bytes, Encoding.Latin1));
    }

    [Theory]
    [InlineData(1252, "[00:01.00]Café")]
    [InlineData(936, "[00:01.00]你好")]
    [InlineData(950, "[00:01.00]你好")]
    [InlineData(932, "[00:01.00]こんにちは")]
    public void Bytes_that_arent_utf8_are_read_in_the_legacy_page(int codePage, string text)
    {
        var legacy = Encoding.GetEncoding(codePage);

        Assert.Equal(text, FileText.Decode(legacy.GetBytes(text), legacy));
    }

    [Fact]
    public void The_default_legacy_page_reads_western_text()
    {
        // Windows-1252 outside Windows, and the ANSI code page of CI's and most
        // Western systems; no replacement characters either way.
        var text = FileText.Decode(Encoding.GetEncoding(1252).GetBytes(Line));

        Assert.DoesNotContain('�', text);
        Assert.StartsWith("[00:01.00]Caf", text);
    }
}
