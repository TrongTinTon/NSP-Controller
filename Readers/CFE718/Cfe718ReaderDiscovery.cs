using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Logging;
using NSPGatekeeper.Controller.Readers.CFE718.Sdk;

namespace NSPGatekeeper.Controller.Readers.CFE718
{
    internal static class Cfe718ReaderDiscovery
    {
        private static readonly object Gate = new object();

        internal static IList<ReaderDiscoveryObservation> Discover(
            FileLogger logger,
            ISet<string> excludedEndpoints)
        {
            var result = new List<ReaderDiscoveryObservation>();
            var excluded = new HashSet<string>(
                excludedEndpoints ?? new HashSet<string>(),
                StringComparer.OrdinalIgnoreCase);

            IList<int> ports;
            try
            {
                ports = SerialPort.GetPortNames()
                    .Select(Cfe718Options.ParseComPort)
                    .Where(value => value > 0)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList();
            }
            catch
            {
                return result;
            }

            lock (Gate)
            {
                foreach (var port in ports)
                {
                    var endpoint = "COM" + port.ToString(CultureInfo.InvariantCulture);
                    if (excluded.Contains(endpoint)) continue;

                    UhfReader288Session session = null;
                    byte address = 0xFF;
                    try
                    {
                        session = UhfReader288Sdk.CreateSession();
                        var open = session.OpenComPort(port, ref address, 6);
                        if (open != 0) continue;

                        var identity = Cfe718ReaderIdentity.Read(session, ref address);
                        result.Add(new ReaderDiscoveryObservation
                        {
                            DriverKey = "cf-e718",
                            SerialNumber = identity.SerialNumber,
                            Endpoint = endpoint,
                            FirmwareVersion = identity.FirmwareVersion,
                            DiscoveredAtUtc = DateTime.UtcNow
                        });

                        if (logger != null)
                            logger.Info(
                                "reader-discovery",
                                "Physical Reader discovered",
                                "serial=" + identity.SerialNumber
                                + "; endpoint=" + endpoint
                                + "; firmware=" + (identity.FirmwareVersion ?? "<unknown>")
                                + "; session_id=" + session.SessionId);
                    }
                    catch
                    {
                        // A Windows COM port may belong to any device. Discovery reports only
                        // positive SDK observations and does not classify non-Reader devices.
                    }
                    finally
                    {
                        if (session != null)
                        {
                            try
                            {
                                var connection = session.ConnectionKind;
                                var close = session.Close();
                                if (close != 0 && logger != null)
                                    logger.Warn(
                                        "reader-discovery",
                                        "Physical Reader discovery transport close returned a non-zero SDK result",
                                        "endpoint=" + endpoint
                                        + "; connection=" + connection
                                        + "; session_id=" + session.SessionId
                                        + "; result=" + UhfReader288Result.Format(close));
                            }
                            catch (Exception closeError)
                            {
                                if (logger != null)
                                    logger.Error(
                                        "reader-discovery",
                                        "Physical Reader discovery transport close failed",
                                        closeError,
                                        "endpoint=" + endpoint
                                        + "; session_id=" + session.SessionId);
                            }
                            finally
                            {
                                session.Dispose();
                            }
                        }
                    }
                }
            }

            return result;
        }
    }
}
