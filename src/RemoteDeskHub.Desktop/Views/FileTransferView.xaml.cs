using System.Windows.Controls;
using System.Windows.Input;
using RemoteDeskHub.Desktop.ViewModels;

namespace RemoteDeskHub.Desktop.Views;

public partial class FileTransferView : UserControl
{
    public FileTransferView()
    {
        InitializeComponent();
    }

    private void Files_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileTransferViewModel vm && vm.OpenEntryCommand.CanExecute(null))
        {
            vm.OpenEntryCommand.Execute(null);
        }
    }
}
