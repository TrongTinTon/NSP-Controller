using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace NSPGatekeeper.Controller.Readers.CFE718
{
    /// <summary>
    /// CF-E718 / UHFReader288 SDK facade.
    ///
    /// x86 SDK: native DLL with unmanaged exports, called through P/Invoke.
    /// x64 SDK: managed .NET assembly, called through reflection.
    ///
    /// The rest of NSPGatekeeper uses one stable API and does not care which SDK type is loaded.
    /// </summary>
    internal static class Cfe718Native
    {
        private const string DllName = "UHFReader288.dll";

        [StructLayout(LayoutKind.Sequential)]
        internal struct RFIDTag
        {
            public byte PacketParam;
            public byte LEN;
            public string UID;
            public int phase_begin;
            public int phase_end;
            public byte RSSI;
            public int Freqkhz;
            public byte ANT;
            public Int32 Handles;
        }

        internal delegate void RFIDCallBack(IntPtr p, Int32 nEvt);

        private static bool UseManagedX64Sdk
        {
            get { return IntPtr.Size == 8; }
        }

        internal static string DescribeRuntime()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DllName);
            var file = new FileInfo(path);
            string version = null;
            try
            {
                if (file.Exists) version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            }
            catch
            {
                version = null;
            }

            return "mode=" + (UseManagedX64Sdk ? "x64-managed-reflection" : "x86-native-pinvoke")
                + "; path=" + path
                + "; exists=" + file.Exists
                + "; size=" + (file.Exists ? file.Length.ToString() : "0")
                + "; file_version=" + (string.IsNullOrWhiteSpace(version) ? "<unknown>" : version)
                + (UseManagedX64Sdk
                    ? "; managed_reader_type=" + ManagedSdk.LoadedReaderTypeName
                      + "; managed_sessions=" + ManagedSdk.SessionCount
                    : string.Empty);
        }

        internal static string FormatResult(int result)
        {
            return result.ToString() + " (0x" + unchecked((uint)result).ToString("X8") + ")";
        }

        internal static void InitRFIDCallBack(RFIDCallBack callback, bool uidBack, int portHandle)
        {
            if (UseManagedX64Sdk)
            {
                ManagedSdk.Get(portHandle).InitRFIDCallBack(callback);
                return;
            }
            NativeMethods.InitRFIDCallBack(callback, uidBack, portHandle);
        }

        internal static int OpenNetPort(int port, string ipAddress, ref byte comAddress, ref int portHandle)
        {
            if (UseManagedX64Sdk)
            {
                var session = ManagedSdk.Create();
                var ret = session.OpenNetPort(port, ipAddress, ref comAddress);
                if (ret == 0) portHandle = session.Handle;
                else ManagedSdk.Close(session.Handle);
                return ret;
            }
            return NativeMethods.OpenNetPort(port, ipAddress, ref comAddress, ref portHandle);
        }

        internal static int CloseNetPort(int portHandle)
        {
            if (UseManagedX64Sdk) return ManagedSdk.Close(portHandle);
            return NativeMethods.CloseNetPort(portHandle);
        }

        internal static int OpenComPort(int port, ref byte comAddress, byte baud, ref int portHandle)
        {
            if (UseManagedX64Sdk)
            {
                var session = ManagedSdk.Create();
                var ret = session.OpenComPort(port, ref comAddress, baud);
                if (ret == 0) portHandle = session.Handle;
                else ManagedSdk.Close(session.Handle);
                return ret;
            }
            return NativeMethods.OpenComPort(port, ref comAddress, baud, ref portHandle);
        }


        internal static int CloseComPort(int port, int portHandle)
        {
            if (UseManagedX64Sdk) return ManagedSdk.Close(portHandle);
            return NativeMethods.CloseSpecComPort(port);
        }

        internal static int StopInventory(ref byte comAddress, int portHandle)
        {
            if (UseManagedX64Sdk) return ManagedSdk.Get(portHandle).StopInventory(ref comAddress);
            return NativeMethods.StopInventory(ref comAddress, portHandle);
        }

        internal static int GetSeriaNo(ref byte comAddress, byte[] serialNo, int portHandle)
        {
            if (UseManagedX64Sdk) return ManagedSdk.Get(portHandle).GetSeriaNo(ref comAddress, serialNo);
            return NativeMethods.GetSeriaNo(ref comAddress, serialNo, portHandle);
        }

        internal static int GetModuleVersion(ref byte comAddress, byte[] moduleVersion, int portHandle)
        {
            if (UseManagedX64Sdk) return ManagedSdk.Get(portHandle).GetModuleVersion(ref comAddress, moduleVersion);
            return NativeMethods.GetModuleVersion(ref comAddress, moduleVersion, portHandle);
        }

        internal static int SetInventoryScanTime(ref byte comAddress, byte scanTime, int portHandle)
        {
            if (UseManagedX64Sdk) return ManagedSdk.Get(portHandle).SetInventoryScanTime(ref comAddress, scanTime);
            return NativeMethods.SetInventoryScanTime(ref comAddress, scanTime, portHandle);
        }

        internal static int SetTIDParameter(ref byte comAddress, byte tidAddr, byte tidLen, int portHandle)
        {
            if (UseManagedX64Sdk) return ManagedSdk.Get(portHandle).SetTIDParameter(ref comAddress, tidAddr, tidLen);
            return NativeMethods.SetTIDParameter(ref comAddress, tidAddr, tidLen, portHandle);
        }

        internal static int SetAntennaPower(ref byte comAddress, byte[] powerDbm, int length, int portHandle)
        {
            if (UseManagedX64Sdk) return ManagedSdk.Get(portHandle).SetAntennaPower(ref comAddress, powerDbm, length);
            return NativeMethods.SetAntennaPower(ref comAddress, powerDbm, length, portHandle);
        }

        internal static int SetAntennaMultiplexing4(ref byte comAddress, byte antMask, int portHandle)
        {
            if (UseManagedX64Sdk) return ManagedSdk.Get(portHandle).SetAntennaMultiplexing4(ref comAddress, antMask);
            return NativeMethods.SetAntennaMultiplexing4(ref comAddress, antMask, portHandle);
        }

        internal static int Inventory_G2(
            ref byte comAddress,
            byte qValue,
            byte session,
            byte maskMem,
            byte[] maskAdr,
            byte maskLen,
            byte[] maskData,
            byte maskFlag,
            byte tidAddr,
            byte tidLen,
            byte tidFlag,
            byte target,
            byte inAnt,
            byte scanTime,
            byte fastFlag,
            byte[] epcList,
            ref byte antenna,
            ref int totalLen,
            ref int tagNum,
            int portHandle)
        {
            if (UseManagedX64Sdk)
            {
                return ManagedSdk.Get(portHandle).Inventory_G2(
                    ref comAddress, qValue, session, maskMem, maskAdr, maskLen, maskData, maskFlag,
                    tidAddr, tidLen, tidFlag, target, inAnt, scanTime, fastFlag, epcList,
                    ref antenna, ref totalLen, ref tagNum);
            }
            return NativeMethods.Inventory_G2(
                ref comAddress, qValue, session, maskMem, maskAdr, maskLen, maskData, maskFlag,
                tidAddr, tidLen, tidFlag, target, inAnt, scanTime, fastFlag, epcList,
                ref antenna, ref totalLen, ref tagNum, portHandle);
        }

        private sealed class ManagedSdk
        {
            private static readonly object Gate = new object();
            private static readonly Dictionary<int, ManagedSdk> Sessions = new Dictionary<int, ManagedSdk>();
            private static int NextHandle = 64000;
            private static Assembly Assembly;
            private static Type ReaderType;
            private static Type ManagedCallbackType;

            private enum ManagedConnectionKind
            {
                None,
                Com,
                Tcp
            }

            private readonly object _reader;
            private RFIDCallBack _callback;
            private Delegate _managedCallbackDelegate;
            private ManagedConnectionKind _connectionKind;

            private ManagedSdk(int handle)
            {
                Handle = handle;
                EnsureLoaded();
                _reader = Activator.CreateInstance(ReaderType);
            }

            public int Handle { get; private set; }

            public static int SessionCount
            {
                get
                {
                    lock (Gate) return Sessions.Count;
                }
            }

            public static string LoadedReaderTypeName
            {
                get { return ReaderType == null ? "<not-loaded>" : ReaderType.FullName; }
            }

            public static ManagedSdk Create()
            {
                lock (Gate)
                {
                    var handle = Interlocked.Increment(ref NextHandle);
                    var session = new ManagedSdk(handle);
                    Sessions[handle] = session;
                    return session;
                }
            }

            public static ManagedSdk Get(int handle)
            {
                lock (Gate)
                {
                    ManagedSdk session;
                    if (!Sessions.TryGetValue(handle, out session))
                    {
                        throw new InvalidOperationException("CF-E718 x64 managed SDK session not found. Handle=" + handle);
                    }
                    return session;
                }
            }

            public static int Close(int handle)
            {
                ManagedSdk session;
                lock (Gate)
                {
                    if (!Sessions.TryGetValue(handle, out session)) return 0;
                    Sessions.Remove(handle);
                }
                return session.CloseInternal();
            }

            private static void EnsureLoaded()
            {
                if (Assembly != null) return;
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DllName);
                if (!File.Exists(path))
                {
                    throw new DllNotFoundException("Cannot find CF-E718 x64 managed SDK: " + path);
                }
                Assembly = Assembly.LoadFrom(path);
                ReaderType = FindTypeByFullNameOrName(Assembly, "UHF.UHFReader", "UHFReaderModule.UHFReader", "UHFReaderModule.Reader", "UHFReader");
                ManagedCallbackType = FindTypeByFullNameOrName(Assembly, "UHF.RFIDCallBack", "UHFReaderModule.RFIDCallBack", "RFIDCallBack");
                if (ReaderType == null)
                    throw new TypeLoadException("Cannot find CF-E718 x64 reader type in " + path);
                if (ManagedCallbackType == null)
                    throw new TypeLoadException("Cannot find CF-E718 x64 RFID callback type in " + path);
            }

            private static Type FindTypeByFullNameOrName(Assembly assembly, params string[] candidates)
            {
                if (assembly == null) return null;
                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    var exact = assembly.GetType(candidate, false);
                    if (exact != null) return exact;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types == null ? new Type[0] : ex.Types.Where(t => t != null).ToArray();
                }

                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    var byName = types.FirstOrDefault(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase) || string.Equals(t.FullName, candidate, StringComparison.OrdinalIgnoreCase));
                    if (byName != null) return byName;
                }

                return null;
            }

            public void InitRFIDCallBack(RFIDCallBack callback)
            {
                if (callback == null) throw new ArgumentNullException("callback");
                _callback = callback;
                var method = FindMethod("InitRFIDCallBack", 1);
                if (method == null)
                    throw new MissingMethodException(ReaderType.FullName, "InitRFIDCallBack(1)");
                _managedCallbackDelegate = CreateManagedCallbackDelegate();
                method.Invoke(_reader, new object[] { _managedCallbackDelegate });
            }

            public int OpenNetPort(int port, string ipAddress, ref byte comAddress)
            {
                _connectionKind = ManagedConnectionKind.Tcp;
                var args = new object[] { port, ipAddress, comAddress };
                var ret = InvokeInt("OpenNetPort", args);
                comAddress = ToByte(args[2]);
                return ret;
            }

            public int OpenComPort(int port, ref byte comAddress, byte baud)
            {
                _connectionKind = ManagedConnectionKind.Com;
                var args = new object[] { port, comAddress, baud };
                var ret = InvokeInt("OpenComPort", args);
                comAddress = ToByte(args[1]);
                return ret;
            }


            public int StopInventory(ref byte comAddress)
            {
                var args = new object[] { comAddress };
                var ret = InvokeInt("StopInventory", args);
                comAddress = ToByte(args[0]);
                return ret;
            }

            public int GetSeriaNo(ref byte comAddress, byte[] serialNo)
            {
                var args = new object[] { comAddress, serialNo };
                var ret = InvokeInt("GetSeriaNo", args);
                comAddress = ToByte(args[0]);
                return ret;
            }

            public int GetModuleVersion(ref byte comAddress, byte[] moduleVersion)
            {
                var args = new object[] { comAddress, moduleVersion };
                var ret = InvokeInt("GetModuleVersion", args);
                comAddress = ToByte(args[0]);
                return ret;
            }

            public int SetInventoryScanTime(ref byte comAddress, byte scanTime)
            {
                var args = new object[] { comAddress, scanTime };
                var ret = InvokeInt("SetInventoryScanTime", args);
                comAddress = ToByte(args[0]);
                return ret;
            }

            public int SetTIDParameter(ref byte comAddress, byte tidAddr, byte tidLen)
            {
                var args = new object[] { comAddress, tidAddr, tidLen };
                var ret = InvokeInt("SetTIDParameter", args);
                comAddress = ToByte(args[0]);
                return ret;
            }

            public int SetAntennaPower(ref byte comAddress, byte[] powerDbm, int length)
            {
                var args = new object[] { comAddress, powerDbm, length };
                var ret = InvokeInt("SetAntennaPower", args);
                comAddress = ToByte(args[0]);
                return ret;
            }

            public int SetAntennaMultiplexing4(ref byte comAddress, byte antMask)
            {
                var args = new object[] { comAddress, antMask };
                var ret = InvokeInt("SetAntennaMultiplexing", args);
                comAddress = ToByte(args[0]);
                return ret;
            }

            public int Inventory_G2(
                ref byte comAddress,
                byte qValue,
                byte session,
                byte maskMem,
                byte[] maskAdr,
                byte maskLen,
                byte[] maskData,
                byte maskFlag,
                byte tidAddr,
                byte tidLen,
                byte tidFlag,
                byte target,
                byte inAnt,
                byte scanTime,
                byte fastFlag,
                byte[] epcList,
                ref byte antenna,
                ref int totalLen,
                ref int tagNum)
            {
                var args = new object[]
                {
                    comAddress, qValue, session, maskMem, maskAdr, maskLen, maskData, maskFlag,
                    tidAddr, tidLen, tidFlag, target, inAnt, scanTime, fastFlag, epcList,
                    antenna, totalLen, tagNum
                };
                var ret = InvokeInt("Inventory_G2", args);
                comAddress = ToByte(args[0]);
                antenna = ToByte(args[16]);
                totalLen = ToInt(args[17]);
                tagNum = ToInt(args[18]);
                return ret;
            }

            private int CloseInternal()
            {
                MethodInfo method;
                if (_connectionKind == ManagedConnectionKind.Tcp)
                {
                    method = FindMethod("CloseNetPort", 0) ?? FindMethod("CloseByTcp", 0);
                }
                else
                {
                    method = FindMethod("CloseSpecComPort", 0)
                        ?? FindMethod("CloseComPort", 0)
                        ?? FindMethod("CloseByCom", 0);
                }

                _connectionKind = ManagedConnectionKind.None;
                if (method == null)
                    throw new MissingMethodException(ReaderType.FullName, "Close connection method");
                var value = method.Invoke(_reader, new object[0]);
                return value == null ? 0 : Convert.ToInt32(value);
            }

            private int InvokeInt(string methodName, object[] args)
            {
                return InvokeInt(methodName, args, null);
            }

            private int InvokeInt(string methodName, object[] args, Type preferredLastParamType)
            {
                var method = FindMethod(methodName, args.Length, preferredLastParamType);
                if (method == null)
                {
                    throw new MissingMethodException(ReaderType.FullName, methodName + "(" + args.Length + ")");
                }
                var value = method.Invoke(_reader, args);
                return value == null ? 0 : Convert.ToInt32(value);
            }

            private MethodInfo FindMethod(string name, int parameterCount)
            {
                return FindMethod(name, parameterCount, null);
            }

            private MethodInfo FindMethod(string name, int parameterCount, Type preferredLastParamType)
            {
                var methods = ReaderType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == name && m.GetParameters().Length == parameterCount)
                    .ToList();
                if (preferredLastParamType != null)
                {
                    var matched = methods.FirstOrDefault(m =>
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 0) return false;
                        var t = ps[ps.Length - 1].ParameterType;
                        if (t.IsByRef) t = t.GetElementType();
                        return t == preferredLastParamType;
                    });
                    if (matched != null) return matched;
                }
                return methods.FirstOrDefault();
            }

            private Delegate CreateManagedCallbackDelegate()
            {
                var invoke = ManagedCallbackType.GetMethod("Invoke");
                var parameters = invoke.GetParameters();
                if (parameters.Length != 1)
                {
                    throw new NotSupportedException("Unsupported UHF.RFIDCallBack signature in x64 SDK.");
                }
                var tagParameter = Expression.Parameter(parameters[0].ParameterType, "tag");
                var call = Expression.Call(
                    Expression.Constant(this),
                    typeof(ManagedSdk).GetMethod("ManagedCallbackObject", BindingFlags.Instance | BindingFlags.NonPublic),
                    Expression.Convert(tagParameter, typeof(object)));
                return Expression.Lambda(ManagedCallbackType, call, tagParameter).Compile();
            }

            private void ManagedCallbackObject(object tagObject)
            {
                if (_callback == null || tagObject == null) return;
                var tag = new RFIDTag
                {
                    PacketParam = ReadByteMember(tagObject, "PacketParam"),
                    LEN = ReadByteMember(tagObject, "LEN"),
                    UID = ReadStringMember(tagObject, "UID"),
                    phase_begin = ReadIntMember(tagObject, "phase_begin"),
                    phase_end = ReadIntMember(tagObject, "phase_end"),
                    RSSI = ReadByteMember(tagObject, "RSSI"),
                    Freqkhz = ReadIntMember(tagObject, "Freqkhz"),
                    ANT = ReadByteMember(tagObject, "ANT"),
                    Handles = ReadIntMember(tagObject, "Handles")
                };

                var size = Marshal.SizeOf(typeof(RFIDTag));
                var pointer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(tag, pointer, false);
                    _callback(pointer, 0);
                }
                finally
                {
                    try { Marshal.DestroyStructure(pointer, typeof(RFIDTag)); } catch { }
                    Marshal.FreeHGlobal(pointer);
                }
            }

            private static byte ReadByteMember(object obj, string name)
            {
                var value = ReadMember(obj, name);
                if (value == null) return 0;
                return Convert.ToByte(value);
            }

            private static int ReadIntMember(object obj, string name)
            {
                var value = ReadMember(obj, name);
                if (value == null) return 0;
                return Convert.ToInt32(value);
            }

            private static string ReadStringMember(object obj, string name)
            {
                var value = ReadMember(obj, name);
                return value == null ? string.Empty : Convert.ToString(value);
            }

            private static object ReadMember(object obj, string name)
            {
                var type = obj.GetType();
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null) return field.GetValue(obj);
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(obj, null);
                return null;
            }

            private static byte ToByte(object value)
            {
                return value == null ? (byte)0 : Convert.ToByte(value);
            }

            private static int ToInt(object value)
            {
                return value == null ? 0 : Convert.ToInt32(value);
            }
        }

        private static class NativeMethods
        {
            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern void InitRFIDCallBack(RFIDCallBack callback, bool uidBack, int portHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int OpenNetPort(int port, string ipAddress, ref byte comAddress, ref int portHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int CloseNetPort(int portHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int OpenComPort(int port, ref byte comAddress, byte baud, ref int portHandle);


            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int CloseSpecComPort(int port);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int StopInventory(ref byte comAddress, int portHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int GetSeriaNo(ref byte comAddress, byte[] serialNo, int portHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int GetModuleVersion(ref byte comAddress, byte[] moduleVersion, int portHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int SetInventoryScanTime(ref byte comAddress, byte scanTime, int portHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int SetTIDParameter(ref byte comAddress, byte tidAddr, byte tidLen, int portHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int SetAntennaPower(ref byte comAddress, byte[] powerDbm, int length, int portHandle);

            [DllImport(DllName, EntryPoint = "SetAntennaMultiplexing", CallingConvention = CallingConvention.StdCall)]
            internal static extern int SetAntennaMultiplexing4(ref byte comAddress, byte antMask, int portHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
            internal static extern int Inventory_G2(
                ref byte comAddress,
                byte qValue,
                byte session,
                byte maskMem,
                byte[] maskAdr,
                byte maskLen,
                byte[] maskData,
                byte maskFlag,
                byte tidAddr,
                byte tidLen,
                byte tidFlag,
                byte target,
                byte inAnt,
                byte scanTime,
                byte fastFlag,
                byte[] epcList,
                ref byte antenna,
                ref int totalLen,
                ref int tagNum,
                int portHandle);
        }
    }
}
