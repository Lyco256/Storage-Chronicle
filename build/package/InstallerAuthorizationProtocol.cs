using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

#pragma warning disable CA1507, CA1510 // Keep source compilable by the Windows PowerShell 5.1 .NET Framework compiler.

namespace StorageChronicle.InstallerAuthorization
{
    /// <summary>Windows PowerShell 5.1-compatible primitives shared by the physical installer broker and tests.</summary>
    public static class InstallerAuthorizationProtocol
    {
        /// <summary>Generates a 256-bit cryptographically random URL-safe token.</summary>
        public static string CreateToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var result = new char[bytes.Length * 2];
            const string alphabet = "0123456789ABCDEF";
            for (var index = 0; index < bytes.Length; index++)
            {
                result[index * 2] = alphabet[bytes[index] >> 4];
                result[(index * 2) + 1] = alphabet[bytes[index] & 15];
            }
            return new string(result);
        }

        /// <summary>Validates all Hello fields against the exact RunAs child and case context.</summary>
        public static bool ValidateHello(string schema, string runId, string caseId, string computerName, int processId, int parentProcessId, string authorizationNonce, string hashManifestSha256,
            string expectedRunId, string expectedCaseId, string expectedComputerName, int expectedProcessId, int expectedParentProcessId, string expectedAuthorizationNonce, string expectedHashManifestSha256)
        {
            return string.Equals(schema, "StorageChronicle.InstallerCaseAuthorizationHello.v1", StringComparison.Ordinal) &&
                string.Equals(runId, expectedRunId, StringComparison.Ordinal) &&
                string.Equals(caseId, expectedCaseId, StringComparison.Ordinal) &&
                string.Equals(computerName, expectedComputerName, StringComparison.OrdinalIgnoreCase) &&
                processId == expectedProcessId && parentProcessId == expectedParentProcessId &&
                string.Equals(authorizationNonce, expectedAuthorizationNonce, StringComparison.Ordinal) &&
                FixedHashEquals(hashManifestSha256, expectedHashManifestSha256);
        }

        /// <summary>Returns the kernel-reported process ID connected to a named-pipe server.</summary>
        public static uint GetClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipeHandle)
        {
            if (pipeHandle == null) throw new ArgumentNullException("pipeHandle");
            uint processId;
            if (!GetNamedPipeClientProcessId(pipeHandle.DangerousGetHandle(), out processId))
                throw new IOException("Could not establish named-pipe client process identity.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            return processId;
        }

        /// <summary>Returns NOT_EXECUTED only for Windows ShellExecute's UAC-cancel error code 1223.</summary>
        public static string MapLaunchFailure(int nativeErrorCode)
        {
            return nativeErrorCode == 1223 ? "NOT_EXECUTED" : "FAILED";
        }

        private static bool FixedHashEquals(string actual, string expected)
        {
            if (actual == null || expected == null || actual.Length != 64 || expected.Length != 64) return false;
            byte[] actualBytes;
            byte[] expectedBytes;
            try
            {
                actualBytes = FromHex(actual);
                expectedBytes = FromHex(expected);
            }
            catch (FormatException)
            {
                return false;
            }

            var difference = 0;
            for (var index = 0; index < actualBytes.Length; index++) difference |= actualBytes[index] ^ expectedBytes[index];
            return difference == 0;
        }

        private static byte[] FromHex(string value)
        {
            var bytes = new byte[value.Length / 2];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = Convert.ToByte(value.Substring(index * 2, 2), 16);
            return bytes;
        }

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
    }

    /// <summary>Rejects a mismatched or replayed nonce for a single case authorization exchange.</summary>
    public sealed class OneTimeNonce
    {
        private readonly string expectedNonce;
        private int consumed;

        /// <summary>Creates a nonce validator for exactly one case.</summary>
        public OneTimeNonce(string nonce)
        {
            if (string.IsNullOrEmpty(nonce)) throw new ArgumentException("A nonce is required.", "nonce");
            expectedNonce = nonce;
        }

        /// <summary>Consumes the matching nonce at most once.</summary>
        public bool TryConsume(string candidate)
        {
            if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0) return false;
            return string.Equals(candidate, expectedNonce, StringComparison.Ordinal);
        }
    }
}
