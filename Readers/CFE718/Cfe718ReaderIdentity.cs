using System;
using System.Globalization;
using System.Linq;
using System.Text;
using NSPGatekeeper.Controller.Readers.CFE718.Sdk;

namespace NSPGatekeeper.Controller.Readers.CFE718
{
    internal sealed class Cfe718ReaderIdentity
    {
        internal string SerialNumber { get; private set; }
        internal string FirmwareVersion { get; private set; }
        internal string RawSerial { get; private set; }

        internal static Cfe718ReaderIdentity Read(UhfReader288Session session, ref byte comAddress)
        {
            if (session == null) throw new ArgumentNullException("session");

            var serialBytes = new byte[4];
            var result = session.GetSerialNumber(ref comAddress, serialBytes);
            var rawSerial = BitConverter.ToString(serialBytes).Replace("-", string.Empty);
            EnsureSuccess("GetSeriaNo", result, "raw_serial=" + rawSerial + "; session_id=" + session.SessionId);

            var serial = ToHardwareSerial(serialBytes);
            if (string.IsNullOrWhiteSpace(serial))
                throw new InvalidOperationException("Reader SDK returned an empty SerialNumber. raw_serial=" + rawSerial);

            string firmware = null;
            try
            {
                var module = new byte[32];
                if (session.GetModuleVersion(ref comAddress, module) == 0)
                {
                    var value = Encoding.ASCII.GetString(module).Trim('\0', ' ', '\r', '\n');
                    if (!string.IsNullOrWhiteSpace(value)) firmware = value;
                }
            }
            catch
            {
                firmware = null;
            }

            return new Cfe718ReaderIdentity
            {
                SerialNumber = serial,
                FirmwareVersion = firmware,
                RawSerial = rawSerial
            };
        }

        internal static string ToHardwareSerial(byte[] value)
        {
            if (value == null || value.Length < 4) return null;
            if (value.Take(4).All(item => item == 0x00) || value.Take(4).All(item => item == 0xFF)) return null;
            return string.Concat(value.Take(4).Select(item => item.ToString("X2", CultureInfo.InvariantCulture)));
        }

        internal static void EnsureSuccess(string operation, int result, string context)
        {
            if (result == 0) return;
            throw new InvalidOperationException(
                operation + " failed. sdk_result=" + UhfReader288Result.Format(result)
                + (string.IsNullOrWhiteSpace(context) ? string.Empty : "; " + context));
        }
    }
}
