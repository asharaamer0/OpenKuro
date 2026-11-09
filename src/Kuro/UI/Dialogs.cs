using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kuro.UI;
public static class Dialogs
{
    public static Window Window(string title, Window owner, double width = 480, double height = 520) => new() { Title = "Kuro · " + title, Owner = owner, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize, MinWidth = 360, MinHeight = 230 };
    public static TextBlock Label(string text, bool heading = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, heading ? 20 : 7, 0, 7), FontSize = heading ? 14 : 11, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, heading ? "Text" : "Muted"); return t;
    }
    public static TextBox Field(StackPanel panel, string label, string value)
    { panel.Children.Add(Label(label)); var box = new TextBox { Text = value }; panel.Children.Add(box); return box; }
    public static ComboBox Choice(StackPanel panel, string label, IEnumerable<string> values, string selected)
    { panel.Children.Add(Label(label)); var combo = new ComboBox { ItemsSource = values.ToList(), SelectedItem = selected }; panel.Children.Add(combo); return combo; }
    public static Button Button(string text, Action action, bool primary = false)
    { var button = new Button { Content = text, Margin = new Thickness(0, 8, 8, 0), HorizontalAlignment = HorizontalAlignment.Left }; if (primary) button.Style = (Style)Application.Current.FindResource("PrimaryButton"); button.Click += (_, _) => action(); return button; }
    public static string? Prompt(Window owner, string title, string label, string initial)
    {
        var window = Window(title, owner, 440, 240); var panel = new StackPanel { Margin = new Thickness(24) }; var field = Field(panel, label, initial);
        panel.Children.Add(Button("Apply", () => { window.DialogResult = true; }, true)); window.Content = panel; window.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        return window.ShowDialog() == true ? field.Text : null;
    }
}
