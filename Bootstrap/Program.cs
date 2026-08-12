using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using NSPGatekeeper.Controller.Configuration;
using NSPGatekeeper.Controller.Infrastructure.Database;
using NSPGatekeeper.Controller.Infrastructure.Logging;
using NSPGatekeeper.Controller.Integration.CoreApi;
using NSPGatekeeper.Controller.Readers;
using NSPGatekeeper.Controller.Readers.CFE718;
using NSPGatekeeper.Controller.Readers.CFE718.Sdk;
using NSPGatekeeper.Controller.Services;
using NSPGatekeeper.Controller.UI;

namespace NSPGatekeeper.Controller.Bootstrap
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            Directory.SetCurrentDirectory(baseDirectory);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var settings = AppSettings.Load();
            var logger = new FileLogger(Path.Combine(baseDirectory, settings.LogDirectory ?? "logs"));
            RegisterUnhandledExceptionLogging(logger);

            logger.Info(
                "startup",
                "NSP Controller starting",
                "version=" + Assembly.GetExecutingAssembly().GetName().Version
                + "; controller=" + (string.IsNullOrWhiteSpace(settings.ControllerCode) ? "<not-configured>" : settings.ControllerCode)
                + "; base_dir=" + baseDirectory
                + "; os=" + Environment.OSVersion
                + "; process_arch=" + (Environment.Is64BitProcess ? "x64" : "x86")
                + "; sdk=" + UhfReader288Sdk.DescribeRuntime());

            try
            {
                var databaseBootstrapper = new DatabaseBootstrapper(
                    settings.PostgreSqlConnectionString,
                    settings.PostgreSqlAdminConnectionString,
                    logger);
                databaseBootstrapper.EnsureDatabase();

                var store = new LocalStore(settings.PostgreSqlConnectionString, logger);
                store.EnsureSchema();
                var recoveredGatewayDetections = store.RequeueGatewayConfigurationFailures();
                if (recoveredGatewayDetections > 0)
                    logger.Warn(
                        "parking-push",
                        "Recovered RFID detections previously dead-lettered by an Edge Gateway configuration error",
                        "count=" + recoveredGatewayDetections);
                store.MarkReaderRuntimeStatusesOffline();

                var registry = new ReaderDriverRegistry();
                registry.Register(new Cfe718ReaderFactory(logger));

                using (var outboxWriter = new DetectionOutboxWriter(store, logger))
                {
                    var readers = new ReaderManager(registry, store, outboxWriter, logger, settings);
                    var coreApi = new CoreApiClient(settings, logger);
                    using (var runtime = new ControllerRuntime(settings, store, coreApi, readers, logger))
                    {
                        runtime.Start();
                        logger.Info("startup", "Controller runtime started", "log_dir=" + logger.DirectoryPath);
                        Application.Run(new MainForm(settings, runtime, readers, store, logger));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error("startup", "Controller startup failed", ex, "base_dir=" + baseDirectory);
                MessageBox.Show(ex.Message, "NSP Gatekeeper Controller", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                logger.Info("startup", "NSP Controller stopped");
            }
        }

        private static void RegisterUnhandledExceptionLogging(FileLogger logger)
        {
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs args)
            {
                logger.Error("unhandled-ui", "Unhandled UI thread exception", args.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                logger.Error(
                    "unhandled-appdomain",
                    "Unhandled AppDomain exception",
                    args.ExceptionObject as Exception,
                    "terminating=" + args.IsTerminating);
            };

            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs args)
            {
                logger.Error("unhandled-task", "Unobserved task exception", args.Exception);
                args.SetObserved();
            };
        }
    }
}
