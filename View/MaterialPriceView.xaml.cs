using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ADLMRateGen.View
{
    /// <summary>
    /// Interaction logic for MaterialPriceView.xaml
    /// </summary>
    public partial class MaterialPriceView : UserControl
    {
        public MaterialPriceView()
        {
            InitializeComponent();
        }

        /// <summary>Close or Cancel: nothing is saved.</summary>
        private void Close_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (System.Windows.Application.Current?.MainWindow is MainWindow mw) mw.PopupHost.Hide();
        }
    }
}
