using SFRT_PlanningScript;
using SFRT_PlanningScript.Views;
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using VMS.TPS.Common.Model.API;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ESAPIScript;

[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{
    public class Script
    {

        private void RunOnNewStaThread(Action a)
        {
            Thread thread = new Thread(() =>
            {
                // Belt-and-suspenders: pin this STA thread's culture too, so the
                // GUI's data binding / number parsing never depends on the
                // per-user Citrix OS locale (which a freshly created thread would
                // otherwise inherit).
                var enUs = new CultureInfo("en-US");
                Thread.CurrentThread.CurrentCulture = enUs;
                Thread.CurrentThread.CurrentUICulture = enUs;
                a();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        private void InitializeAndStartMainWindow(EsapiWorker esapiWorker)
        {
            var viewModel = new SphereDialog(esapiWorker);
            var mainWindow = new MainWindow(viewModel);
            mainWindow.ShowDialog();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext scriptcontext)
        {
            if (scriptcontext.Patient == null || scriptcontext.PlanSetup == null)
            {
                MessageBox.Show("No active patient/plan selected - exiting",
                                "SFRT_PlanningScript",
                                MessageBoxButton.OK,
                                MessageBoxImage.Exclamation);
                return;
            }

            // Force a stable, invariant-compatible culture for BOTH the ESAPI
            // thread and every thread we spawn. The GUI runs on its own STA
            // thread (see RunOnNewStaThread); without DefaultThreadCurrentCulture
            // that thread inherits the Citrix user's OS locale, which silently
            // breaks number parsing/formatting of templates and defaults and
            // makes the bug present for some users but not others.
            var enUs = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentCulture = enUs;
            Thread.CurrentThread.CurrentUICulture = enUs;
            CultureInfo.DefaultThreadCurrentCulture = enUs;
            CultureInfo.DefaultThreadCurrentUICulture = enUs;

            // Helpers.SeriLog.Initialize(scriptcontext.CurrentUser.Id);
            // The ESAPI worker needs to be created in the main thread
            var esapiWorker = new EsapiWorker(scriptcontext.Patient, scriptcontext.PlanSetup);

            // This new queue of tasks will prevent the script
            // for exiting until the new window is closed
            DispatcherFrame frame = new DispatcherFrame();

            RunOnNewStaThread(() =>
            {
                // This method won't return until the window is closed
                InitializeAndStartMainWindow(esapiWorker);
                // End the queue so that the script can exit
                frame.Continue = false;
            });

            // Start the new queue, waiting until the window is closed
            Dispatcher.PushFrame(frame);
        }
    }
}
