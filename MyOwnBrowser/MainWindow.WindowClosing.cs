using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MyOwnBrowser;

public sealed partial class MainWindow
{
    private bool _closeApproved, _closePromptOpen;

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved || !Preferences.ConfirmCloseMultipleTabs || _tabs.Count <= 1) return;
        args.Cancel = true;
        if (_closePromptOpen) return;
        _closePromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                Title = $"¿Cerrar {_tabs.Count} pestañas?", Content = "Se cerrará esta ventana. Las pestañas privadas no se guardan.",
                PrimaryButtonText = "Cerrar ventana", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) { _closeApproved = true; Close(); }
        }
        finally { _closePromptOpen = false; }
    }
}
