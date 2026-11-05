// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace ProxmoxSpice;

internal static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
    public static string Protect(string secret) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(secret), true));
    public static string Unprotect(string secret) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(secret), false));
    static byte[] Transform(byte[] input, bool encrypt)
    {
        var source = new Blob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Blob result = default;
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            var ok = encrypt ? CryptProtectData(ref source, "Proxmox SPICE API token", 0, 0, 0, 1, out result) : CryptUnprotectData(ref source, 0, 0, 0, 0, 1, out result);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not process the token secret. Enter it again.");
            var output = new byte[result.Length]; Marshal.Copy(result.Data, output, 0, output.Length); return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            for (int i = 0; i < source.Length; i++) Marshal.WriteByte(source.Data, i, 0);
            Marshal.FreeHGlobal(source.Data);
            if (result.Data != 0) { for (int i = 0; i < result.Length; i++) Marshal.WriteByte(result.Data, i, 0); LocalFree(result.Data); }
        }
    }
}


