using Core.GOAP;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Core.Goals;

public abstract partial class GoapGoal
{
    public Dictionary<GoapKey, bool> Preconditions { get; } = new();
    public Dictionary<GoapKey, bool> Effects { get; } = new();

    private KeyAction[] keys = Array.Empty<KeyAction>();
    public KeyAction[] Keys
    {
        get => keys;
        protected set
        {
            keys = value;
            if (keys.Length == 1)
                DisplayName = $"{keys[0].Name} [{keys[0].Key}]";
        }
    }

    public abstract float Cost { get; }

    public string Name { get; }

    public string DisplayName { get; protected set; }

    public event Action<GoapEventArgs>? GoapEvent;

    protected GoapGoal(string name)
    {
        string output = RegexGoalName().Replace(name.Replace("Goal", ""), m => " " + m.Value.ToUpperInvariant());
        // ── Fix CP (log-115 evidence — root-cause companion to Fix CN/CO) ──
        //
        // The regex `\p{Lu}` (see RegexGoalName below) matches every uppercase
        // letter with no lookbehind. The replacement callback returns `" " + m.Value`,
        // which inserts a space BEFORE EVERY uppercase letter — including the
        // first one. So for `nameof(ApproachTargetGoal)` the intermediate string
        // becomes " Approach Target" (leading space + CamelCase split).
        //
        // The next line, `string.Concat(output[0].ToString().ToUpper(), output.AsSpan(1))`,
        // appears to intend "uppercase the first character." But output[0] is
        // ALWAYS the space the regex inserted before the first capital, and
        // ' '.ToUpper() is still ' '. The line is effectively a no-op for the
        // leading-space case. Net effect: Name and DisplayName always begin
        // with a space — e.g. Name = " Approach Target" rather than the
        // intended "Approach Target".
        //
        // Consequence: any consumer that compares Name by string equality
        // misses. This surfaced as Fix CN (LeaderStateService.DetermineStatus
        // switch on class-name strings) producing zero behavior change across
        // log-114 and log-115, traced via hex-dump of the LogNewGoal output
        // template ("New Plan= {name}") producing "New Plan=  Approach Target"
        // with double space. Fix CO worked around it by adding `.Trim()` at
        // the switch's read site; this fix (CP) addresses the root cause so
        // Fix CO becomes a defensive belt-and-suspenders rather than a
        // required workaround.
        //
        // Confirmed by inspection of all known callers: every Goal class uses
        // either `: base(nameof(XxxGoal))` (CamelCase class name) or
        // `: base("Follow " + filename)` (FRG). Both routes go through this
        // regex and acquire the leading space. The fix below catches both.
        //
        // The TrimStart strips ONLY leading whitespace. The downstream
        // string.Concat then uppercases the first non-whitespace character
        // (which, post-trim, is the actual first letter of the display name —
        // already uppercase since the regex inserted " X" pairs, but the
        // ToUpper() defends against pathological lowercase input).
        output = output.TrimStart();
        DisplayName = Name = string.Concat(output[0].ToString().ToUpper(), output.AsSpan(1));
    }

    public void SendGoapEvent(GoapEventArgs e)
    {
        GoapEvent?.Invoke(e);
    }

    public virtual bool CanRun() => true;

    public virtual void OnEnter() { }

    public virtual void OnExit() { }

    public virtual void Update() { }

    protected void AddPrecondition(GoapKey key, bool value)
    {
        Preconditions[key] = value;
    }
    protected void AddEffect(GoapKey key, bool value)
    {
        Effects[key] = value;
    }

    [GeneratedRegex(@"\p{Lu}")]
    private static partial Regex RegexGoalName();
}