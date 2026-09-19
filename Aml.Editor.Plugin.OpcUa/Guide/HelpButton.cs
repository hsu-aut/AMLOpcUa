// The "?" next to a part of the plugin: a popup with what the part does, its
// terms to click open, and the lesson that walks through it. The popup closes
// when the user clicks elsewhere; it never blocks the plugin.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Aml.Editor.Plugin.OpcUa.Guide;

public sealed class HelpButton : Button
{
    public static readonly DependencyProperty TopicProperty =
        DependencyProperty.Register(nameof(Topic), typeof(string), typeof(HelpButton), new PropertyMetadata(""));

    /// <summary>The key of the topic in <see cref="HelpTopics.All"/>.</summary>
    public string Topic
    {
        get => (string)GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    /// <summary>Raised on the plugin when a popup's lesson link is clicked; the argument is the lesson's id.</summary>
    internal static event Action<HelpButton, string>? LessonRequested;

    /// <summary>Raised whenever a popup opens; the argument is the topic.</summary>
    internal static event Action<HelpButton, string>? Opened;

    public HelpButton()
    {
        Content = new TextBlock { Text = "?", FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        Width = 20;
        Height = 20;
        Padding = new Thickness(0);
        Margin = new Thickness(6, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = System.Windows.Input.Cursors.Help;
        // A small ring in the theme's colours, not a grey button: it sits in bands and toolbars alike.
        SetResourceReference(ForegroundProperty, "Exchange");
        SetResourceReference(BorderBrushProperty, "Exchange");
        Background = Brushes.Transparent;
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(1.2));
        ring.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        ring.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        ring.AppendChild(content);
        Template = new ControlTemplate(typeof(Button)) { VisualTree = ring };
        Loaded += (_, __) =>
        {
            if (!HelpTopics.All.TryGetValue(Topic, out var topic)) return;
            ToolTip = $"What is this? {topic.Title}";
            AutomationProperties.SetName(this, "Help: " + topic.Title);
        };
        Click += (_, __) => Show();
    }

    private void Show()
    {
        if (!HelpTopics.All.TryGetValue(Topic, out var topic)) return;
        var palette = ThemePalette.Current(this);
        var body = new StackPanel { MaxWidth = 440 };
        body.Children.Add(new TextBlock { Text = topic.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(new TextBlock { Text = topic.Text, TextWrapping = TextWrapping.Wrap });

        // Terms: a click shows the explanation below, a second click hides it.
        var explanation = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = palette.Muted, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var terms = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        terms.Inlines.Add(new Run("Terms: ") { Foreground = palette.Muted });
        string? shown = null;
        foreach (var term in topic.Terms)
        {
            var link = new Hyperlink(new Run(term)) { Foreground = palette.Exchange };
            link.Click += (_, __) =>
            {
                shown = shown == term ? null : term;
                explanation.Text = shown == null ? "" : $"{term}: {HelpTopics.Glossary[term]}";
                explanation.Visibility = shown == null ? Visibility.Collapsed : Visibility.Visible;
            };
            if (terms.Inlines.Count > 1) terms.Inlines.Add(new Run(",  "));
            terms.Inlines.Add(link);
        }
        body.Children.Add(terms);
        body.Children.Add(explanation);

        var popup = new Popup { PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        if (topic.Lesson != null && Lessons.Titles.TryGetValue(topic.Lesson, out var lesson))
        {
            var start = new Hyperlink(new Run($"Show me in the tutorial: {lesson}")) { Foreground = palette.Exchange };
            start.Click += (_, __) =>
            {
                popup.IsOpen = false;
                LessonRequested?.Invoke(this, topic.Lesson);
            };
            body.Children.Add(new TextBlock(start) { Margin = new Thickness(0, 10, 0, 0) });
        }

        var frame = new Border
        {
            Child = body,
            Background = palette.Surface,
            BorderBrush = palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 0, 8, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.25 },
        };
        frame.SetValue(TextElement.ForegroundProperty, palette.Foreground);
        popup.Child = frame;
        popup.IsOpen = true;
        Opened?.Invoke(this, Topic);
    }
}
