using System.Globalization;

namespace NSPGatekeeper.Controller.Readers.CFE718.Sdk
{
    internal static class UhfReader288Result
    {
        internal static string Format(int result)
        {
            return result.ToString(CultureInfo.InvariantCulture)
                + " (0x" + unchecked((uint)result).ToString("X8", CultureInfo.InvariantCulture) + ")";
        }

        internal static bool IsInventoryAccepted(int result)
        {
            // SDK manual: 0x01 = completed, 0x02 = timeout with returned data.
            // Existing CF-E718 firmware also uses 0xFB for a normal no-tag cycle.
            return result == 0 || result == 1 || result == 2 || result == 0xFB;
        }

        internal static bool IsSelectorRetryCandidate(int result)
        {
            return result == 0xFF || result == 0xFD;
        }
    }
}
