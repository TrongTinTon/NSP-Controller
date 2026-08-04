using System;

namespace NSPGatekeeper.Controller.Domain
{
    public abstract class ReaderConfigurationSynchronizationException : InvalidOperationException
    {
        protected ReaderConfigurationSynchronizationException(string serialNumber, string message)
            : base(message)
        {
            SerialNumber = (serialNumber ?? string.Empty).Trim().ToUpperInvariant();
        }

        public string SerialNumber { get; private set; }
    }

    public sealed class ReaderConfigurationUnavailableException : ReaderConfigurationSynchronizationException
    {
        public ReaderConfigurationUnavailableException(string serialNumber, string message)
            : base(serialNumber, message)
        {
        }
    }
}
