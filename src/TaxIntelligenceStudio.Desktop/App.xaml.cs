using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaxIntelligenceStudio.Desktop.ViewModels;
using TaxIntelligenceStudio.Desktop.Services;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Infrastructure;

namespace TaxIntelligenceStudio.Desktop;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureServices((context, services) =>
            {
                var dataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TaxIntelligenceStudio");
                Directory.CreateDirectory(dataDirectory);

                services.AddTaxIntelligenceInfrastructure(
                    Path.Combine(dataDirectory, "tax-intelligence.db"),
                    context.Configuration);
                services.AddSingleton<IPdfOcrService, WindowsPdfOcrService>();
                services.AddTransient<IImageAttachmentAnalyzer, WindowsImageAttachmentAnalyzer>();
                services.AddTransient<IInferenceAttachmentService, TaxIntelligenceStudio.Infrastructure.Inference.InferenceAttachmentService>();
                services.AddTransient<MainViewModel>();
                services.AddTransient<MainWindow>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            (System.Windows.Media.Color)FindResource("ComfortAccentColor"),
            Wpf.Ui.Appearance.ApplicationTheme.Dark);
        base.OnStartup(e);
        await _host.StartAsync();
        await _host.Services.InitializeTaxIntelligenceDatabaseAsync();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
