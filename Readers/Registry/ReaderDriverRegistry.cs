using System;
using System.Collections.Generic;
using NSPGatekeeper.Controller.Domain;

namespace NSPGatekeeper.Controller.Readers
{
    public sealed class ReaderDriverRegistry
    {
        private readonly Dictionary<string, IReaderDriverFactory> _factories =
            new Dictionary<string, IReaderDriverFactory>(StringComparer.OrdinalIgnoreCase);

        public void Register(IReaderDriverFactory factory)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            if (string.IsNullOrWhiteSpace(factory.DriverKey)) throw new ArgumentException("DriverKey is required.");
            _factories[factory.DriverKey.Trim()] = factory;
        }

        public IReaderRuntime Create(ReaderDeviceConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            IReaderDriverFactory factory;
            if (!_factories.TryGetValue(config.DriverKey ?? string.Empty, out factory))
                throw new InvalidOperationException("Reader driver is not registered: " + (config.DriverKey ?? "<empty>"));
            return factory.Create(config);
        }

        public IList<IReaderDriverFactory> List()
        {
            return new List<IReaderDriverFactory>(_factories.Values);
        }
    }
}
