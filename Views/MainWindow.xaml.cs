using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using VMS.TPS.Common.Model.API;

namespace SFRT_PlanningScript.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow(SphereDialog vm)
        {
            InitializeComponent();
            HostContent.Content = vm;
        }

        // Citrix/RDP render-tier workaround: WPF binds the (virtual-)GPU hardware
        // tier once at process start; in a remote session that can land wrong and
        // render content dark/blank for some users until Eclipse is relaunched.
        // Force software rendering, but only in a remote session (local users keep
        // hardware acceleration) and only for this window (not process-wide, which
        // would also change Eclipse's own UI). Code-behind, so it can't cause a
        // XAML-load crash; failures are swallowed as this is only a render hint.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                if (GetSystemMetrics(SM_REMOTESESSION) != 0
                    && PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src
                    && src.CompositionTarget != null)
                {
                    src.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                }
            }
            catch { /* a rendering hint only — never block the window from opening */ }
        }

        private const int SM_REMOTESESSION = 0x1000;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            // for .NET Core you need to add UseShellExecute = true
            // see https://learn.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.useshellexecute#property-value
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }
    }
}
