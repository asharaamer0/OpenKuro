using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kuro.Core;

namespace Kuro.UI;
public static class KeyframeMenu
{
    public static ContextMenu Create(Editor editor, Action<string> openGraph, Window? owner)
    {
        var menu = new ContextMenu(); var selected = editor.KeyTargets.ToArray(); var editable = selected.Length > 0 && selected.All(t => !t.Layer.Locked);
        string Binding(string id, string fallback) => Preferences.Current.Shortcuts.GetValueOrDefault(id, fallback);
        void Item(string label, Action run, string shortcut = "", string? icon = null, bool enabled = true)
        {
            var item = new MenuItem { Header = label, InputGestureText = shortcut, IsEnabled = enabled };
            if (icon != null) item.Icon = new Icon { Kind = icon, Width = 14, Height = 14 };
            item.Click += (_, _) => run(); menu.Items.Add(item);
        }
        Item("Graph Editor", () => openGraph(selected.FirstOrDefault()?.Property ?? "x"), Binding("timeline.graph", "Shift+F3"), "Graph", selected.Length > 0);
        menu.Items.Add(new Separator());
        Item("Easy Ease", () => editor.Execute(new("set_selected_interpolation", Text: "Easy Ease")), Binding("animation.ease", "F9"), "Graph", editable);
        Item("Ease In", () => editor.Execute(new("set_selected_interpolation", Text: "Ease In")), Binding("animation.easeIn", "Shift+F9"), null, editable);
        Item("Ease Out", () => editor.Execute(new("set_selected_interpolation", Text: "Ease Out")), Binding("animation.easeOut", "Ctrl+Shift+F9"), null, editable);
        var interpolation = new MenuItem { Header = "Interpolation", IsEnabled = editable };
        foreach (var type in new[] { "Linear", "Hold" }) { var item = new MenuItem { Header = type, IsCheckable = true, IsChecked = selected.Length > 0 && selected.All(k => k.Key.Interpolation == type) }; item.Click += (_, _) => editor.Execute(new("set_selected_interpolation", Text: type)); interpolation.Items.Add(item); } menu.Items.Add(interpolation);
        Item("Keyframe Value…", () =>
        {
            if (owner == null || selected.Length == 0) return;
            var text = Dialogs.Prompt(owner, "Keyframe value", "Value", selected[0].Key.Value.ToString("0.###", CultureInfo.InvariantCulture));
            if (text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) editor.Execute(new("set_selected_keyframe_value", Value: value));
        }, icon: "Text", enabled: editable);
        menu.Items.Add(new Separator());
        Item("Copy Keyframes", editor.CopyKeys, Binding("animation.copy", "Ctrl+C"), enabled: selected.Length > 0);
        Item("Paste Keyframes", () => editor.PasteKeys(editor.Playhead), Binding("animation.paste", "Ctrl+V"), enabled: editor.KeyClipboard.Count > 0);
        Item("Delete Keyframes", () => editor.Execute(new("delete_selected_keyframes")), Binding("layer.deleteBackspace", "Backspace"), "Close", editable);
        return menu;
    }
}
