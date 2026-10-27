using System;
using System.Runtime.InteropServices;

namespace OldenPerf;

// The verified 64-bit Unity NativeArray layout: data pointer at 0, signed length at +8.
// Reads only the embedded header. The caller validates its owning class/field offset and capacity.
static class NativeBatchArray
{
    public static bool TryRead(IntPtr owner, int offset, int capacity, out IntPtr buffer, out int length)
    {
        buffer = IntPtr.Zero; length = 0;
        if (IntPtr.Size != 8 || owner == IntPtr.Zero || offset < 16 || capacity <= 0) return false;
        buffer = Marshal.ReadIntPtr(owner, offset);
        length = Marshal.ReadInt32(owner, offset + IntPtr.Size);
        return buffer != IntPtr.Zero && length > 0 && length <= capacity;
    }
}
