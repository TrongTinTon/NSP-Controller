using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace NSPGatekeeper.Controller.Readers.CFE718.Sdk
{
    /// <summary>
    /// One managed UHFReader288 object equals one SDK connection/session.
    /// The vendor C# API owns the serial/TCP transport internally; no unmanaged FrmHandle
    /// is exposed by the documented C# interface.
    /// </summary>
    internal sealed class UhfReader288Session : IDisposable
    {
        private static int _nextSessionId;
        private readonly Type _readerType;
        private readonly object _reader;
        private Action<UhfReader288Tag> _tagCallback;
        private Delegate _vendorCallback;
        private bool _disposed;

        internal UhfReader288Session(Type readerType)
        {
            _readerType = readerType ?? throw new ArgumentNullException("readerType");
            _reader = Activator.CreateInstance(_readerType);
            SessionId = Interlocked.Increment(ref _nextSessionId);
        }

        internal int SessionId { get; private set; }
        internal UhfReader288ConnectionKind ConnectionKind { get; private set; }

        internal int OpenComPort(int port, ref byte comAddress, byte baud)
        {
            ThrowIfDisposed();
            var args = new object[] { port, comAddress, baud };
            var result = InvokeInt("OpenComPort", args);
            comAddress = ToByte(args[1]);
            if (result == 0) ConnectionKind = UhfReader288ConnectionKind.Com;
            return result;
        }

        internal int OpenNetPort(int port, string ipAddress, ref byte comAddress)
        {
            ThrowIfDisposed();
            var args = new object[] { port, ipAddress, comAddress };
            var result = InvokeInt("OpenNetPort", args);
            comAddress = ToByte(args[2]);
            if (result == 0) ConnectionKind = UhfReader288ConnectionKind.Tcp;
            return result;
        }

        internal int GetSerialNumber(ref byte comAddress, byte[] serialNumber)
        {
            var args = new object[] { comAddress, serialNumber };
            var result = InvokeInt("GetSeriaNo", args);
            comAddress = ToByte(args[0]);
            return result;
        }

        internal int GetModuleVersion(ref byte comAddress, byte[] moduleVersion)
        {
            var args = new object[] { comAddress, moduleVersion };
            var result = InvokeInt("GetModuleVersion", args);
            comAddress = ToByte(args[0]);
            return result;
        }

        internal int SetInventoryScanTime(ref byte comAddress, byte scanTime)
        {
            var args = new object[] { comAddress, scanTime };
            var result = InvokeInt("SetInventoryScanTime", args);
            comAddress = ToByte(args[0]);
            return result;
        }

        internal int SetRfPower(ref byte comAddress, byte powerDbm)
        {
            var args = new object[] { comAddress, powerDbm };
            var result = InvokeInt("SetRfPower", args, typeof(byte));
            comAddress = ToByte(args[0]);
            return result;
        }

        internal UhfReader288InventoryResult InventoryG2(ref byte comAddress, UhfReader288InventoryRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");

            byte antenna = 0;
            int totalLength = 0;
            int tagCount = 0;
            var args = new object[]
            {
                comAddress,
                request.QValue,
                request.Session,
                request.MaskMemory,
                request.MaskAddress,
                request.MaskLength,
                request.MaskData,
                request.MaskFlag,
                request.TidAddress,
                request.TidLength,
                request.TidFlag,
                request.Target,
                request.AntennaSelector,
                request.ScanTime,
                request.FastFlag,
                request.OutputBuffer,
                antenna,
                totalLength,
                tagCount
            };

            var result = InvokeInt("Inventory_G2", args);
            comAddress = ToByte(args[0]);
            antenna = ToByte(args[16]);
            totalLength = ToInt(args[17]);
            tagCount = ToInt(args[18]);

            return new UhfReader288InventoryResult
            {
                ResultCode = result,
                Antenna = antenna,
                TotalLength = totalLength,
                TagCount = tagCount
            };
        }

        internal void RegisterTagCallback(Action<UhfReader288Tag> callback)
        {
            ThrowIfDisposed();
            if (callback == null) throw new ArgumentNullException("callback");

            var method = FindMethod("InitRFIDCallBack", 1, null);
            if (method == null)
                throw new MissingMethodException(_readerType.FullName, "InitRFIDCallBack(1)");

            var callbackType = method.GetParameters()[0].ParameterType;
            _tagCallback = callback;
            _vendorCallback = CreateVendorCallback(callbackType);
            method.Invoke(_reader, new object[] { _vendorCallback });
        }

        internal int Close()
        {
            if (_disposed) return 0;

            var connectionKind = ConnectionKind;
            if (connectionKind == UhfReader288ConnectionKind.None) return 0;

            MethodInfo method;
            if (connectionKind == UhfReader288ConnectionKind.Tcp)
            {
                method = FindMethod("CloseNetPort", 0, null);
            }
            else
            {
                method = FindMethod("CloseComPort", 0, null);
            }

            if (method == null)
                throw new MissingMethodException(
                    _readerType.FullName,
                    connectionKind == UhfReader288ConnectionKind.Tcp ? "CloseNetPort()" : "CloseComPort()");

            try
            {
                var value = method.Invoke(_reader, new object[0]);
                return value == null ? 0 : Convert.ToInt32(value);
            }
            finally
            {
                ConnectionKind = UhfReader288ConnectionKind.None;
            }
        }

        private int InvokeInt(string methodName, object[] args)
        {
            return InvokeInt(methodName, args, null);
        }

        private int InvokeInt(string methodName, object[] args, Type preferredLastParameterType)
        {
            ThrowIfDisposed();
            var method = FindMethod(methodName, args.Length, preferredLastParameterType);
            if (method == null)
                throw new MissingMethodException(_readerType.FullName, methodName + "(" + args.Length + ")");

            var value = method.Invoke(_reader, args);
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private MethodInfo FindMethod(string name, int parameterCount, Type preferredLastParameterType)
        {
            var methods = _readerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name == name && item.GetParameters().Length == parameterCount)
                .ToList();

            if (preferredLastParameterType != null)
            {
                var matched = methods.FirstOrDefault(item =>
                {
                    var parameters = item.GetParameters();
                    if (parameters.Length == 0) return false;
                    var type = parameters[parameters.Length - 1].ParameterType;
                    if (type.IsByRef) type = type.GetElementType();
                    return type == preferredLastParameterType;
                });
                if (matched != null) return matched;
            }

            return methods.FirstOrDefault();
        }

        private Delegate CreateVendorCallback(Type callbackType)
        {
            var invoke = callbackType.GetMethod("Invoke");
            if (invoke == null)
                throw new NotSupportedException("UHFReader288 callback type has no Invoke method.");

            var parameters = invoke.GetParameters();
            if (parameters.Length != 1)
                throw new NotSupportedException("Unsupported UHFReader288 callback signature. Expected one RFIDTag argument.");

            var tagParameter = Expression.Parameter(parameters[0].ParameterType, "tag");
            var dispatch = typeof(UhfReader288Session).GetMethod(
                "DispatchVendorTag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var call = Expression.Call(
                Expression.Constant(this),
                dispatch,
                Expression.Convert(tagParameter, typeof(object)));
            return Expression.Lambda(callbackType, call, tagParameter).Compile();
        }

        private void DispatchVendorTag(object vendorTag)
        {
            var callback = _tagCallback;
            if (callback == null || vendorTag == null) return;

            callback(new UhfReader288Tag
            {
                PacketParam = ReadByteMember(vendorTag, "PacketParam"),
                Length = ReadByteMember(vendorTag, "LEN"),
                Uid = ReadStringMember(vendorTag, "UID"),
                PhaseBegin = ReadIntMember(vendorTag, "phase_begin"),
                PhaseEnd = ReadIntMember(vendorTag, "phase_end"),
                Rssi = ReadByteMember(vendorTag, "RSSI"),
                FrequencyKhz = ReadIntMember(vendorTag, "Freqkhz"),
                Antenna = ReadByteMember(vendorTag, "ANT"),
                Handles = ReadIntMember(vendorTag, "Handles")
            });
        }

        private static object ReadMember(object source, string name)
        {
            var type = source.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(source);
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property == null ? null : property.GetValue(source, null);
        }

        private static byte ReadByteMember(object source, string name)
        {
            var value = ReadMember(source, name);
            return value == null ? (byte)0 : Convert.ToByte(value);
        }

        private static int ReadIntMember(object source, string name)
        {
            var value = ReadMember(source, name);
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private static string ReadStringMember(object source, string name)
        {
            var value = ReadMember(source, name);
            return value == null ? string.Empty : Convert.ToString(value);
        }

        private static byte ToByte(object value)
        {
            return value == null ? (byte)0 : Convert.ToByte(value);
        }

        private static int ToInt(object value)
        {
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                Close();
            }
            finally
            {
                _disposed = true;
                _vendorCallback = null;
                _tagCallback = null;
            }
        }
    }
}
