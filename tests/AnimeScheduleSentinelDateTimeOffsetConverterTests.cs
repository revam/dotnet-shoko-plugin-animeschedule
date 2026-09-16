using System;
using System.Text.Json;
using Shoko.Plugin.AnimeSchedule.Api;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

public class AnimeScheduleSentinelDateTimeOffsetConverterTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Deserialize_SentinelString_IsNull()
    {
        var entry = JsonSerializer.Deserialize<AnimeScheduleTimetableEntry>(
            """{"delayedFrom":"0001-01-01T00:00:00Z"}""", Options);

        Assert.NotNull(entry);
        Assert.Null(entry.DelayedFrom);
    }

    [Fact]
    public void Deserialize_JsonNull_IsNull()
    {
        var entry = JsonSerializer.Deserialize<AnimeScheduleTimetableEntry>(
            """{"delayedFrom":null}""", Options);

        Assert.NotNull(entry);
        Assert.Null(entry.DelayedFrom);
    }

    [Fact]
    public void Deserialize_RealDate_IsParsed()
    {
        var entry = JsonSerializer.Deserialize<AnimeScheduleTimetableEntry>(
            """{"delayedFrom":"2026-09-20T15:00:00Z"}""", Options);

        Assert.NotNull(entry);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 15, 0, 0, TimeSpan.Zero), entry.DelayedFrom);
    }
}
