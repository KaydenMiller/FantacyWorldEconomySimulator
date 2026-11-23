using Avalonia;

namespace WorldEcon.Gui;

/// <summary>
/// Desktop app entry point. Usage:
/// <c>WorldEcon.Gui [&lt;db&gt;] [--centre &lt;lat&gt;,&lt;lon&gt;] [--zoom-to globe|region|detail] [--layer &lt;name&gt;]
/// [--screenshot &lt;png&gt;] [--exit-after &lt;seconds&gt;] [--spin &lt;frames&gt;]</c>.
/// <c>--centre</c> turns the map to a point after load; <c>--zoom-to</c> sets the scale where that view is shown (Region
/// View: region cells 45 px; Detail View: tiles 26 px; the visible tile blocks are loaded before the screenshot);
/// <c>--layer</c> picks the colour layer. <c>--screenshot</c> saves a PNG of the main window once the world has loaded
/// and drawn; <c>--exit-after</c> closes the app that many seconds after start; <c>--spin</c> pans the map a little on
/// each of that many animation frames after load and prints frame timings (diagnostics for non-interactive checks).
/// </summary>
public static class Program
{
    internal static GuiOptions Options { get; private set; } = new(null, null, null, null);

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Options = GuiOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine(GuiOptions.Usage);
            return 1;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the Avalonia designer and tooling.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}

/// <summary>Command-line options of the desktop app.</summary>
internal sealed record GuiOptions(string? DbPath, string? ScreenshotPath, double? ExitAfterSeconds, int? SpinFrames)
{
    public const string Usage = "Usage: WorldEcon.Gui [<db>] [--centre <lat>,<lon>] [--zoom-to globe|region|detail] [--layer <name>] [--screenshot <png>] [--exit-after <seconds>] [--spin <frames>]";

    /// <summary>Map centre after load (degrees), or null to keep the default (the first settlement).</summary>
    public (double LatDeg, double LonDeg)? Centre { get; init; }

    /// <summary>View to zoom to after load, or null for Globe View.</summary>
    public WorldEcon.Rendering.MapLevel? ZoomTo { get; init; }

    /// <summary>Colour layer after load, or null for the default (Biome).</summary>
    public WorldEcon.Rendering.GlobeLayer? Layer { get; init; }

    public static GuiOptions Parse(IReadOnlyList<string> args)
    {
        var o = new GuiOptions(null, null, null, null);
        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"Option {args[i]} needs a value.");
            switch (args[i])
            {
                case "--screenshot":
                    o = o with { ScreenshotPath = Value() };
                    break;
                case "--exit-after":
                    var v = Value();
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) || s <= 0)
                        throw new ArgumentException($"--exit-after expects a positive number of seconds, not '{v}'.");
                    o = o with { ExitAfterSeconds = s };
                    break;
                case "--spin":
                    var f = Value();
                    if (!int.TryParse(f, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var frames) || frames <= 0)
                        throw new ArgumentException($"--spin expects a positive number of frames, not '{f}'.");
                    o = o with { SpinFrames = frames };
                    break;
                case "--centre":
                case "--center":
                    var c = Value();
                    var parts = c.Split(',');
                    if (parts.Length != 2
                        || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) || lat is < -90 or > 90
                        || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon) || lon is < -180 or > 180)
                        throw new ArgumentException($"--centre expects <lat>,<lon> in degrees (lat -90..90, lon -180..180), not '{c}'.");
                    o = o with { Centre = (lat, lon) };
                    break;
                case "--zoom-to":
                    var z = Value();
                    if (!Enum.TryParse<WorldEcon.Rendering.MapLevel>(z, ignoreCase: true, out var level) || !Enum.IsDefined(level))
                        throw new ArgumentException($"--zoom-to expects globe, region or detail, not '{z}'.");
                    o = o with { ZoomTo = level };
                    break;
                case "--layer":
                    var l = Value();
                    if (!Enum.TryParse<WorldEcon.Rendering.GlobeLayer>(l, ignoreCase: true, out var layer) || !Enum.IsDefined(layer))
                        throw new ArgumentException($"--layer expects one of {string.Join(", ", Enum.GetNames<WorldEcon.Rendering.GlobeLayer>())}, not '{l}'.");
                    o = o with { Layer = layer };
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option {args[i]}.");
                    if (o.DbPath is not null) throw new ArgumentException("Only one database path may be given.");
                    o = o with { DbPath = args[i] };
                    break;
            }
        }

        return o;
    }
}
