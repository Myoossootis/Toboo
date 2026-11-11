using System.Collections.Concurrent;
using System.Text;
using Toboo.Desktop.Services;
using Xunit;

namespace Toboo.Desktop.Tests;

public sealed class LcdTransportTests
{
    private sealed class Channel : ILcdSerialChannel
    {
        private readonly ConcurrentQueue<byte> incoming = new();
        public ConcurrentQueue<string> Written { get; } = new();
        public string Hello { get; set; } = "TOBOO_LCD,1,UNO,16,2\r\n";
        public string Capabilities { get; set; } = "ERR\r\n";
        public Func<string, string?> Reply { get; set; } = text => "OK," + text.Split(',')[1] + "\r\n";
        public bool Disposed { get; private set; }
        public bool Opened { get; private set; }
        public bool Discarded { get; private set; }
        public void Open() => Opened = true;
        public void DiscardInput() { Discarded = true; while (incoming.TryDequeue(out _)) { } }
        public void WriteLine(string text)
        {
            Written.Enqueue(text);
            var response = text switch { "TOBOO_LCD?" => Hello, "LCD_CAPS?" => Capabilities, _ => Reply(text) };
            if (response is not null) foreach (var value in Encoding.ASCII.GetBytes(response)) incoming.Enqueue(value);
        }
        public int ReadByte()
        {
            if (incoming.TryDequeue(out var value)) return value;
            Thread.Sleep(2);
            throw new TimeoutException();
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task NativeProtocolClearsOldInputValidatesHelloAndSendsBoundedLifetime()
    {
        var channel = new Channel();
        using var connection = await LcdConnection.OpenAsync(channel, default, TimeSpan.Zero);
        Assert.True(channel.Opened && channel.Discarded);
        Assert.False(connection.SupportsRoles);
        await connection.SendAsync("happy", 450, default);
        await connection.SendAsync("unknown", 1, default);
        var sent = channel.Written.ToArray();
        Assert.Equal("TOBOO_LCD?", sent[0]);
        Assert.Equal("LCD_CAPS?", sent[1]);
        Assert.StartsWith("E,happy,", sent[2]);
        Assert.InRange(int.Parse(sent[2].Split(',')[2]), 1, 450);
        Assert.Equal("E,unknown,1", sent[3]);
    }

    [Fact]
    public async Task AnimatedFirmwareRequiresAnAcknowledgementForTheExactRole()
    {
        var channel = new Channel { Capabilities = "LCD_CAPS,ROLE1\n", Reply = _ => "OK,R,thinking\n" };
        using var connection = await LcdConnection.OpenAsync(channel, default, TimeSpan.Zero);
        Assert.True(connection.SupportsRoles);
        Assert.True(await connection.SendRoleAsync(LcdRole.Thinking, 1200, default));
        Assert.StartsWith("R,thinking,", channel.Written.Last());
        Assert.InRange(int.Parse(channel.Written.Last().Split(',')[2]), 1, 1200);
        await Assert.ThrowsAsync<IOException>(() => connection.SendRoleAsync(LcdRole.Happy, 450, default));
    }

    [Fact]
    public async Task WrongFirmwareNeverReceivesADisplayCommand()
    {
        var channel = new Channel { Hello = "SOME_OTHER_DEVICE\n" };
        await Assert.ThrowsAsync<IOException>(() => LcdConnection.OpenAsync(channel, default, TimeSpan.Zero));
        Assert.True(channel.Disposed);
        Assert.Equal(["TOBOO_LCD?"], channel.Written.ToArray());
    }

    [Theory]
    [InlineData("happy", 0)]
    [InlineData("happy", 501)]
    [InlineData("Happy", 100)]
    [InlineData("happy\nE,angry", 100)]
    [InlineData("0", 100)]
    public async Task InvalidRequestsNeverReachSerial(string label, int ttl)
    {
        var channel = new Channel();
        using var connection = await LcdConnection.OpenAsync(channel, default, TimeSpan.Zero);
        await Assert.ThrowsAsync<ArgumentException>(() => connection.SendAsync(label, ttl, default));
        Assert.Equal(["TOBOO_LCD?", "LCD_CAPS?"], channel.Written.ToArray());
    }

    [Theory]
    [InlineData("OK,sad\n")]
    [InlineData("TOBOO_LCD_ERROR,-100\n")]
    [InlineData("\u0001\n")]
    public async Task IncorrectAckOrNonAsciiResponseFails(string response)
    {
        var channel = new Channel { Reply = _ => response };
        using var connection = await LcdConnection.OpenAsync(channel, default, TimeSpan.Zero);
        await Assert.ThrowsAsync<IOException>(() => connection.SendAsync("happy", 450, default));
    }

    [Fact]
    public async Task OversizedReplyCannotGrowAnUnboundedBuffer()
    {
        var channel = new Channel { Reply = _ => new string('x', 81) + "\n" };
        using var connection = await LcdConnection.OpenAsync(channel, default, TimeSpan.Zero);
        await Assert.ThrowsAsync<IOException>(() => connection.SendAsync("happy", 450, default));
    }

    [Fact]
    public async Task MissingAckTimesOutAndCancellationDoesNotQueueAnotherCue()
    {
        var channel = new Channel { Reply = _ => null };
        using var connection = await LcdConnection.OpenAsync(channel, default, TimeSpan.Zero);
        await Assert.ThrowsAsync<TimeoutException>(() => connection.SendAsync("happy", 450, default));
        using var cancel = new CancellationTokenSource();
        var pending = connection.SendAsync("sad", 450, cancel.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAsync("neutral", 450, default));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.DoesNotContain(channel.Written, text => text.StartsWith("E,neutral,"));
    }

    [Fact]
    public async Task CancelledOpenClosesTheOwnedSerialChannel()
    {
        var channel = new Channel();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LcdConnection.OpenAsync(channel, cancel.Token, TimeSpan.Zero));
        Assert.True(channel.Disposed);
        Assert.Empty(channel.Written);
    }
}
