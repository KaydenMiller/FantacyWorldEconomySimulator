using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WorldEcon.Gui.ViewModels;

namespace WorldEcon.Gui.Views;

/// <summary>
/// Seed, preset and settings file for File → New world and the tuning mode. Closes with true when Generate is pressed and
/// the settings build (an invalid settings file keeps the dialog open with the error shown).
/// </summary>
public partial class GenerationSettingsDialog : Window
{
    private static readonly FilePickerFileType JsonFiles = new("Settings JSON") { Patterns = ["*.json"] };

    public GenerationSettingsDialog() => InitializeComponent();

    public GenerationSettingsDialog(GenerationSettingsViewModel vm) : this() => DataContext = vm;

    private GenerationSettingsViewModel? Vm => DataContext as GenerationSettingsViewModel;

    private async void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a generation settings file",
            AllowMultiple = false,
            FileTypeFilter = [JsonFiles, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.SettingsFile = path;
    }

    private void OnClearFile(object? sender, RoutedEventArgs e) => Vm?.ClearFile();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanGenerate: true } vm) return;
        try
        {
            vm.Build();
            Close(true);
        }
        catch (InvalidOperationException)
        {
            // Error is shown by the binding; the dialog stays open.
        }
    }
}
