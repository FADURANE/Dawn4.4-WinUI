using Dawn44.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Dawn44.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        internal const string TraySwitch = "--tray";
        internal const string ElevatedRelaunchSwitch = Elevation.ElevatedRelaunchSwitch;

        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            UnhandledException += App_UnhandledException;
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var launchArguments = GetLaunchArguments(args);
            var isTrayLaunch = HasSwitch(launchArguments, TraySwitch);
            var isElevatedRelaunch = HasSwitch(launchArguments, ElevatedRelaunchSwitch);

            if (!SingleInstanceManager.TryAcquire())
            {
                SingleInstanceManager.NotifyExistingInstance();
                Environment.Exit(0);
                return;
            }

            // Auto-restart as admin if the user has requested it and we're not yet elevated.
            if (!isElevatedRelaunch && SettingsStore.GetRunAsAdmin() && !Elevation.IsCurrentProcessElevated())
            {
                // Hand the single-instance mutex to the elevated child before starting it,
                // otherwise it sees this process as an existing instance and exits at once.
                SingleInstanceManager.Release();

                if (Elevation.TryRestartAsAdmin(launchArguments))
                {
                    Environment.Exit(0);
                    return;
                }

                // Elevation was declined or is unattended (a logon-time UAC prompt nobody
                // answers). Continue unelevated rather than silently failing to start.
                SingleInstanceManager.TryAcquire();
            }

            try
            {
                _window = new MainWindow(isTrayLaunch);
                _window.Activate();
            }
            catch (Exception ex)
            {
                WriteCrashLog(ex);
                throw;
            }
        }

        /// <summary>
        /// WinUI 3 leaves <see cref="Microsoft.UI.Xaml.LaunchActivatedEventArgs.Arguments"/> empty
        /// for unpackaged apps, so the process command line is the only reliable source.
        /// See https://github.com/microsoft/WindowsAppSDK/issues/1619.
        /// </summary>
        private static string[] GetLaunchArguments(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var processArguments = Environment.GetCommandLineArgs();
            if (processArguments.Length > 1)
            {
                return processArguments[1..];
            }

            return (args.Arguments ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static bool HasSwitch(string[] arguments, string name)
        {
            return arguments.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        }

        private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            WriteCrashLog(e.Exception);
        }

        private static void WriteCrashLog(Exception ex)
        {
            try
            {
                var directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Dawn4.4 Control");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    System.IO.Path.Combine(directory, "crash.log"),
                    $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
