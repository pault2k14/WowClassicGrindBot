using Core.Goals;
using Core.GOAP;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;

namespace Core;

public enum ChatMessageType
{
    Whisper,
    Say,
    Yell,
    Emote,
    Party
}

public readonly record struct ChatMessageEntry(DateTime Time, ChatMessageType Type, string Author, string Message);

public sealed class ChatReader : IReader
{
    private const int cMsg = 98;
    private const int cMeta = 99;

    private readonly ILogger<ChatReader> logger;

    private readonly StringBuilder sb = new(12 + 1 + 256);

    public ObservableCollection<ChatMessageEntry> Messages { get; } = new();
    private const int MsgIdRadix = 1 << 20; // 1048576
    public bool ForcedFollow;

    private static string DecodeChatPart(int number, int take)
    {
        // number is n1*10000 + n2*100 + n3 (each 0..100)
        int n1 = number / 10000;
        int n2 = (number / 100) % 100;
        int n3 = number % 100;

        Span<char> buffer = stackalloc char[3];

        switch (take)
        {
            case 1:
                buffer[0] = (char)n3;
                return buffer[..1].ToString();

            case 2:
                buffer[0] = (char)n2;
                buffer[1] = (char)n3;
                return buffer[..2].ToString();

            default: // 3
                buffer[0] = (char)n1;
                buffer[1] = (char)n2;
                buffer[2] = (char)n3;
                return buffer[..3].ToString();
        }
    }

    private static (int msgId, int number) UnpackMsg(int packed)
    {
        int msgId = packed / MsgIdRadix;
        int number = packed - msgId * MsgIdRadix; // faster than %
        return (msgId, number);
    }

    public ChatReader(ILogger<ChatReader> logger)
    {
        this.logger = logger;
    }

    private int _currentMsgId = -1;
    private readonly HashSet<int> _seenHeads = new();

    private long _lastProgressTicks = 0;     // last time we appended a valid chunk
    private const int ResetAfterMs = 750;    // tune: 250-1000ms typical
    private const int SilenceResetAfterMs = 2000; // if meta stays 0 for long, clear state
    private int _lastDbgMeta = -1;
    private int _lastDbgPacked = -1;

    private void ResetAssembly()
    {
        _currentMsgId = -1;
        _seenHeads.Clear();
        sb.Clear();
        _lastProgressTicks = 0;
    }

    public void Update(IAddonDataProvider reader)
    {
        long now = Environment.TickCount64;

        int meta = reader.GetInt(cMeta);
        int packed = reader.GetInt(cMsg);

        // optional debug
        if (meta == _lastDbgMeta && packed == _lastDbgPacked)
            return; // identical frame, ignore completely

        //_lastDbgMeta = meta;
        //_lastDbgPacked = packed;
        //logger.LogDebug($"meta={meta} packed={packed}");

        if (meta == 0 || packed == 0)
            return;

        ChatMessageType type = (ChatMessageType)(meta / 1_000_000);
        int length = (meta % 1_000_000) / 1000;
        int head = meta % 1000;

        int head0 = head - 1;
        int remaining = length - head0;
        int take = Math.Min(3, remaining);
        if (take <= 0) return;

        var (msgId, number) = UnpackMsg(packed);
        if ((uint)msgId > 15) return;

        // timeout: if we're mid-assembly and stalled, drop it
        if (sb.Length > 0 && _lastProgressTicks != 0 && (now - _lastProgressTicks) > ResetAfterMs)
        {
            ResetAssembly();
        }

        // HARD start rule:
        // If we're idle and we see head=1, start a new message even if msgId repeats/wraps.
        if (sb.Length == 0 && head == 1)
        {
            _currentMsgId = msgId;
            _seenHeads.Clear();
            sb.Clear();
            _lastProgressTicks = 0;
        }
        else if (msgId != _currentMsgId)
        {
            // msgId changed -> new message boundary
            _currentMsgId = msgId;
            _seenHeads.Clear();
            sb.Clear();
            _lastProgressTicks = 0;
        }

        // If we are idle and not at head=1, ignore completely (DON'T poison _seenHeads)
        if (sb.Length == 0 && head != 1)
            return;

        // Strict ordering once started
        if (sb.Length != head0)
            return;

        // Deduplicate only AFTER ordering checks pass
        if (!_seenHeads.Add(head))
            return;

        string part = DecodeChatPart(number, take);
        sb.Append(part);
        _lastProgressTicks = now;

        if (head0 + take < length)
            return;

        // Completed message
        string text = sb.ToString().ToLowerInvariant();
        sb.Clear();
        // Keep _seenHeads until next head=1/msgId change to ignore held last chunk.

        int firstSpaceIdx = text.AsSpan().IndexOf(' ');
        if (firstSpaceIdx == -1)
        {
            ResetAssembly();
            return;
        }

        string author = text[..firstSpaceIdx];
        string msg = text[(firstSpaceIdx + 1)..];

        if(type == ChatMessageType.Party && msg.Equals("follow me"))
        {
            logger.LogInformation("Received follow me");
            ForcedFollow = true;
        }

        if (type == ChatMessageType.Party && msg.Equals("stop following me"))
        {
            logger.LogInformation("Received stop following me");
            ForcedFollow = false;
        }

        Messages.Add(new ChatMessageEntry(DateTime.Now, type, author, msg));
        logger.LogInformation($"[{type}] {author}: {msg}");
    }
}
