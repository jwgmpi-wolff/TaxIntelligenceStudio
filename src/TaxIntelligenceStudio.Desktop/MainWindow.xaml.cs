using System.Windows;
using System.Windows.Controls;
using TaxIntelligenceStudio.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace TaxIntelligenceStudio.Desktop;

public partial class MainWindow : FluentWindow
{
    private const double CompactLayoutWidth = 900;
    private bool? _isCompactLayout;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SizeChanged += MainWindow_OnSizeChanged;
        ApplyResponsiveLayout(Width < CompactLayoutWidth);
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width < CompactLayoutWidth);
    }

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
