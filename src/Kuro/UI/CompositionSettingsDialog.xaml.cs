using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Kuro.Core;

namespace Kuro.UI;

public partial class CompositionSettingsDialog : Window
{
    record SizeOption(string Label, int Width, int Height);
    record RateOption(string Label, FrameRate Rate);

    static readonly SizeOption[] Resolutions =
    [
        new("Custom", 0, 0), new("720p HD · 1280 × 720", 1280, 720),
        new("1080p Full HD · 1920 × 1080", 1920, 1080), new("1440p QHD · 2560 × 1440", 2560, 1440),
        new("2160p 4K UHD · 3840 × 2160", 3840, 2160), new("Portrait · 1080 × 1920", 1080, 1920),
        new("Square · 1080 × 1080", 1080, 1080), new("Portrait 4:5 · 1080 × 1350", 1080, 1350)
    ];
    static readonly SizeOption[] Ratios =
    [
        new("Custom", 0, 0), new("16:9", 16, 9), new("9:16", 9, 16), new("1:1", 1, 1),
        new("4:3", 4, 3), new("3:2", 3, 2), new("4:5", 4, 5), new("21:9", 7, 3)
    ];

    readonly Composition composition;
    readonly Action<string, int, int, FrameRate, long, string> commit;
    bool updatingDimensions;
    string defaultDuration = "";

    public CompositionSettingsDialog(Window? owner, Composition composition, Action<string, int, int, FrameRate, long, string> commit)
    {
        this.composition = composition;
        this.commit = commit;
        InitializeComponent();
        Owner = owner;
        SourceInitialized += (_, _) => NativeChrome.Match(this);
        Resolution.ItemsSource = Resolutions;
        Aspect.ItemsSource = Ratios;
        NameField.Text = composition.Name;
        SetDimensions(composition.Width, composition.Height);
        BackgroundField.Text = composition.Background;
        UpdateSwatch();

        var rates = new FrameRate[] { new(24000, 1001), new(24), new(25), new(30000, 1001), new(30), new(48), new(50), new(60000, 1001), new(60), new(120) };
        var options = rates.Append(composition.Fps).Distinct().OrderBy(rate => rate.Value)
            .Select(rate => new RateOption($"{rate.Value.ToString("0.###", CultureInfo.InvariantCulture)} fps", rate)).ToList();
        FrameRates.ItemsSource = options;
        FrameRates.SelectedItem = options.First(item => item.Rate == composition.Fps);
        SetDefaultDuration(composition.Fps);

        Resolution.SelectionChanged += (_, _) =>
        {
            if (!updatingDimensions && Resolution.SelectedItem is SizeOption { Width: > 0 } preset)
                SetDimensions(preset.Width, preset.Height);
        };
        Aspect.SelectionChanged += (_, _) => ApplyAspect();
        WidthField.TextChanged += (_, _) => MatchDimensions();
        HeightField.TextChanged += (_, _) => MatchDimensions();
        FrameRates.SelectionChanged += (_, _) =>
        {
            if (DurationField.Text == defaultDuration && FrameRates.SelectedItem is RateOption option)
                SetDefaultDuration(option.Rate);
        };
        BackgroundField.TextChanged += (_, _) => UpdateSwatch();
        Loaded += (_, _) => NameField.Focus();
    }

    void SetDefaultDuration(FrameRate rate)
    {
        var ms = (long)Math.Round(rate.Seconds(composition.Duration) * 1000, MidpointRounding.AwayFromZero);
        defaultDuration = FormattableString.Invariant($"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}:{ms % 1000:000}");
        DurationField.Text = defaultDuration;
    }

    static bool TryDurationSeconds(string text, out double seconds)
    {
        var parts = text.Split(':');
        if (parts.Length == 1)
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        seconds = 0;
        if (parts.Length > 4) return false;
        Span<int> fields = stackalloc int[4];
        fields.Clear();
        var offset = parts.Length == 2 ? 1 : 0;
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out fields[offset + i])) return false;
        if ((offset == 0 && fields[1] >= 60) || fields[2] >= 60 || fields[3] >= 1000) return false;
        seconds = fields[0] * 3600d + fields[1] * 60d + fields[2] + fields[3] / 1000d;
        return true;
    }

    void SetDimensions(int width, int height)
    {
        updatingDimensions = true;
        WidthField.Text = width.ToString(CultureInfo.InvariantCulture);
        HeightField.Text = height.ToString(CultureInfo.InvariantCulture);
        updatingDimensions = false;
        MatchDimensions();
    }

    void MatchDimensions()
    {
        if (updatingDimensions) return;
        int.TryParse(WidthField.Text, out var width);
        int.TryParse(HeightField.Text, out var height);
        updatingDimensions = true;
        Resolution.SelectedItem = Resolutions.Skip(1).FirstOrDefault(p => p.Width == width && p.Height == height) ?? Resolutions[0];
        Aspect.SelectedItem = Ratios.Skip(1).FirstOrDefault(p => width > 0 && height > 0 && (long)width * p.Height == (long)height * p.Width) ?? Ratios[0];
        updatingDimensions = false;
    }

    void ApplyAspect()
    {
        if (updatingDimensions || Aspect.SelectedItem is not SizeOption { Width: > 0 } ratio) return;
        int.TryParse(WidthField.Text, out var width);
        int.TryParse(HeightField.Text, out var height);
        var longEdge = Math.Max(width, height);
        if (longEdge < 2) longEdge = Math.Max(composition.Width, composition.Height);
        var maxUnits = Math.Min(7680 / ratio.Width, 4320 / ratio.Height);
        var units = (int)Math.Round((double)longEdge / (2 * Math.Max(ratio.Width, ratio.Height))) * 2;
        units = Math.Clamp(units, 2, maxUnits - maxUnits % 2);
        SetDimensions(ratio.Width * units, ratio.Height * units);
    }

    void UpdateSwatch()
    {
        try { BackgroundSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BackgroundField.Text)); }
        catch (FormatException) { BackgroundSwatch.ClearValue(System.Windows.Controls.Border.BackgroundProperty); }
    }

    void Apply(object sender, RoutedEventArgs e)
    {
        Error.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(NameField.Text)) { ShowError("Enter a composition name.", NameField); return; }
        if (!int.TryParse(WidthField.Text, out var width) || width is < 2 or > 7680) { ShowError("Width must be between 2 and 7680 pixels.", WidthField); return; }
        if (!int.TryParse(HeightField.Text, out var height) || height is < 2 or > 4320) { ShowError("Height must be between 2 and 4320 pixels.", HeightField); return; }
        var rate = ((RateOption)FrameRates.SelectedItem).Rate;
        var duration = composition.Duration;
        var durationText = DurationField.Text.Trim();
        if (durationText != defaultDuration)
        {
            if (!TryDurationSeconds(durationText, out var seconds) || !double.IsFinite(seconds) || seconds is <= 0 or > 86400)
            { ShowError("Enter a duration in HH:MM:SS:mmm, up to 24:00:00:000.", DurationField); return; }
            duration = Math.Max(1, rate.Frames(seconds));
        }
        if (composition.Layers.Any(layer => layer.Start >= duration)) { ShowError("A layer starts outside the new duration.", DurationField); return; }
        var background = BackgroundField.Text.Trim();
        try { _ = (Color)ColorConverter.ConvertFromString(background); }
        catch (FormatException) { ShowError("Enter a valid background color.", BackgroundField); return; }
        try { commit(NameField.Text.Trim(), width, height, rate, duration, background); }
        catch (InvalidOperationException ex) { ShowError(ex.Message, NameField); return; }
        DialogResult = true;
    }

    void ShowError(string message, UIElement field)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
        field.Focus();
    }
}
