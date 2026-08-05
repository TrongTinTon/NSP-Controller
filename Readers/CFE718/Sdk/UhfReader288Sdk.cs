using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NSPGatekeeper.Controller.Readers.CFE718.Sdk
{
    /// <summary>
    /// Loads the vendor C# SDK assembly described by "UHFReader288.DLL manual V2.1".
    /// Both x86 and x64 packages are managed .NET assemblies copied beside the executable.
    /// </summary>
    internal static class UhfReader288Sdk
    {
        private const string DllName = "UHFReader288.dll";
        private static readonly object Gate = new object();
        private static Assembly _assembly;
        private static Type _readerType;

        internal static UhfReader288Session CreateSession()
        {
            EnsureLoaded();
            return new UhfReader288Session(_readerType);
        }

        internal static string DescribeRuntime()
        {
            var path = AssemblyPath;
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

            return "mode=managed-csharp-sdk"
                + "; architecture=" + (IntPtr.Size == 8 ? "x64" : "x86")
                + "; path=" + path
                + "; exists=" + file.Exists
                + "; size=" + (file.Exists ? file.Length.ToString() : "0")
                + "; file_version=" + (string.IsNullOrWhiteSpace(version) ? "<unknown>" : version)
                + "; reader_type=" + (_readerType == null ? "<not-loaded>" : _readerType.FullName);
        }

        private static string AssemblyPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DllName); }
        }

        private static void EnsureLoaded()
        {
            if (_assembly != null && _readerType != null) return;

            lock (Gate)
            {
                if (_assembly != null && _readerType != null) return;

                var path = AssemblyPath;
                if (!File.Exists(path))
                    throw new DllNotFoundException("Cannot find managed UHFReader288 SDK beside the executable: " + path);

                _assembly = Assembly.LoadFrom(path);
                _readerType = FindReaderType(_assembly);
                if (_readerType == null)
                    throw new TypeLoadException("Cannot find a UHFReader288 reader API type in " + path);
            }
        }

        private static Type FindReaderType(Assembly assembly)
        {
            var preferredNames = new[]
            {
                "UHF.UHFReader",
                "UHFReaderModule.UHFReader",
                "UHFReaderModule.Reader",
                "UHFReader"
            };

            foreach (var name in preferredNames)
            {
                var exact = assembly.GetType(name, false);
                if (IsReaderApiType(exact)) return exact;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types == null ? new Type[0] : ex.Types.Where(item => item != null).ToArray();
            }

            return types.FirstOrDefault(IsReaderApiType);
        }

        private static bool IsReaderApiType(Type type)
        {
            if (type == null || type.IsAbstract || type.IsInterface) return false;
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            return methods.Any(item => item.Name == "OpenComPort" && item.GetParameters().Length == 3)
                && methods.Any(item => item.Name == "GetSeriaNo" && item.GetParameters().Length == 2)
                && methods.Any(item =>
                {
                    if (item.Name != "SetRfPower") return false;
                    var parameters = item.GetParameters();
                    if (parameters.Length != 2) return false;
                    var powerType = parameters[1].ParameterType;
                    if (powerType.IsByRef) powerType = powerType.GetElementType();
                    return powerType == typeof(byte);
                })
                && methods.Any(item => item.Name == "Inventory_G2" && item.GetParameters().Length == 19);
        }
    }
}
