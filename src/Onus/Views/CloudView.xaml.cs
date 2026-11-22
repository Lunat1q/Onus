using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Onus.ViewModels;

namespace Onus.Views;

public partial class CloudView : UserControl
{
    public CloudView() => InitializeComponent();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is CloudClip c && DataContext is CloudViewModel vm)
            vm.PlayCommand.Execute(c);
    }
}
