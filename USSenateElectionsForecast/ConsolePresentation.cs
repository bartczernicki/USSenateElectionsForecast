using System.Globalization;

namespace USSenateElectionsForecast;

public sealed class ConsolePresentation
{
    private const string Cyan = "\u001b[38;2;0;229;255m";
    private const string Gold = "\u001b[38;2;255;215;0m";
    private const string Green = "\u001b[38;2;128;255;160m";
    private const string Red = "\u001b[38;2;255;128;128m";
    private const string Reset = "\u001b[39m";
    private readonly TextWriter _output;
    private readonly bool _color;
    private readonly bool _rgb;

    public ConsolePresentation(TextWriter output, bool enableColor = false)
    {
        _output = output;
        string? terminal = Environment.GetEnvironmentVariable("TERM");
        _color = enableColor && ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null
            && !string.Equals(terminal, "dumb", StringComparison.OrdinalIgnoreCase);
        string? colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        _rgb = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))
            || string.Equals(colorTerm, "truecolor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(colorTerm, "24bit", StringComparison.OrdinalIgnoreCase)
            || terminal?.Contains("direct", StringComparison.OrdinalIgnoreCase) == true;
    }

    public void Banner()
    {
        // Three stacked words keep the title readable in an 80-column terminal.
        Colored("""
            _   _ ____    ____  _____ _   _    _  _____ _____
           | | | / ___|  / ___|| ____| \ | |  / \|_   _| ____|
           | | | \___ \  \___ \|  _| |  \| | / _ \ | | |  _|
           | |_| |___) |  ___) | |___| |\  |/ ___ \| | | |___
            \___/|____/  |____/|_____|_| \_/_/   \_\_| |_____|

            _____ _     _____ ____ _____ ___ ___  _   _ ____
           | ____| |   | ____/ ___|_   _|_ _/ _ \| \ | / ___|
           |  _| | |   |  _|| |     | |  | | | | |  \| \___ \
           | |___| |___| |__| |___  | |  | | |_| | |\  |___) |
           |_____|_____|_____\____| |_| |___\___/|_| \_|____/

            _____ ___  ____  _____ ____    _    ____ _____
           |  ___/ _ \|  _ \| ____/ ___|  / \  / ___|_   _|
           | |_ | | | | |_) |  _|| |     / _ \ \___ \ | |
           |  _|| |_| |  _ <| |__| |___ / ___ \ ___) || |
           |_|   \___/|_| \_\_____\____/_/   \_\____/ |_|
           """, Cyan);
        _output.WriteLine();
        Colored("US Senate Elections Forecast", Cyan);
        _output.WriteLine();
    }

    public void Menu(string? title, IReadOnlyList<string> items, string zeroLabel)
    {
        _output.WriteLine();
        if (title is not null) Line(title, Gold);
        for (int index = 0; index < items.Count; index++) Line($"{index + 1}) {items[index]}", Gold);
        Line($"0) {zeroLabel}", Gold);
    }

    public void Context(AppSettings settings, PredictionSnapshot? snapshot)
    {
        _output.WriteLine();
        _output.WriteLine($"Simulation count: {settings.TrialCount.ToString("N0", CultureInfo.InvariantCulture)} | Rho: {settings.Rho.ToString("0.00############", CultureInfo.InvariantCulture)}");
        if (snapshot is null)
        {
            Warning("Predictions unavailable. Use Configure > Update Predictions from Polymarket before simulating.");
            return;
        }
        _output.WriteLine($"Election: {snapshot.ElectionYear} | Races: {snapshot.Races.Count} | Predictions retrieved: {Timestamp(snapshot.RetrievedAt)}");
        _output.WriteLine("Seats not on the ballot: Democratic caucus 34 | Republican caucus 31");
        Prose("Control: Democratic caucus needs 51 seats; Republican caucus needs 50.");
        Prose("The Vice President breaks a 50-50 tie for the Republican caucus.");
        if (DateTimeOffset.UtcNow - snapshot.RetrievedAt > TimeSpan.FromDays(1))
            Warning("These saved predictions are over 24 hours old. Update Predictions from Polymarket to refresh them.");
    }

    public void Results(SimulationResult result)
    {
        _output.WriteLine();
        Line($"{result.Mode} Monte Carlo Simulation", Cyan);
        _output.WriteLine($"Trials: {result.TrialCount.ToString("N0", CultureInfo.InvariantCulture)} | Predictions retrieved: {Timestamp(result.SnapshotAt)}");
        if (result.Mode == SimulationMode.Correlated)
        {
            _output.WriteLine($"Rho: {result.Rho.ToString("0.00############", CultureInfo.InvariantCulture)} (shared latent Gaussian correlation)");
            Prose("A shared national factor moves races together while preserving each race's win probabilities.");
        }
        else
        {
            Prose("Each race is sampled independently using its individual win probabilities.");
        }
        _output.WriteLine();
        Prose("Final Senate seats: 100 total. Independents count with their known caucus.");
        Summary("Democratic caucus", result.DemocraticSeats);
        Summary("Republican caucus", result.RepublicanSeats);
        Summary("Unknown / unaffiliated", result.UnknownSeats);
        _output.WriteLine();
        Line("Senate Control Probability:", Cyan);
        Bar("Democratic caucus", (double)result.DemocraticControlTrials / result.TrialCount, Cyan);
        Bar("Republican caucus", (double)result.RepublicanControlTrials / result.TrialCount, Cyan);
        Bar("Uncertain control", (double)result.UncertainControlTrials / result.TrialCount, Cyan);
        Prose("The Vice President's tie-breaking vote favors the Republican caucus at 50-50.");
        Prose("Control is uncertain when unaffiliated winners could determine the majority.");
        Distribution("Democratic Caucus Seat Distribution:", result.DemocraticSeats, result.TrialCount, Cyan);
        Distribution("Republican Caucus Seat Distribution:", result.RepublicanSeats, result.TrialCount, Cyan);
        DemocraticSeatProbabilities(result);
    }

    public void Warning(string message) => Line(message, Gold);
    public void Error(string message) => Line($"Error: {message}", Red);
    public void Success(string message) => Line(message, Green);

    private void Summary(string label, SeatDistribution distribution) =>
        _output.WriteLine($"  {label,-23} Mean: {distribution.Mean.ToString("0.00", CultureInfo.InvariantCulture),6} | 90% interval: {distribution.Percentile(0.05)}–{distribution.Percentile(0.95)} seats");

    private void Distribution(string heading, SeatDistribution distribution, int trialCount, string color)
    {
        _output.WriteLine();
        Line(heading, Cyan);
        for (int seats = 0; seats < distribution.Counts.Length; seats++)
            if (distribution.Counts[seats] > 0) Bar($"{seats} seats", (double)distribution.Counts[seats] / trialCount, color);
    }

    private void DemocraticSeatProbabilities(SimulationResult result)
    {
        _output.WriteLine();
        Line("Democratic seat probabilities (at least):", Cyan);
        for (int threshold = 50; threshold <= 60; threshold++)
        {
            int qualifyingTrials = result.DemocraticSeats.Counts.Skip(threshold).Sum();
            double probability = (double)qualifyingTrials / result.TrialCount;
            _output.WriteLine($"  Probability of >={threshold} Dem Seats: {probability.ToString("P2", CultureInfo.InvariantCulture)}");
        }
    }

    private void Bar(string label, double probability, string color)
    {
        const int width = 40;
        const string partials = "▏▎▍▌▋▊▉";
        int units = (int)Math.Round(probability * width * 8, MidpointRounding.AwayFromZero);
        string cells = new('█', units / 8);
        if (units % 8 is int remainder and > 0) cells += partials[remainder - 1];
        _output.Write($"  {label,-23} |");
        Colored(cells.PadRight(width), color);
        _output.WriteLine($"| {probability.ToString("P2", CultureInfo.InvariantCulture)}");
    }

    private void Line(string text, string color)
    {
        foreach (string line in WrappedLines(text))
        {
            Colored(line, color);
            _output.WriteLine();
        }
    }

    private void Prose(string text)
    {
        foreach (string line in WrappedLines(text)) _output.WriteLine(line);
    }

    private static IEnumerable<string> WrappedLines(string text)
    {
        const int width = 80;
        foreach (string paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string remaining = paragraph;
            while (remaining.Length > width)
            {
                int split = remaining.LastIndexOf(' ', width);
                if (split <= 0) split = width;
                yield return remaining[..split].TrimEnd();
                remaining = remaining[split..].TrimStart();
            }
            yield return remaining;
        }
    }

    private void Colored(string text, string color)
    {
        if (!_color)
        {
            _output.Write(text);
            return;
        }
        if (_rgb)
        {
            _output.Write(color);
            try { _output.Write(text); }
            finally { _output.Write(Reset); }
            return;
        }
        ConsoleColor original = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color switch
            {
                Cyan => ConsoleColor.Cyan,
                Gold => ConsoleColor.Yellow,
                Green => ConsoleColor.Green,
                Red => ConsoleColor.Red,
                _ => original
            };
            _output.Write(text);
        }
        finally { Console.ForegroundColor = original; }
    }

    private static string Timestamp(DateTimeOffset timestamp) => timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
