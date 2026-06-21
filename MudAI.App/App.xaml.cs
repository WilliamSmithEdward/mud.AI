using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MudAI.App.ViewModels;
using MudAI.Core.Agent;
using MudAI.Core.Ansi;
using MudAI.Core.Configuration;
using MudAI.Core.Llm;
using MudAI.Core.Memory;
using MudAI.Core.Telnet;

namespace MudAI.App;

/// <summary>
/// Application entry point. Builds a generic host, wires all Core services into DI,
/// initialises the memory store, then resolves and shows the main window.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory // find appsettings.json next to the exe
            });

            builder.Services.Configure<MudAiOptions>(
                builder.Configuration.GetSection(MudAiOptions.SectionName));

            builder.Services.AddSingleton<IAnsiParser, AnsiParser>();
            builder.Services.AddSingleton<MudOutputProcessor>();
            builder.Services.AddSingleton(sp =>
                new ScreenBuffer(sp.GetRequiredService<IOptions<MudAiOptions>>().Value.MaxRecentLines));
            builder.Services.AddSingleton<ITelnetClient, TelnetClient>();
            builder.Services.AddSingleton<IMemoryStore, SqliteMemoryStore>();
            builder.Services.AddSingleton<ICommandTracker, CommandTracker>();
            builder.Services.AddSingleton<IContextBuilder, ContextBuilder>();
            builder.Services.AddSingleton<LoginManager>();
            builder.Services.AddHttpClient<ILlmClient, LmStudioClient>();
            builder.Services.AddSingleton<AgentOrchestrator>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();
            await _host.StartAsync();

            await _host.Services.GetRequiredService<IMemoryStore>().InitializeAsync();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup failed:\n\n{ex}", "MudAI",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var host = _host;
        if (host is not null)
        {
            try
            {
                // Bounded, synchronous teardown. Run on a background thread so the awaited Core
                // code (whose continuations capture no UI context there) cannot deadlock against
                // the blocked UI thread. Disposing the host via DisposeAsync correctly disposes
                // the async-only singletons: the orchestrator stops its loop and closes the
                // socket, and the SQLite connection is closed.
                Task.Run(async () =>
                {
                    await host.StopAsync(TimeSpan.FromSeconds(3));
                    await ((IAsyncDisposable)host).DisposeAsync();
                }).GetAwaiter().GetResult();
            }
            catch { /* shutting down anyway */ }
        }

        base.OnExit(e);
    }
}
