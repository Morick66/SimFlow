using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimFlow.Models;
using SimFlow.ViewModels;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;
// Path 在 Microsoft.UI.Xaml.Shapes 与 System.IO（隐式 using）里重名，这里显式取图形那个。
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace SimFlow.Views;

/// <summary>
/// 统计分析页（UserControl，由 MainWindow 在「统计分析」分区里显示）。
///
/// 图表全部自绘，不引第三方图表库：
/// <list type="bullet">
/// <item>环形图：每片一条 <see cref="XamlPath"/>，用 <c>ArcSegment</c> 描边（描边宽度就是环厚），
/// 单一类型占 100% 时改用整圆 <see cref="Ellipse"/>（起止点重合的圆弧画不出来）。</item>
/// <item>柱状图：ItemsControl + 视图模型预先算好的像素高度，鼠标悬浮用 ToolTip 显示数值。</item>
/// </list>
/// 这里只做排版与绘制，统计口径在 Core 的 StatisticsService。
/// </summary>
public sealed partial class StatisticsPage : UserControl
{
    private const double DonutCenter = 90;
    private const double DonutRadius = 72;
    private const double DonutThickness = 26;

    /// <summary>接近整圈的扇形用整圆绘制。</summary>
    private const double FullCircleSweep = 359.9;

    /// <summary>已绘制的环形图扇形（重绘时只移除这些，保留 XAML 里的底圈）。</summary>
    private readonly List<Shape> _donutSlices = [];

    private FrameworkElement[]? _cards;
    private bool _syncingRange;

    public StatisticsViewModel ViewModel { get; }

    /// <summary>
    /// 统计卡片上的“查看全部 →”。参数是目标筛选键（当前只有“已完成项目列表”这个真实目的地）；
    /// 由 MainWindow 接住并切到「所有项目」页，避免统计页直接操作壳层导航。
    /// </summary>
    public event EventHandler<string>? ViewAllRequested;

    public StatisticsPage()
    {
        // 页面由 XAML 直接实例化，拿不到构造函数注入；这里从应用容器取单例视图模型
        // （单例是为了在导航来回切换时保留用户选中的时间范围）。
        ViewModel = App.Services.GetRequiredService<StatisticsViewModel>();
        InitializeComponent();
    }

    private void ViewAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && key.Length > 0)
        {
            ViewAllRequested?.Invoke(this, key);
        }
    }

    /// <summary>重新读取项目并重算全部统计，然后重绘环形图。异常在这里收口，调用方可以直接 fire-and-forget。</summary>
    public async Task RefreshAsync()
    {
        SyncRangeButtons();
        try
        {
            await ViewModel.RefreshAsync();
            ViewModel.StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"统计加载失败：{ex.Message}";
        }

        RenderDonut();
        ArrangeCards(ActualWidth);
        ArrangeHeader(ActualWidth);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ArrangeCards(e.NewSize.Width);

    /// <summary>
    /// 页头在窄宽度下把“时间范围 + 时间窗口”整块挪到第二行：否则右侧固定宽度的控件会把
    /// 左边的标题/口径说明挤成一条很窄的竖排文字。
    /// </summary>
    private void ArrangeHeader(double width)
    {
        if (HeaderGrid is null || RangePanel is null)
        {
            return;
        }

        var stacked = width > 0 && width < 780;
        if (stacked)
        {
            Grid.SetRow(RangePanel, 1);
            Grid.SetColumn(RangePanel, 0);
            Grid.SetColumnSpan(RangePanel, 2);
            RangePanel.HorizontalAlignment = HorizontalAlignment.Left;
            RangePanel.Margin = new Thickness(0, 10, 0, 0);
        }
        else
        {
            Grid.SetRow(RangePanel, 0);
            Grid.SetColumn(RangePanel, 1);
            Grid.SetColumnSpan(RangePanel, 1);
            RangePanel.HorizontalAlignment = HorizontalAlignment.Right;
            RangePanel.Margin = new Thickness(0);
        }
    }

    private void SyncRangeButtons()
    {
        _syncingRange = true;
        try
        {
            RangeThisMonth.IsChecked = ViewModel.Range == StatisticsRange.CurrentMonth;
            RangeLastThreeMonths.IsChecked = ViewModel.Range == StatisticsRange.LastThreeMonths;
            RangeThisYear.IsChecked = ViewModel.Range == StatisticsRange.CurrentYear;
            RangeAll.IsChecked = ViewModel.Range == StatisticsRange.All;
        }
        finally
        {
            _syncingRange = false;
        }
    }

    private async void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingRange || sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        if (!Enum.TryParse<StatisticsRange>(tag, out var range))
        {
            return;
        }

        ViewModel.Range = range;
        await RefreshAsync();
    }

    /// <summary>
    /// 把 7 张卡片按可用宽度重排成 3 / 2 / 1 列。列定义与位置全部在这里算，
    /// 不依赖固定像素宽度，因此窗口缩放时不会出现内容裁剪或横向溢出。
    /// </summary>
    private void ArrangeCards(double width)
    {
        if (double.IsNaN(width) || width <= 0)
        {
            // 还没完成首次布局：先按三列摆好，等 SizeChanged 再修正。
            width = 1240;
        }

        var columns = width >= 1240 ? 3 : width >= 860 ? 2 : 1;
        var span = 6 / columns;
        var cards = Cards;

        CardsGrid.ColumnDefinitions.Clear();
        for (var index = 0; index < 6; index++)
        {
            CardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        CardsGrid.RowDefinitions.Clear();
        var rows = ((cards.Length + columns - 1) / columns);
        for (var index = 0; index < rows; index++)
        {
            CardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // 最后一行单独处理：只剩一张时铺满整行；剩两张（三列布局）时按 2:1 分栏，
        // 对齐参考图（左宽右窄）。注意要按“这一行有几张卡”判断，不能用“后面还剩几张”。
        for (var index = 0; index < cards.Length; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var cardsInRow = Math.Min(columns, cards.Length - (row * columns));
            var isLastRow = row == rows - 1;
            var startColumn = column * span;
            var columnSpan = span;
            if (isLastRow && cardsInRow == 1)
            {
                startColumn = 0;
                columnSpan = 6;
            }
            else if (isLastRow && cardsInRow == 2 && columns == 3)
            {
                startColumn = column == 0 ? 0 : 4;
                columnSpan = column == 0 ? 4 : 2;
            }

            Grid.SetRow(cards[index], row);
            Grid.SetColumn(cards[index], startColumn);
            Grid.SetColumnSpan(cards[index], columnSpan);
        }

        UpdateKpiRow(width);
        ArrangeHeader(width);
    }

    private FrameworkElement[] Cards => _cards ??=
    [
        SimulationTypeCard, TrendCard, SoftwareCard, RequesterCard, CycleCard, WaitCard, RecentCompletedCard, TagCard
    ];

    private void UpdateKpiRow(double width)
    {
        if (KpiRow.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            return;
        }

        var columns = width >= 1120 ? 5 : width >= 900 ? 3 : width >= 560 ? 2 : 1;
        panel.ItemWidth = Math.Max(160, Math.Floor((width - (columns * 12d)) / columns));
        panel.ItemHeight = 96;
    }

    private void RenderDonut()
    {
        foreach (var slice in _donutSlices)
        {
            SimulationTypeCanvas.Children.Remove(slice);
        }

        _donutSlices.Clear();
        foreach (var item in ViewModel.TypeSlices)
        {
            if (item.Count <= 0 || item.SweepAngle <= 0)
            {
                continue;
            }

            var shape = CreateSliceShape(item);
            SimulationTypeCanvas.Children.Add(shape);
            _donutSlices.Add(shape);
        }
    }

    private static Shape CreateSliceShape(SimulationTypeSliceViewModel item)
    {
        var brush = new SolidColorBrush(ParseColor(item.Color));
        if (item.SweepAngle >= FullCircleSweep)
        {
            var circle = new Ellipse
            {
                Width = DonutRadius * 2,
                Height = DonutRadius * 2,
                Stroke = brush,
                StrokeThickness = DonutThickness,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(circle, DonutCenter - DonutRadius);
            Canvas.SetTop(circle, DonutCenter - DonutRadius);
            return circle;
        }

        var figure = new PathFigure
        {
            StartPoint = PointOnCircle(item.StartAngle),
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointOnCircle(item.StartAngle + item.SweepAngle),
            Size = new Size(DonutRadius, DonutRadius),
            IsLargeArc = item.SweepAngle > 180,
            SweepDirection = SweepDirection.Clockwise
        });
        return new XamlPath
        {
            Data = new PathGeometry { Figures = { figure } },
            Stroke = brush,
            StrokeThickness = DonutThickness,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            IsHitTestVisible = false
        };
    }

    /// <summary>角度以 12 点方向为 0°、顺时针增大（视图模型给的起始角是 -90°）。</summary>
    private static Point PointOnCircle(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(
            DonutCenter + (DonutRadius * Math.Cos(radians)),
            DonutCenter + (DonutRadius * Math.Sin(radians)));
    }

    private static Color ParseColor(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 6)
        {
            text = "FF" + text;
        }

        return text.Length == 8 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : Color.FromArgb(0, 0, 0, 0);
    }
}
