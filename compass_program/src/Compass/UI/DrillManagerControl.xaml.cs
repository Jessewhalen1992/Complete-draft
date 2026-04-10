using System.Windows.Controls;
using Compass.ViewModels;

namespace Compass.UI;

public partial class DrillManagerControl : UserControl
{
    public DrillManagerControl()
    {
        InitializeComponent();
        DataContext = new DrillManagerViewModel();
    }

    public DrillManagerViewModel ViewModel => (DrillManagerViewModel)DataContext;

    public DrillPropsAccessor DrillProps => ViewModel.DrillProps;
}
