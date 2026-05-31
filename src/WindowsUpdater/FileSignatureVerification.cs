using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WindowsUpdater;

public sealed record FileSignatureVerificationResult(bool IsTrusted, string? Publisher = null, string? Reason = null)
{
    public static FileSignatureVerificationResult Trusted(string? publisher = null)
    {
        return new FileSignatureVerificationResult(true, publisher);
    }

    public static FileSignatureVerificationResult Untrusted(string reason)
    {
        return new FileSignatureVerificationResult(false, Reason: reason);
    }
}

public interface IFileSignatureVerifier
{
    Task<FileSignatureVerificationResult> VerifyAsync(
        string path,
        string? expectedPublisher,
        CancellationToken cancellationToken = default);
}

public sealed class PlatformFileSignatureVerifier : IFileSignatureVerifier
{
    public Task<FileSignatureVerificationResult> VerifyAsync(
        string path,
        string? expectedPublisher,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(FileSignatureVerificationResult.Untrusted(
                "Authenticode verification requires Windows WinVerifyTrust."));
        }

        var trustResult = WinTrust.VerifyEmbeddedSignature(path);
        if (!trustResult.IsTrusted)
        {
            return Task.FromResult(trustResult);
        }

        if (string.IsNullOrWhiteSpace(expectedPublisher))
        {
            return Task.FromResult(trustResult);
        }

        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var publisher = certificate.Subject;
            return publisher.Contains(expectedPublisher, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(FileSignatureVerificationResult.Trusted(publisher))
                : Task.FromResult(FileSignatureVerificationResult.Untrusted(
                    $"Publisher '{publisher}' does not match expected publisher '{expectedPublisher}'."));
        }
        catch (Exception error)
        {
            return Task.FromResult(FileSignatureVerificationResult.Untrusted(error.Message));
        }
    }
}

internal static class WinTrust
{
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static FileSignatureVerificationResult VerifyEmbeddedSignature(string filePath)
    {
        var filePathPointer = IntPtr.Zero;
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePathPointer
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UIChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                File = fileInfoPointer,
                StateAction = 0,
                ProvFlags = 0x00000020,
                UIContext = 0
            };

            var result = WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, ref data);
            return result == 0
                ? FileSignatureVerificationResult.Trusted()
                : FileSignatureVerificationResult.Untrusted($"WinVerifyTrust failed with 0x{result:X8}.");
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            if (filePathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(filePathPointer);
            }
        }
    }

    [DllImport("wintrust.dll", PreserveSig = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionId,
        ref WinTrustData pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public uint ProvFlags;
        public uint UIContext;
    }
}
