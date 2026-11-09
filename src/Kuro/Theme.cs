using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Kuro;
public static class Theme
{
    public static bool Dark { get; private set; }
    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
    public static Color Color(string key) => ((SolidColorBrush)Get(key)).Color;
    public static void Apply(string mode)
    {
        Dark = mode == "Dark" || mode == "Follow Windows" && (int)(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) ?? 0) == 0;
        var dark = new Dictionary<string, string> { ["Workspace"] = "#0B0B0D", ["Panel"] = "#131316", ["Elevated"] = "#1B1B20", ["Border"] = "#26262D", ["Text"] = "#E8E8EC", ["Muted"] = "#8A8A94", ["Accent"] = "#FF4D2E", ["Primary"] = "#FF4D2E", ["OnPrimary"] = "#0B0B0D", ["Selection"] = "#25252B", ["Hover"] = "#24242B", ["Success"] = "#9BACAA", ["Error"] = "#F69C94" };
        var light = new Dictionary<string, string> { ["Workspace"] = "#F5F1EA", ["Panel"] = "#FCFAF6", ["Elevated"] = "#FFFFFF", ["Border"] = "#D9D1C5", ["Text"] = "#29231D", ["Muted"] = "#6E6257", ["Accent"] = "#A84518", ["Primary"] = "#AD4619", ["OnPrimary"] = "#FFFFFF", ["Selection"] = "#F8E5D4", ["Hover"] = "#EBE5DB", ["Success"] = "#326D4F", ["Error"] = "#A42E29" };
        foreach (var (key, value) in Dark ? dark : light) Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        if (!Application.Current.Resources.Contains("StylesLoaded"))
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Kuro;component/Styles.xaml", UriKind.Relative) });
            Application.Current.Resources["StylesLoaded"] = true;
        }
    }
}
