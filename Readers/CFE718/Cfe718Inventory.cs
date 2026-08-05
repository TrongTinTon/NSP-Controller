using System;
using NSPGatekeeper.Controller.Readers.CFE718.Sdk;

namespace NSPGatekeeper.Controller.Readers.CFE718
{
    internal sealed class Cfe718Inventory
    {
        private readonly Cfe718Options _options;

        internal Cfe718Inventory(Cfe718Options options)
        {
            _options = options ?? throw new ArgumentNullException("options");
        }

        internal int Execute(UhfReader288Session session, ref byte comAddress, int portNo)
        {
            var selector = _options.PortSelector(portNo, false);
            var result = ExecuteWithSelector(session, ref comAddress, selector);
            if (!UhfReader288Result.IsSelectorRetryCandidate(result)) return result;

            var alternate = _options.PortSelector(portNo, true);
            if (alternate == selector) return result;
            return ExecuteWithSelector(session, ref comAddress, alternate);
        }

        private int ExecuteWithSelector(UhfReader288Session session, ref byte comAddress, byte selector)
        {
            var request = new UhfReader288InventoryRequest
            {
                QValue = _options.QValue,
                Session = _options.Session,
                MaskMemory = 0x02,
                MaskLength = 0,
                MaskFlag = 0,
                TidAddress = Cfe718Options.ClampByte(_options.Config.TidStartAddress, 0, 255),
                TidLength = Cfe718Options.ClampByte(_options.Config.TidLength, 1, 15),
                TidFlag = 1,
                Target = _options.Target,
                AntennaSelector = selector,
                ScanTime = _options.ScanTime,
                FastFlag = _options.FastFlag
            };

            return session.InventoryG2(ref comAddress, request).ResultCode;
        }
    }
}
