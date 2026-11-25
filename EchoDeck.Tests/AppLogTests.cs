using System;
using System.IO;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

/// <summary>How tempo.log lines look: free-text lines and structured key=value events.</summary>
public class AppLogFormatTests
{
    private static readonly DateTime At = new(2026, 11, 24, 10, 15, 2);

    [Fact]
    public void A_line_has_one_space_between_its_parts_whatever_is_missing()
    {
        Assert.Equal("2026-11-24 10:15:02 [Where]", AppLog.FormatLine(At, "Where", null, null, true));
        Assert.Equal("2026-11-24 10:15:02 [Where] a note", AppLog.FormatLine(At, "Where", null, "a note", true));
        Assert.Equal("2026-11-24 10:15:02 [Where] InvalidOperationException: boom",
            AppLog.FormatLine(At, "Where", new InvalidOperationException("boom"), null, true));
        Assert.Equal("2026-11-24 10:15:02 [Where] a note InvalidOperationException",
            AppLog.FormatLine(At, "Where", new InvalidOperationException("boom"), "a note", includeMessage: false));
    }

    [Fact]
    public void An_event_has_a_level_a_name_and_key_value_fields()
    {
        string line = AppLog.FormatEvent(At, AppLogLevel.Warning, "settings.save-failed",
            new (string, object?)[] { ("exception", "IOException"), ("retries", 3) });

        Assert.Equal("2026-11-24 10:15:02 WARN settings.save-failed exception=IOException retries=3", line);
    }

    [Theory]
    [InlineData(AppLogLevel.Info, "INFO")]
    [InlineData(AppLogLevel.Warning, "WARN")]
    [InlineData(AppLogLevel.Error, "ERROR")]
    public void The_level_is_written_as_a_word_to_search_for(AppLogLevel level, string word) =>
        Assert.Contains(" " + word + " ", AppLog.FormatEvent(At, level, "x", Array.Empty<(string, object?)>()));

    [Fact]
    public void Values_with_spaces_quotes_or_line_breaks_are_quoted_and_stay_on_one_line()
    {
        Assert.Equal("simple", AppLog.FormatValue("simple"));
        Assert.Equal("42", AppLog.FormatValue(42));
        Assert.Equal("1.5", AppLog.FormatValue(1.5));          // never a decimal comma
        Assert.Equal("true", AppLog.FormatValue(true));
        Assert.Equal("-", AppLog.FormatValue(null));
        Assert.Equal("\"two words\"", AppLog.FormatValue("two words"));
        Assert.Equal("\"say \\\"hi\\\"\"", AppLog.FormatValue("say \"hi\""));
        Assert.Equal("\"a\\r\\nb\"", AppLog.FormatValue("a\r\nb"));
        Assert.Equal("\"\"", AppLog.FormatValue(""));
        Assert.Equal("\"a=b\"", AppLog.FormatValue("a=b"));
    }
}

[Collection("AppLog")]
public sealed class AppLogFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tempo-applog-tests-" + Guid.NewGuid().ToString("N"));

    public AppLogFileTests() => AppLog.DirectoryOverride = _root;

    public void Dispose()
    {
        AppLog.DirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* temp folder */ }
    }

    [Fact]
    public void Events_and_lines_end_up_one_per_line_in_the_same_file()
    {
        AppLog.Event(AppLogLevel.Error, "test.event", ("code", 7));
        AppLog.Write("Test", note: "a free-text line");

        var lines = File.ReadAllLines(AppLog.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("ERROR test.event code=7", lines[0]);
        Assert.EndsWith("[Test] a free-text line", lines[1]);
    }

    [Fact]
    public void Logging_into_a_place_that_cannot_be_written_never_throws()
    {
        string file = Path.Combine(_root, "a-file");
        Directory.CreateDirectory(_root);
        File.WriteAllText(file, "x");
        AppLog.DirectoryOverride = Path.Combine(file, "inside-a-file");   // a folder cannot be made inside a file

        var thrown = Record.Exception(() => AppLog.Event(AppLogLevel.Info, "never.thrown"));

        Assert.Null(thrown);
    }
}
