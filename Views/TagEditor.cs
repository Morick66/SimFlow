using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace SimFlow.Views;

/// <summary>
/// chip 形式的标签编辑器：输入回车添加、点 × 删除、点“已有标签”快速加入。
/// 界面本身不使用逗号分隔；只有在粘贴 “短耐, 150kA” 这类内容时才按逗号自动拆开。
/// </summary>
internal sealed class TagEditor
{
    private static readonly Color ChipBackground = Color.FromArgb(255, 226, 236, 253);
    private static readonly Color ChipForeground = Color.FromArgb(255, 18, 74, 176);
    private static readonly Color SuggestionBackground = Color.FromArgb(255, 241, 244, 249);
    private static readonly Color MutedForeground = Color.FromArgb(255, 104, 119, 146);
    private static readonly Color SubtleForeground = Color.FromArgb(255, 128, 140, 160);
    private static readonly Color SurfaceBackground = Color.FromArgb(255, 247, 249, 252);
    private static readonly Color SurfaceBorder = Color.FromArgb(255, 223, 230, 241);
    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);

    private readonly List<string> _tags = [];
    private readonly StackPanel _chipPanel = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _suggestionPanel = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBox _input;
    private readonly TextBlock _emptyHint;
    private readonly IReadOnlyList<string> _suggestions;

    public TagEditor(IEnumerable<string> suggestions, IEnumerable<string> initial)
    {
        _suggestions = suggestions
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var tag in initial)
        {
            var value = Normalize(tag);
            if (value.Length > 0 && !_tags.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                _tags.Add(value);
            }
        }

        _emptyHint = new TextBlock
        {
            Text = "还没有标签，输入后按回车添加",
            FontSize = 13,
            Foreground = new SolidColorBrush(SubtleForeground),
            VerticalAlignment = VerticalAlignment.Center
        };

        _input = new TextBox { PlaceholderText = "输入标签后按回车添加", Margin = new Thickness(0, 8, 0, 0) };
        _input.KeyDown += OnInputKeyDown;

        var chipArea = new Border
        {
            Background = new SolidColorBrush(SurfaceBackground),
            BorderBrush = new SolidColorBrush(SurfaceBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 8, 9, 8),
            MinHeight = 42,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = _chipPanel
            }
        };

        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "标签", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        root.Children.Add(chipArea);
        root.Children.Add(_input);
        if (_suggestions.Count > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "已有标签（点击加入）",
                FontSize = 12,
                Margin = new Thickness(0, 9, 0, 0),
                Foreground = new SolidColorBrush(MutedForeground)
            });
            root.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Margin = new Thickness(0, 6, 0, 0),
                Content = _suggestionPanel
            });
        }

        Root = root;
        Refresh();
    }

    public FrameworkElement Root { get; }

    public IReadOnlyList<string> Tags => _tags;

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        var parts = _input.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            AddTag(_input.Text);
        }
        else
        {
            foreach (var part in parts)
            {
                AddTag(part);
            }
        }

        _input.Text = string.Empty;
    }

    private void AddTag(string value)
    {
        var tag = Normalize(value);
        if (tag.Length == 0 || _tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _tags.Add(tag);
        Refresh();
    }

    private void Refresh()
    {
        _chipPanel.Children.Clear();
        if (_tags.Count == 0)
        {
            _chipPanel.Children.Add(_emptyHint);
        }
        else
        {
            foreach (var tag in _tags)
            {
                _chipPanel.Children.Add(CreateChip(tag, removable: true));
            }
        }

        _suggestionPanel.Children.Clear();
        foreach (var suggestion in _suggestions.Where(item => !_tags.Contains(item, StringComparer.OrdinalIgnoreCase)))
        {
            _suggestionPanel.Children.Add(CreateChip(suggestion, removable: false));
        }
    }

    private Border CreateChip(string tag, bool removable)
    {
        var label = new TextBlock
        {
            Text = removable ? $"#{tag}" : tag,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(label);

        var chip = new Border
        {
            Background = new SolidColorBrush(removable ? ChipBackground : SuggestionBackground),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 5, 10, 5),
            Child = content
        };

        if (removable)
        {
            label.Foreground = new SolidColorBrush(ChipForeground);
            var close = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
                Padding = new Thickness(3),
                MinWidth = 0,
                MinHeight = 0,
                Background = new SolidColorBrush(Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            close.Click += (_, _) =>
            {
                _tags.RemoveAll(item => string.Equals(item, tag, StringComparison.OrdinalIgnoreCase));
                Refresh();
            };
            content.Children.Add(close);
        }
        else
        {
            chip.Tapped += (_, _) => AddTag(tag);
        }

        return chip;
    }

    private static string Normalize(string value) => (value ?? string.Empty).Trim().TrimStart('#').Trim();
}
