using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using Il2CppInterop.Runtime;

namespace OldenPerf;

// Native entry address of a declared managed method, read from the Il2Cpp MethodInfo that il2cpp_class_get_methods
// returns. The exported name accessor avoids version-dependent MethodInfo name offsets. The name is compared with the managed name, and a
// name that occurs more than once in the class is ambiguous. Any failure returns 0 (unknown), and callers fail closed.
// il2cpp_method_get_pointer is not exported by this build, so the entry is read from the MethodInfo directly.
static class NativeIdentity
{
    static ClassGetMethods _getMethods;
    static MethodGetName _getName;
    static bool _resolved;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr ClassGetMethods(IntPtr klass, ref IntPtr iter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr MethodGetName(IntPtr method);

    static ClassGetMethods Methods()
    {
        if (_resolved) return _getMethods;
        _resolved = true;
        var module = NativeLibrary.Load(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
        var export = NativeLibrary.GetExport(module, "il2cpp_class_get_methods");
        _getName = Marshal.GetDelegateForFunctionPointer<MethodGetName>(NativeLibrary.GetExport(module, "il2cpp_method_get_name"));
        _getMethods = Marshal.GetDelegateForFunctionPointer<ClassGetMethods>(export);
        return _getMethods;
    }

    // Returns the entry of the single method called name declared on type, or 0.
    public static long EntryOf(Type type, string name)
    {
        try
        {
            var get = Methods();
            var klassField = typeof(Il2CppClassPointerStore<>).MakeGenericType(type).GetField("NativeClassPtr", BindingFlags.Public | BindingFlags.Static);
            var klass = klassField == null ? IntPtr.Zero : (IntPtr)klassField.GetValue(null);
            if (get == null || klass == IntPtr.Zero) return 0;
            long entry = 0;
            int hits = 0;
            IntPtr iter = IntPtr.Zero;
            for (var mi = get(klass, ref iter); mi != IntPtr.Zero; mi = get(klass, ref iter))
            {
                var namePtr = _getName(mi);
                if (namePtr == IntPtr.Zero || Marshal.PtrToStringAnsi(namePtr) != name) continue;
                hits++;
                entry = Marshal.ReadInt64(mi, 0);
            }
            return hits == 1 ? entry : 0;
        }
        catch (Exception) { return 0; }
    }
}
