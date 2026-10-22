$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class SharmShaderCompiler {
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int D3DCompile(byte[] source, UIntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target, uint flags, uint flags2, out IntPtr code, out IntPtr errors);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr BufferPointer(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate UIntPtr BufferSize(IntPtr self);
    static byte[] ReadBlob(IntPtr blob) {
        var table = Marshal.ReadIntPtr(blob);
        var pointer = Marshal.GetDelegateForFunctionPointer<BufferPointer>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size))(blob);
        var size = Marshal.GetDelegateForFunctionPointer<BufferSize>(Marshal.ReadIntPtr(table, 4 * IntPtr.Size))(blob);
        byte[] bytes = new byte[(int)size.ToUInt64()]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes;
    }
    public static void Compile(string source, string destination) {
        byte[] bytes = File.ReadAllBytes(source); IntPtr code, errors;
        int result = D3DCompile(bytes, (UIntPtr)bytes.Length, source, IntPtr.Zero, IntPtr.Zero, "main", "ps_3_0", 0x8000, 0, out code, out errors);
        try {
            if (result < 0) throw new Exception(errors == IntPtr.Zero ? "Shader compilation failed" : System.Text.Encoding.UTF8.GetString(ReadBlob(errors)));
            File.WriteAllBytes(destination, ReadBlob(code));
        } finally { if(code != IntPtr.Zero) Marshal.Release(code); if(errors != IntPtr.Zero) Marshal.Release(errors); }
    }
}
'@
[SharmShaderCompiler]::Compile("$PSScriptRoot\Assets\Glass.hlsl", "$PSScriptRoot\Assets\Glass.ps")
