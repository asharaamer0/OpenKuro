using System.Windows.Input;
using Kuro.Core;

namespace Kuro.UI;
public record EditorAction(string Id, string Name, string DefaultShortcut, Action Run);
public sealed class ActionRegistry
{
    public List<EditorAction> Actions { get; } = [];
    public void Add(string id, string name, string shortcut, Action run) => Actions.Add(new(id, name, shortcut, run));
    public string Binding(EditorAction action) => Preferences.Current.Shortcuts.TryGetValue(action.Id, out var custom) ? custom : action.DefaultShortcut;
    public bool Dispatch(KeyEventArgs e, string? onlyAction = null)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        foreach (var action in Actions)
        {
            if (onlyAction != null && action.Id != onlyAction) continue;
            var shortcut = Binding(action); if (shortcut == "") continue;
            if (Parse(shortcut, out var boundKey, out var modifiers) && boundKey == key && modifiers == Keyboard.Modifiers) { action.Run(); e.Handled = true; return true; }
        }
        return false;
    }
    public static bool Valid(string shortcut) => shortcut == "" || Parse(shortcut, out _, out _);
    public static bool Parse(string shortcut, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None; modifiers = ModifierKeys.None; var parts = shortcut.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].ToLowerInvariant() switch { "ctrl" or "control" => ModifierKeys.Control, "alt" => ModifierKeys.Alt, "shift" => ModifierKeys.Shift, _ => ModifierKeys.None };
            if (modifier == ModifierKeys.None || modifiers.HasFlag(modifier)) return false; modifiers |= modifier;
        }
        var final = parts[^1]; if (final.Length == 1 && char.IsDigit(final[0])) final = "D" + final;
        return Enum.TryParse(final, true, out key) && key != Key.None && !final.All(char.IsDigit);
    }
}
