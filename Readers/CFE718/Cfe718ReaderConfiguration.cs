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

            var powerDbm = Cfe718Options.ClampByte(options.Config.PowerDbm, 0, 33);
            Cfe718ReaderIdentity.EnsureSuccess(
                "SetRfPower",
                session.SetRfPower(ref comAddress, powerDbm),
                "power_dbm=" + powerDbm
                + "; scope=reader"
                + "; session_id=" + session.SessionId);

            // Controller does not configure antenna topology or routing.
            // It only applies Reader-wide RF power and reports raw SDK port observations to Edge.

            // TID address and length are intentionally supplied on every Inventory_G2 request.
            // SetTIDParameter is not required for synchronous CF-E718 inventory.
        }
    }
}
