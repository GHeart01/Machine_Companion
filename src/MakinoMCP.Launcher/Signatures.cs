#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MakinoMCP.Launcher
{
    // Whether an exe carries a valid Authenticode signature from SignPath Foundation for MakinoMCP. SignPath Foundation
    // signs many open-source projects with one certificate, so the product name has to be MakinoMCP too (SignPath only
    // lets this project sign files whose product name is MakinoMCP: .signpath/artifact-configuration.xml).
    // Revocation is checked from Windows' cache only, so no extra request goes to the certificate authority; a revoked
    // certificate fails, a revocation status Windows doesn't have cached doesn't.
    // Also compiled into the tests, so keep it to plain System APIs.
    internal static class Signatures
    {
        internal const string Signer = "SignPath Foundation";
        internal const string Product = "MakinoMCP";

        internal static bool IsSignPathSigned(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return false;
                if (!HasValidSignature(exePath)) return false;
                // The one API that reads the signer from a signed PE file; .NET 10 marks it obsolete in favour of
                // X509CertificateLoader, which can't read PE files. The tests compile this file on .NET 10.
#pragma warning disable SYSLIB0057
                var cert = X509Certificate.CreateFromSignedFile(exePath);
#pragma warning restore SYSLIB0057
                if (cert.Subject.IndexOf(Signer, StringComparison.OrdinalIgnoreCase) < 0) return false;
                return string.Equals(FileVersionInfo.GetVersionInfo(exePath).ProductName, Product, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
        private const int CertERevocationFailure = unchecked((int)0x800B010E); // revocation status unknown (offline)

        /// <summary>A valid embedded Authenticode signature, whoever signed it.</summary>
        internal static bool HasValidSignature(string path)
        {
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
            IntPtr filePtr = IntPtr.Zero;
            try
            {
                var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WintrustFileInfo)), pcwszFilePath = pathPtr };
                filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustFileInfo)));
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new WintrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WintrustData)),
                    dwUIChoice = 2,            // WTD_UI_NONE
                    fdwRevocationChecks = 1,   // WTD_REVOKE_WHOLECHAIN
                    dwUnionChoice = 1,         // WTD_CHOICE_FILE
                    pFile = filePtr,
                    dwStateAction = 1,         // WTD_STATEACTION_VERIFY
                    dwProvFlags = 0x1000,      // WTD_CACHE_ONLY_URL_RETRIEVAL
                };
                int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                data.dwStateAction = 2;        // WTD_STATEACTION_CLOSE
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result == 0 || result == CertERevocationFailure;
            }
            finally
            {
                if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);
    }
}
