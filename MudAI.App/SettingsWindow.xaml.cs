using System.Windows;
using MudAI.App.ViewModels;

namespace MudAI.App;

/// <summary>
/// A modal dialog for set-once configuration (connection target and auto-login), kept off the
/// main window so the live play surface stays uncluttered. Binds to the shared MainViewModel,
/// so edits flow to the orchestrator the same way the main window's bindings do.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        // PasswordBox can't be data-bound (by design); sync it manually.
        PasswordBox.Password = viewModel.InitialLoginPassword;
        PasswordBox.PasswordChanged += (_, _) => viewModel.SetLoginPassword(PasswordBox.Password);
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
