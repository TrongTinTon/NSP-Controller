using System;
using NSPGatekeeper.Controller.Readers.CFE718.Sdk;

namespace NSPGatekeeper.Controller.Readers.CFE718
{
    internal static class Cfe718ReaderConfiguration
    {
        internal static void Apply(UhfReader288Session session, ref byte comAddress, Cfe718Options options)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (options == null) throw new ArgumentNullException("options");

            Cfe718ReaderIdentity.EnsureSuccess(
                "SetInventoryScanTime",
                session.SetInventoryScanTime(ref comAddress, options.ScanTime),
                "requested_interval_ms=" + options.Config.ReadIntervalMs
                + "; sdk_scan_time=" + options.ScanTime
                + "; session_id=" + session.SessionId);

            const byte allFourPortsMask = 0x0F;
            Cfe718ReaderIdentity.EnsureSuccess(
                "SetAntennaMultiplexing",
                session.SetAntennaMultiplexing(ref comAddress, allFourPortsMask),
                "scan_ports=1,2,3,4; session_id=" + session.SessionId);

            var power = Cfe718Options.ClampByte(options.Config.PowerDbm, 0, 33);
            var powers = new byte[options.HardwarePorts.Count];
            for (var index = 0; index < powers.Length; index++) powers[index] = power;

            Cfe718ReaderIdentity.EnsureSuccess(
                "SetAntennaPower",
                session.SetAntennaPower(ref comAddress, powers, powers.Length),
                "power_dbm=" + power
                + "; array_length=" + powers.Length
                + "; session_id=" + session.SessionId);

            // TID address and length are intentionally supplied on every Inventory_G2 request.
            // SetTIDParameter is not required for synchronous CF-E718 inventory.
        }
    }
}
