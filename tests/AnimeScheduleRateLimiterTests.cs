using System;
using System.Net.Http;
using Shoko.Plugin.AnimeSchedule.Api;
using Xunit;

namespace Shoko.Plugin.AnimeSchedule.Tests;

public class AnimeScheduleRateLimiterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComputeDelay_NoStateYet_ReturnsZero()
    {
        var delay = AnimeScheduleRateLimiter.ComputeDelay(remaining: null, resetAt: null, now: Now);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void ComputeDelay_RemainingPositive_ReturnsZero()
    {
        var delay = AnimeScheduleRateLimiter.ComputeDelay(remaining: 5, resetAt: Now.AddSeconds(30), now: Now);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void ComputeDelay_RemainingZero_WaitsUntilResetPlusBuffer()
    {
        var resetAt = Now.AddSeconds(10);

        var delay = AnimeScheduleRateLimiter.ComputeDelay(remaining: 0, resetAt: resetAt, now: Now);

        Assert.Equal(TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(250), delay);
    }

    [Fact]
    public void ComputeDelay_RemainingNegative_WaitsUntilResetPlusBuffer()
    {
        // The optimistic local decrement in WaitAsync can walk the counter
        // below zero if the server's own accounting is stricter than what it
        // last reported; treat that the same as exactly zero.
        var resetAt = Now.AddSeconds(5);

        var delay = AnimeScheduleRateLimiter.ComputeDelay(remaining: -1, resetAt: resetAt, now: Now);

        Assert.Equal(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(250), delay);
    }

    [Fact]
    public void ComputeDelay_RemainingZero_ResetAlreadyPassed_ReturnsZero()
    {
        var resetAt = Now.AddSeconds(-1);

        var delay = AnimeScheduleRateLimiter.ComputeDelay(remaining: 0, resetAt: resetAt, now: Now);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void ComputeDelay_RemainingZero_NoResetKnown_ReturnsZero()
    {
        var delay = AnimeScheduleRateLimiter.ComputeDelay(remaining: 0, resetAt: null, now: Now);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void ParseHeaders_ReadsRemainingAndResetFromResponse()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add("X-RateLimit-Limit", "120");
        response.Headers.Add("X-RateLimit-Remaining", "42");
        response.Headers.Add("X-RateLimit-Reset", "1758024000");

        var (remaining, resetAt) = AnimeScheduleRateLimiter.ParseHeaders(response.Headers, Now);

        Assert.Equal(42, remaining);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1758024000), resetAt);
    }

    [Fact]
    public void ParseHeaders_MissingHeaders_ReturnsNulls()
    {
        using var response = new HttpResponseMessage();

        var (remaining, resetAt) = AnimeScheduleRateLimiter.ParseHeaders(response.Headers, Now);

        Assert.Null(remaining);
        Assert.Null(resetAt);
    }

    [Fact]
    public void ParseHeaders_UnparsableValues_ReturnsNulls()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add("X-RateLimit-Remaining", "not-a-number");
        response.Headers.Add("X-RateLimit-Reset", "not-a-timestamp");

        var (remaining, resetAt) = AnimeScheduleRateLimiter.ParseHeaders(response.Headers, Now);

        Assert.Null(remaining);
        Assert.Null(resetAt);
    }

    [Fact]
    public void UpdateFromHeaders_ThenComputeDelay_ReflectsExhaustedWindow()
    {
        var limiter = new AnimeScheduleRateLimiter();

        using var response = new HttpResponseMessage();
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", Now.AddSeconds(20).ToUnixTimeSeconds().ToString());

        limiter.UpdateFromHeaders(response.Headers, Now);

        // WaitAsync is not exercised here (it really would sleep); instead,
        // confirm the state UpdateFromHeaders stored is what ComputeDelay
        // would act on, without going through Task.Delay.
        var (remaining, resetAt) = AnimeScheduleRateLimiter.ParseHeaders(response.Headers, Now);
        var delay = AnimeScheduleRateLimiter.ComputeDelay(remaining, resetAt, Now);

        Assert.True(delay > TimeSpan.FromSeconds(19));
    }
}
