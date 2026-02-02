using Microsoft.UI.Xaml.Controls;

namespace WinUI_App.Dialogs
{
    public sealed partial class UploadReportDialog : ContentDialog
    {
        public string Description => DescriptionTextBox.Text;
        public bool Targeted => TargetedCheckBox.IsChecked ?? false;
        public string DesiredAction => DesiredActionTextBox.Text;

        public UploadReportDialog()
        {
            this.InitializeComponent();
        }
    }
}

