using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TaxIntelligenceStudio.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace TaxIntelligenceStudio.Desktop;

public partial class MainWindow : FluentWindow
{
    private const double CompactLayoutWidth = 900;
    private const double DesignHeight = 820;
    private const double MinimumScale = 0.8;
    private bool? _isCompactLayout;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        FitToWorkArea();
        SizeChanged += MainWindow_OnSizeChanged;
        PreviewMouseWheel += MainWindow_OnPreviewMouseWheel;
        FinancialScroll.SizeChanged += (_, e) => FillViewport(FinancialLayout, e.NewSize.Height);
        AnalysisScroll.SizeChanged += (_, e) => FillViewport(AnalysisLayout, e.NewSize.Height);
        ApplyResponsiveLayout(Width < CompactLayoutWidth);
        ApplyScale(Height);
    }

    private void FitToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        if (Width > workArea.Width)
        {
            Width = workArea.Width;
        }

        if (Height > workArea.Height)
        {
            Height = workArea.Height;
        }

        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width < CompactLayoutWidth);
        ApplyScale(e.NewSize.Height);
    }

    // Shrinks all tab content proportionally on short screens so more fits before scrolling is needed.
    private void ApplyScale(double windowHeight)
    {
        var scale = Math.Clamp(windowHeight / DesignHeight, MinimumScale, 1.0);
        AppScale.ScaleX = scale;
        AppScale.ScaleY = scale;
    }

    // Grids with star rows must be given a finite height inside a ScrollViewer; otherwise
    // their DataGrids grow unbounded. They fill the viewport but never shrink below MinHeight.
    private static void FillViewport(FrameworkElement layout, double viewportHeight)
    {
        var verticalMargin = layout.Margin.Top + layout.Margin.Bottom;
        layout.Height = Math.Max(layout.MinHeight, viewportHeight - verticalMargin);
    }

    // DataGrids and text boxes swallow wheel events even when they cannot scroll further,
    // which would block the tab's page-level scrolling. Forward the wheel to the nearest
    // ancestor ScrollViewer that can still move in the requested direction.
    private void MainWindow_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var scrollingDown = e.Delta < 0;
        var isInnermost = true;
        for (var node = source; node is not null; node = GetParent(node))
        {
            if (node is not ScrollViewer viewer)
            {
                continue;
            }

            var canScroll = scrollingDown
                ? viewer.VerticalOffset < viewer.ScrollableHeight
                : viewer.VerticalOffset > 0;
            if (canScroll)
            {
                if (!isInnermost)
                {
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta);
                    e.Handled = true;
                }

                return;
            }

            isInnermost = false;
        }
    }

    private static DependencyObject? GetParent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private void ApplyResponsiveLayout(bool useCompactLayout)
    {
        if (_isCompactLayout == useCompactLayout)
        {
            return;
        }

        _isCompactLayout = useCompactLayout;
        AppTabs.Margin = useCompactLayout
            ? new Thickness(8, 6, 8, 8)
            : new Thickness(24, 12, 24, 16);

        ConfigureColumns(
            useCompactLayout,
            KnowledgePrimaryColumn,
            KnowledgeColumnGap,
            KnowledgeSecondaryColumn,
            new GridLength(2, GridUnitType.Star),
            new GridLength(3, GridUnitType.Star));
        KnowledgeRowGap.Height = new GridLength(useCompactLayout ? 16 : 0);
        PositionSecondaryPanel(KnowledgeStatusCard, useCompactLayout, 0, 2);

        ConfigureColumns(
            useCompactLayout,
            TrainerPrimaryColumn,
            TrainerColumnGap,
            TrainerSecondaryColumn,
            new GridLength(1, GridUnitType.Star),
            new GridLength(1, GridUnitType.Star));
        ConfigureStackedSection(TrainerHeader, AcceleratorsGrid, TrainerRunPanel, useCompactLayout);

        ConfigureColumns(
            useCompactLayout,
            AgentPrimaryColumn,
            AgentColumnGap,
            AgentSecondaryColumn,
            new GridLength(1, GridUnitType.Star),
            new GridLength(1, GridUnitType.Star));
        ConfigureStackedSection(AgentHeader, AgentModelsGrid, AgentTestPanel, useCompactLayout);

        ConfigureColumns(
            useCompactLayout,
            ReportsPrimaryColumn,
            ReportsColumnGap,
            ReportsSecondaryColumn,
            new GridLength(2, GridUnitType.Star),
            new GridLength(3, GridUnitType.Star));
        ReportsRowGap.Height = new GridLength(useCompactLayout ? 16 : 0);
        PositionSecondaryPanel(ReportsStatusCard, useCompactLayout, 0, 2);
    }

    private static void ConfigureColumns(
        bool useCompactLayout,
        ColumnDefinition primary,
        ColumnDefinition gap,
        ColumnDefinition secondary,
        GridLength widePrimaryWidth,
        GridLength wideSecondaryWidth)
    {
        primary.Width = useCompactLayout
            ? new GridLength(1, GridUnitType.Star)
            : widePrimaryWidth;
        gap.Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(24);
        secondary.Width = useCompactLayout
            ? new GridLength(0)
            : wideSecondaryWidth;
    }

    private static void ConfigureStackedSection(
        FrameworkElement header,
        FrameworkElement dataGrid,
        FrameworkElement secondaryPanel,
        bool useCompactLayout)
    {
        Grid.SetColumnSpan(header, useCompactLayout ? 1 : 3);
        Grid.SetColumnSpan(dataGrid, useCompactLayout ? 1 : 3);
        PositionSecondaryPanel(secondaryPanel, useCompactLayout, 2, 3);
        secondaryPanel.Margin = useCompactLayout
            ? new Thickness(0, 20, 0, 0)
            : new Thickness(0);
    }

    private static void PositionSecondaryPanel(
        FrameworkElement panel,
        bool useCompactLayout,
        int wideRow,
        int compactRow)
    {
        Grid.SetRow(panel, useCompactLayout ? compactRow : wideRow);
        Grid.SetColumn(panel, useCompactLayout ? 0 : 2);
    }
}
