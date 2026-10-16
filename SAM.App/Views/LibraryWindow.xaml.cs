using Microsoft.UI.Xaml;
using SAM.App.Services;

namespace SAM.App.Views;

/// <summary>The game library (picker). TODO(library-ui): placeholder.</summary>
public sealed partial class LibraryWindow : Window
{
    public LibraryWindow(AppServices services)
    {
        this.InitializeComponent();
        this.Title = "Steam Achievement Manager";
    }
}
