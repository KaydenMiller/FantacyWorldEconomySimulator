using System.Globalization;
using WorldEcon.Seeding;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Gui.ViewModels;

/// <summary>
/// The generation settings dialog (File → New world, tuning mode): seed, preset and an optional settings file layered on
/// the preset (the CLI's <c>--generation</c>). Individual values are edited in the file, not here.
/// </summary>
public sealed class GenerationSettingsViewModel : ObservableObject
{
    private string _seedText = DemoSeeder.DefaultSeed.ToString(CultureInfo.InvariantCulture);
    private string _preset = WorldGenPresets.Earth5Km;
    private string? _settingsFile;
    private string? _seedError;
    private string? _fileError;
    private ulong _seed = DemoSeeder.DefaultSeed;

    public IReadOnlyList<string> Presets => WorldGenPresets.Names;

    /// <summary>The seed as typed: a whole number from 0 to 18,446,744,073,709,551,615.</summary>
    public string SeedText
    {
        get => _seedText;
        set
        {
            if (!Set(ref _seedText, value)) return;
            if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s))
            {
                _seed = s;
                _seedError = null;
            }
            else
            {
                _seedError = $"The seed must be a whole number from 0 to {ulong.MaxValue:N0}.";
            }

            RaiseState();
        }
    }

    /// <summary>The parsed seed; meaningful only while <see cref="CanGenerate"/>.</summary>
    public ulong Seed => _seed;

    public string Preset
    {
        get => _preset;
        set
        {
            if (Set(ref _preset, value)) ClearFileError();
        }
    }

    /// <summary>A settings JSON file layered on the preset; null for the preset alone.</summary>
    public string? SettingsFile
    {
        get => _settingsFile;
        set
        {
            if (Set(ref _settingsFile, value)) ClearFileError();
        }
    }

    /// <summary>The seed error, else the last settings file error, else null.</summary>
    public string? Error => _seedError ?? _fileError;

    public bool CanGenerate => _seedError is null;

    public void ClearFile() => SettingsFile = null;

    /// <summary>
    /// The preset with the settings file layered on it. A missing, unreadable or invalid file sets <see cref="Error"/> and
    /// throws <see cref="InvalidOperationException"/> with the same message.
    /// </summary>
    public WorldGenSettings Build()
    {
        try
        {
            var preset = WorldGenPresets.Get(_preset);
            var settings = _settingsFile is null ? preset : GenerationSettingsFile.Read(_settingsFile, preset);
            ClearFileError();
            return settings;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _fileError = ex.Message;
            RaiseState();
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private void ClearFileError()
    {
        if (_fileError is null) return;
        _fileError = null;
        RaiseState();
    }

    private void RaiseState()
    {
        Raise(nameof(Error));
        Raise(nameof(CanGenerate));
        Raise(nameof(Seed));
    }
}
