using Core.Goals;
using Core.GOAP;
using Game;
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
    private readonly IBotController botController;

    // Reads Mode from the currently-loaded profile.
    // Returns null if no profile is loaded yet — treated as "no mode match" in all guards.
    private Mode? CurrentMode => botController.ClassConfig?.Mode;

    private readonly ILogger<ChatReader> logger;

    private readonly StringBuilder sb = new(12 + 1 + 256);

    public ObservableCollection<ChatMessageEntry> Messages { get; } = new();
    private const int MsgIdRadix = 1 << 20; // 1048576

    // <summary>
    /// Set on the LEADER bot when the assist sends "leader what is your position?"
    /// GoapAgent polls this and fires the LeaderReplyPosition macro to reply.
    /// Reset by GoapAgent immediately after firing the macro.
    /// </summary>
    public bool AssistRequestedPosition;

    // --- Leader/Assist state flags ---

    public bool ForcedFollow;
    public bool AssistIsFollowing;
    public bool AssistRequestReturn;
    public float AssistXPos;
    public float AssistYPos;

    /// <summary>
    /// Set on the ASSIST bot when the leader replies with "position: x,y".
    /// FollowFocusGoal reads this to get the leader's map coordinates.
    /// Reset by FollowFocusGoal after consuming the values.
    /// </summary>
    public bool LeaderPositionReceived;
    public float LeaderXPos;
    public float LeaderYPos;

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

    public ChatReader(IBotController botController, ILogger<ChatReader> logger)
    {
        this.botController = botController;
        this.logger = logger;
    }

    private int _currentMsgId = -1;
    private readonly HashSet<int> _seenHeads = new();

    private long _lastProgressTicks = 0;
    private const int ResetAfterMs = 750;
    private const int SilenceResetAfterMs = 2000;
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

        if (meta == _lastDbgMeta && packed == _lastDbgPacked)
            return;

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

        if (sb.Length > 0 && _lastProgressTicks != 0 && (now - _lastProgressTicks) > ResetAfterMs)
        {
            ResetAssembly();
        }

        if (sb.Length == 0 && head == 1)
        {
            _currentMsgId = msgId;
            _seenHeads.Clear();
            sb.Clear();
            _lastProgressTicks = 0;
        }
        else if (msgId != _currentMsgId)
        {
            _currentMsgId = msgId;
            _seenHeads.Clear();
            sb.Clear();
            _lastProgressTicks = 0;
        }

        if (sb.Length == 0 && head != 1)
            return;

        if (sb.Length != head0)
            return;

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

        int firstSpaceIdx = text.AsSpan().IndexOf(' ');
        if (firstSpaceIdx == -1)
        {
            ResetAssembly();
            return;
        }

        string author = text[..firstSpaceIdx];
        string msg = text[(firstSpaceIdx + 1)..];

        // --- Existing message handlers ---

        if (type == ChatMessageType.Party && msg.Equals("follow me"))
        {
            logger.LogInformation("Received follow me");
            ForcedFollow = true;
        }

        if (type == ChatMessageType.Party && msg.Equals("stop following me"))
        {
            logger.LogInformation("Received stop following me");
            ForcedFollow = false;
        }

        if ((CurrentMode == Mode.PartyLeader) && type == ChatMessageType.Party && msg.Equals("i'm following"))
        {
            logger.LogInformation("Received i'm following");
            AssistIsFollowing = true;
            AssistRequestReturn = false;
        }

        if ((CurrentMode == Mode.PartyLeader) && type == ChatMessageType.Party && msg.Equals("i'm not following"))
        {
            logger.LogInformation("Received i'm not following");
            AssistIsFollowing = false;
        }

        // "i tried following but you are too far away my position:x,y"
        if ((CurrentMode == Mode.PartyLeader) && type == ChatMessageType.Party && msg.Contains("i tried following but you are too far away my position:"))
        {
            logger.LogInformation("Received: " + msg);
            var msgSubstrings = msg.Split(":");
            if (msgSubstrings.Length != 2)
            {
                logger.LogInformation("msgSubstrings length is not 2, it is " + msgSubstrings.Length);
                for (int i = 0; i < msgSubstrings.Length; i++)
                    logger.LogInformation("msgSubstrings[" + i + "]: " + msgSubstrings[i]);
            }
            else
            {
                logger.LogInformation("coordinate substring: " + msgSubstrings[1]);
                var coordinateSubstrings = msgSubstrings[1].Split(",");
                if (coordinateSubstrings.Length != 2)
                {
                    logger.LogInformation("coordinateSubstrings length is not 2, it is " + coordinateSubstrings.Length);
                }
                else
                {
                    AssistRequestReturn = true;
                    AssistXPos = float.Parse(coordinateSubstrings[0]);
                    AssistYPos = float.Parse(coordinateSubstrings[1]);
                }
            }
        }

        // --- New: ASSIST side receives "position: x,y" from leader ---
        // Expected format: "position: x,y"
        if ((CurrentMode == Mode.AssistFocus) && type == ChatMessageType.Party && msg.StartsWith("position: "))
        {
            logger.LogInformation("[ChatReader] Received leader position: " + msg);
            string coords = msg["position: ".Length..];
            var parts = coords.Split(",");
            if (parts.Length == 2 &&
                float.TryParse(parts[0].Trim(), out float lx) &&
                float.TryParse(parts[1].Trim(), out float ly))
            {
                LeaderXPos = lx;
                LeaderYPos = ly;
                LeaderPositionReceived = true;
                logger.LogInformation($"[ChatReader] Leader position parsed: X={lx} Y={ly}");
            }
            else
            {
                logger.LogWarning("[ChatReader] Failed to parse leader position from: " + msg);
            }
        }

        // --- New: LEADER side receives "leader what is your position?" from assist ---
        if ((CurrentMode == Mode.PartyLeader) && type == ChatMessageType.Party && msg.Equals("leader what is your position?"))
        {
            logger.LogInformation("[ChatReader] Received position request from assist");
            AssistRequestedPosition = true;
        }

        Messages.Add(new ChatMessageEntry(DateTime.Now, type, author, msg));
        logger.LogInformation($"[{type}] {author}: {msg}");
    }
}
