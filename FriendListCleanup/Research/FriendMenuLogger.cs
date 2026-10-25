using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FriendListCleanup.Research;

/// <summary>
/// RESEARCH TOOL. Listens to the messages the game's Friend List logic receives, so we can learn
/// what "open the right-click menu for friend X" looks like and later send it ourselves.
///
/// It only observes: every message is passed straight through to the game unchanged.
/// It's off until you press "Start logging" in the Explore window, and it switches off again when you stop or unload.
/// Everything is also written to the Dalamud log (/xllog) with the tag [MenuLog].
/// </summary>
public sealed unsafe class FriendMenuLogger : IDisposable
{
    // The shape of the game function we listen to: an agent receiving an event with a list of values.
    private delegate AtkValue* ReceiveEventDelegate(AgentInterface* agent, AtkValue* returnValue, AtkValue* values, uint valueCount, ulong eventKind);

    private Hook<ReceiveEventDelegate>? friendHook;
    private Hook<ReceiveEventDelegate>? contextHook;
    private AgentInterface* friendAgent;
    private AgentInterface* contextAgent;

    private readonly object gate = new();
    private readonly List<string> lines = new();

    public bool IsRunning => friendHook?.IsEnabled ?? false;

    public string[] Lines { get { lock (gate) return lines.ToArray(); } }

    public void Clear() { lock (gate) lines.Clear(); }

    /// <summary>Must run on the framework thread.</summary>
    public void Start()
    {
        if (IsRunning)
            return;

        friendAgent = (AgentInterface*)AgentFriendlist.Instance();
        contextAgent = (AgentInterface*)AgentContext.Instance();
        if (friendAgent == null || contextAgent == null)
        {
            Add("Couldn't find the game's friend list or menu logic. Try again after opening /friendlist.");
            return;
        }

        // Slot 0 of each agent's function table is "ReceiveEvent".
        friendHook ??= Plugin.GameInterop.HookFromAddress<ReceiveEventDelegate>((*(nint**)friendAgent)[0], FriendDetour);
        contextHook ??= Plugin.GameInterop.HookFromAddress<ReceiveEventDelegate>((*(nint**)contextAgent)[0], ContextDetour);
        friendHook.Enable();
        contextHook.Enable();
        Add("Logging started. Right-click a friend in the game's Friend List, then press Escape. Don't click Remove.");
    }

    public void Stop()
    {
        friendHook?.Disable();
        contextHook?.Disable();
        Add("Logging stopped.");
    }

    private AtkValue* FriendDetour(AgentInterface* agent, AtkValue* ret, AtkValue* values, uint count, ulong kind)
    {
        var result = friendHook!.Original(agent, ret, values, count, kind);
        if (agent == friendAgent)
            Record("FriendList", agent, values, count, kind);
        return result;
    }

    private AtkValue* ContextDetour(AgentInterface* agent, AtkValue* ret, AtkValue* values, uint count, ulong kind)
    {
        var result = contextHook!.Original(agent, ret, values, count, kind);
        if (agent == contextAgent)
            Record("Menu", agent, values, count, kind);
        return result;
    }

    /// <summary>Writes one event to the list and the log. Never throws: a crash here would crash the game's UI.</summary>
    private void Record(string source, AgentInterface* agent, AtkValue* values, uint count, ulong kind)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append($"{DateTime.Now:HH:mm:ss} {source} kind={kind} values[{count}]:");
            for (var i = 0; i < count && i < 12; i++)
                sb.Append($" [{i}] {values[i].Type}={values[i].GetValueAsString()};");

            // After the game handled it: which friend does the friend list think is selected?
            var fl = (AgentFriendlist*)friendAgent;
            sb.Append($" | selected index={fl->SelectedIndex} contentId={fl->SelectedContentId} name=\"{fl->SelectedPlayerName}\"");

            if (source == "Menu")
            {
                var ctx = (AgentContext*)contextAgent;
                sb.Append($" | menu target contentId={ctx->TargetContentId} yesNoTarget={ctx->YesNoTargetContentId}");
            }

            Add(sb.ToString());
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[MenuLog] failed to record an event");
        }
    }

    private void Add(string line)
    {
        lock (gate)
        {
            lines.Add(line);
            if (lines.Count > 500)
                lines.RemoveAt(0);
        }
        Plugin.Log.Information($"[MenuLog] {line}");
    }

    public void Dispose()
    {
        friendHook?.Dispose();
        contextHook?.Dispose();
    }
}
