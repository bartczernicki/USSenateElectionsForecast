using USSenateElectionsForecast.Markets.Kalshi;
using USSenateElectionsForecast.Markets.Polymarket;

namespace USSenateElectionsForecast;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string dataDirectory;
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("US Senate Elections Forecast\nUsage: USSenateElectionsForecast [--data-dir <directory>]");
            return 0;
        }
        if (args.Length == 0)
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USSenateElectionsForecast");
        else if (args.Length == 2 && args[0] == "--data-dir" && !string.IsNullOrWhiteSpace(args[1]))
            dataDirectory = args[1];
        else
        {
            Console.Error.WriteLine("Usage: USSenateElectionsForecast [--data-dir <directory>]");
            return 2;
        }

        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var store = new ForecastStore(dataDirectory, Path.Combine(AppContext.BaseDirectory, "Data"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("USSenateElectionsForecast/1.0");
            var presentation = new ConsolePresentation(Console.Out, enableColor: !Console.IsOutputRedirected);
            var app = new ConsoleApp(Console.In, Console.Out, store, new PolymarketClient(http),
                new SimulationEngine(), presentation, new ProbabilityCalculationEngine(), new KalshiClient(http));
            await app.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("\nForecast cancelled.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Unable to start the forecast: {ex.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }
}
