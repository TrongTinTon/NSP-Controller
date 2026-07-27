using System;
using System.IO;
using System.Windows.Forms;
using NSPGatekeeper.Controller.Configuration;
using NSPGatekeeper.Controller.Infrastructure.Database;
using NSPGatekeeper.Controller.Infrastructure.Logging;
using NSPGatekeeper.Controller.Integration.CoreApi;
using NSPGatekeeper.Controller.Readers;
using NSPGatekeeper.Controller.Readers.CFE718;
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

            try
            {
                var databaseBootstrapper = new DatabaseBootstrapper(
                    settings.PostgreSqlConnectionString,
                    settings.PostgreSqlAdminConnectionString,
                    logger);
                databaseBootstrapper.EnsureDatabase();

                var store = new LocalStore(settings.PostgreSqlConnectionString, logger);
                store.EnsureSchema();

                var registry = new ReaderDriverRegistry();
                registry.Register(new Cfe718ReaderFactory(logger));

                using (var outboxWriter = new DetectionOutboxWriter(store, logger))
                {
                    var readers = new ReaderManager(registry, store, outboxWriter, logger, settings);
                    var coreApi = new CoreApiClient(settings, logger);
                    using (var runtime = new ControllerRuntime(settings, store, coreApi, readers, logger))
                    {
                        runtime.Start();
                        Application.Run(new MainForm(settings, runtime, readers, store, logger));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error("startup", "Controller startup failed", ex);
                MessageBox.Show(ex.Message, "NSP Gatekeeper Controller", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
