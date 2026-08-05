using System;
using System.Collections.Generic;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Logging;
using NSPGatekeeper.Controller.Readers;

namespace NSPGatekeeper.Controller.Readers.CFE718
{
    public sealed class Cfe718ReaderFactory : IReaderDriverFactory, IReaderDiscoveryProvider
    {
        private readonly FileLogger _logger;

        public Cfe718ReaderFactory(FileLogger logger)
        {
            _logger = logger;
        }

        public string DriverKey { get { return "cf-e718"; } }

        public IReaderRuntime Create(ReaderDeviceConfig config)
        {
            return new Cfe718ReaderRuntime(config, _logger);
        }

        public IList<ReaderDiscoveryObservation> Discover(ISet<string> excludedEndpoints)
        {
            return Cfe718ReaderDiscovery.Discover(_logger, excludedEndpoints);
        }
    }
}
