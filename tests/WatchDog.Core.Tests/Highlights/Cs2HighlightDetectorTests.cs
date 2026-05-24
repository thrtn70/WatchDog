using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Highlights;
using WatchDog.Core.Highlights.Cs2;

namespace WatchDog.Core.Tests.Highlights;

/// <summary>
/// Stream that drip-feeds data in fixed-size chunks to exercise multi-read loops.
/// </summary>
file sealed class ChunkedStream(byte[] data, int chunkSize) : System.IO.Stream
{
    private int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var toRead = Math.Min(Math.Min(count, chunkSize), data.Length - _pos);
        data.AsSpan(_pos, toRead).CopyTo(buffer.AsSpan(offset));
        _pos += toRead;
        return toRead;
    }
    public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public sealed class Cs2HighlightDetectorTests
{
    private readonly Cs2HighlightDetector _detector = new(NullLogger<Cs2HighlightDetector>.Instance);

    [Fact]
    public void DetectHighlights_KillIncrease_EmitsKill()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new Cs2GameState { Kills = 3, Health = 100 };
        var curr = new Cs2GameState { Kills = 4, Health = 100 };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.Kill, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_HealthDropsToZero_EmitsDeath()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new Cs2GameState { Health = 60 };
        var curr = new Cs2GameState { Health = 0 };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.Death, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_RoundWon_EmitsRoundWin()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new Cs2GameState { RoundPhase = "live", TeamScore = 3, EnemyScore = 2 };
        var curr = new Cs2GameState { RoundPhase = "over", TeamScore = 4, EnemyScore = 2 };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.RoundWin, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_RoundLost_EmitsRoundLoss()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new Cs2GameState { RoundPhase = "live", TeamScore = 3, EnemyScore = 4 };
        var curr = new Cs2GameState { RoundPhase = "over", TeamScore = 3, EnemyScore = 5 };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.RoundLoss, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_FiveRoundKills_EmitsAce()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new Cs2GameState { RoundKills = 4, Kills = 9, Health = 100 };
        var curr = new Cs2GameState { RoundKills = 5, Kills = 10, Health = 100 };

        _detector.DetectHighlights(prev, curr);

        // Should emit both Kill (kills increased) and Ace (round_kills == 5)
        Assert.Contains(events, e => e.Type == HighlightType.Kill);
        Assert.Contains(events, e => e.Type == HighlightType.Ace);
    }

    [Fact]
    public void DetectHighlights_MatchWin_EmitsMatchWin()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new Cs2GameState { MapPhase = "live", TeamScore = 12, EnemyScore = 10 };
        var curr = new Cs2GameState { MapPhase = "gameover", TeamScore = 13, EnemyScore = 10 };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.MatchWin, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_NoPreviousState_NoEvents()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        _detector.DetectHighlights(null, new Cs2GameState { Kills = 5 });

        Assert.Empty(events);
    }

    [Fact]
    public void DetectHighlights_NoChange_NoEvents()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var state = new Cs2GameState { Kills = 5, Health = 100, RoundPhase = "live" };
        _detector.DetectHighlights(state, state);

        Assert.Empty(events);
    }

    [Fact]
    public void GameExecutableName_IsCs2()
    {
        Assert.Equal("cs2.exe", _detector.GameExecutableName);
    }

    // ── Streaming-cap tests ────────────────────────────────────────────────

    [Fact]
    public async Task ReadBodyCappedAsync_StreamExceedsCap_ReturnsOversize()
    {
        var data = new byte[Cs2HighlightDetector.MaxPayloadBytes + 1];
        using var stream = new System.IO.MemoryStream(data);

        var (body, oversize) = await Cs2HighlightDetector.ReadBodyCappedAsync(stream, Cs2HighlightDetector.MaxPayloadBytes);

        Assert.True(oversize);
        Assert.Null(body);
    }

    [Fact]
    public async Task ReadBodyCappedAsync_StreamExactlyAtCap_ReturnsBody()
    {
        var data = new byte[Cs2HighlightDetector.MaxPayloadBytes];
        using var stream = new System.IO.MemoryStream(data);

        var (body, oversize) = await Cs2HighlightDetector.ReadBodyCappedAsync(stream, Cs2HighlightDetector.MaxPayloadBytes);

        Assert.False(oversize);
        Assert.NotNull(body);
    }

    [Fact]
    public async Task ReadBodyCappedAsync_SmallPayload_ReturnsCorrectBody()
    {
        const string content = "{\"auth\":{\"token\":\"watchdog_gsi_v1\"}}";
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

        var (body, oversize) = await Cs2HighlightDetector.ReadBodyCappedAsync(stream, Cs2HighlightDetector.MaxPayloadBytes);

        Assert.False(oversize);
        Assert.Equal(content, body);
    }

    [Fact]
    public async Task ReadBodyCappedAsync_ChunkedStreamExceedsCap_ReturnsOversize()
    {
        // Deliver 1000-byte chunks to exercise the accumulation loop (not a single ReadAsync).
        var data = new byte[Cs2HighlightDetector.MaxPayloadBytes + 1];
        using var stream = new ChunkedStream(data, chunkSize: 1000);

        var (body, oversize) = await Cs2HighlightDetector.ReadBodyCappedAsync(stream, Cs2HighlightDetector.MaxPayloadBytes);

        Assert.True(oversize);
        Assert.Null(body);
    }

    [Fact]
    public async Task ReadBodyCappedAsync_ChunkedStreamWithinCap_ReturnsBody()
    {
        const string content = "chunked payload that arrives in pieces";
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using var stream = new ChunkedStream(bytes, chunkSize: 5);

        var (body, oversize) = await Cs2HighlightDetector.ReadBodyCappedAsync(stream, Cs2HighlightDetector.MaxPayloadBytes);

        Assert.False(oversize);
        Assert.Equal(content, body);
    }
}
