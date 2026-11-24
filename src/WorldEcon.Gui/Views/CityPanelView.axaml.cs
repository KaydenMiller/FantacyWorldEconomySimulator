using Avalonia.Controls;
using Avalonia.Interactivity;
using WorldEcon.Gui.ViewModels;

namespace WorldEcon.Gui.Views;

/// <summary>The city panel (docked in the City tab, or the content of a pop-out window); see <see cref="CityPanelViewModel"/>.</summary>
public partial class CityPanelView : UserControl
{
    public CityPanelView() => InitializeComponent();

    private CityPanelViewModel? Vm => DataContext as CityPanelViewModel;

    private void OnLine(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TableLine { Open: { } open }) open();
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Vm?.RefreshAll();

    private void OnPopOut(object? sender, RoutedEventArgs e) => Vm?.RequestPopOut();

    private void OnSearchChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: SettlementChoice choice } list && Vm is { } vm)
        {
            list.SelectedItem = null;
            _ = vm.ChooseAsync(choice);
        }
    }
}
