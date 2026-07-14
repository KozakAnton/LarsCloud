using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LarsCloud.Infrastructure;
using LarsCloud.Models;

namespace LarsCloud.Services;

public sealed class TokenVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LarsCloud.TokenVault.v1");

    public async Task SaveAsync(AuthTokens tokens, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.SerializeToUtf8Bytes(tokens);
        var encrypted = Protect(json);
        var temp = AppPaths.TokenFile + ".tmp";
        await File.WriteAllBytesAsync(temp, encrypted, cancellationToken);
        File.Move(temp, AppPaths.TokenFile, true);
    }

    public async Task<AuthTokens?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(AppPaths.TokenFile)) return null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(AppPaths.TokenFile, cancellationToken);
            var json = Unprotect(encrypted);
            return JsonSerializer.Deserialize<AuthTokens>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or Win32Exception)
        {
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(AppPaths.TokenFile)) File.Delete(AppPaths.TokenFile);
    }

    private static byte[] Protect(byte[] plain) => Transform(plain, protect: true);
    private static byte[] Unprotect(byte[] encrypted) => Transform(encrypted, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is available only on Windows.");
        var inBlob = ToBlob(input);
        var entropyBlob = ToBlob(Entropy);
        try
        {
            DATA_BLOB outBlob;
            var success = protect
                ? CryptProtectData(ref inBlob, "Lar's Cloud Google OAuth", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0x1, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0x1, out outBlob);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entropyBlob.pbData);
        }
    }

    private static DATA_BLOB ToBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DATA_BLOB { cbData = bytes.Length, pbData = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? description,
        ref DATA_BLOB optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB pDataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr description,
        ref DATA_BLOB optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB pDataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
