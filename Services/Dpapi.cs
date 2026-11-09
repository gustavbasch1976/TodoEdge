using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TodoEdge
{
    /// <summary>Encrypts the API key with Windows DPAPI, so only your Windows user account can read it.</summary>
    internal static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static string? Protect(string? plain)
        {
            if (string.IsNullOrWhiteSpace(plain)) return null;
            byte[] result = Transform(Encoding.UTF8.GetBytes(plain.Trim()), protect: true);
            return Convert.ToBase64String(result);
        }

        public static string? Unprotect(string? protectedBase64)
        {
            if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
            try
            {
                byte[] result = Transform(Convert.FromBase64String(protectedBase64), protect: false);
                return Encoding.UTF8.GetString(result);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Transform(byte[] data, bool protect)
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
            var output = new DATA_BLOB();
            try
            {
                Marshal.Copy(data, 0, input.pbData, data.Length);
                bool ok = protect
                    ? CryptProtectData(ref input, "TodoEdge", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
    }
}
