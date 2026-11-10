using System;
using System.Threading;
using System.Windows;

namespace TodoEdge
{
    public partial class App : Application
    {
        private Mutex? _mutex;

        public static AppController Controller { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, "TodoEdge.SingleInstance.Mutex", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("TodoEdge is already running. Look for the icon on your screen edge or in the system tray.",
                    "TodoEdge", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Older emails use Windows code pages (e.g. 1252, Western European). .NET needs these switched on.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(args.Exception.Message, "TodoEdge error", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            Controller = new AppController();
            Controller.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Controller?.Dispose();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
