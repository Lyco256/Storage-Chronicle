using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

#if STORAGE_CHRONICLE_TEST_BUILD
#pragma warning disable CA1507, CA1510, CA1872 // Keep this legacy-compatible source warning-clean in the modern test target.
#pragma warning disable CS8600 // The Windows PowerShell 5.1 CodeDom compiler has no nullable annotations for Win32 handle APIs.
#endif

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

        /// <summary>Returns whether a registry value name already exists, using Windows registry name comparison rules.</summary>
        public static bool ContainsRegistryValueName(IEnumerable<string> valueNames, string expectedName)
        {
            if (valueNames == null) throw new ArgumentNullException("valueNames");
            if (string.IsNullOrEmpty(expectedName)) throw new ArgumentException("A registry value name is required.", "expectedName");
            foreach (var valueName in valueNames)
            {
                if (string.Equals(valueName, expectedName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
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

    /// <summary>Holds a payload open against write, delete, or replacement while its verified path is consumed.</summary>
    public sealed class VerifiedPayloadLock : IDisposable
    {
        private readonly FileStream stream;
        private readonly List<SafeFileHandle> parentDirectoryHandles;
        private bool disposed;

        private VerifiedPayloadLock(string path, string sha256, FileStream payloadStream, List<SafeFileHandle> heldDirectories)
        {
            Path = path;
            Sha256 = sha256;
            stream = payloadStream;
            parentDirectoryHandles = heldDirectories;
        }

        /// <summary>Gets the canonical path whose file handle remains held.</summary>
        public string Path { get; private set; }

        /// <summary>Gets the SHA-256 computed from the held file handle.</summary>
        public string Sha256 { get; private set; }

        /// <summary>Opens a file with read-only sharing, hashes that same handle, and retains it until disposed.</summary>
        public static VerifiedPayloadLock OpenAndVerify(string path, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A payload path is required.", "path");
            if (!IsSha256(expectedSha256)) throw new ArgumentException("A 64-character hexadecimal SHA-256 is required.", "expectedSha256");

            var fullPath = System.IO.Path.GetFullPath(path);
            var heldDirectories = AcquireParentDirectoryHandles(fullPath);
            FileStream payloadStream = null;
            SafeFileHandle payloadHandle = null;
            try
            {
                payloadHandle = CreateFileW(fullPath, GenericRead, FileShareRead, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint | FileFlagSequentialScan, IntPtr.Zero);
                if (payloadHandle == null || payloadHandle.IsInvalid)
                    throw new IOException("Could not open the payload without write/delete sharing.", new Win32Exception(Marshal.GetLastWin32Error()));

                ByHandleFileInformation payloadInformation;
                if (!GetFileInformationByHandle(payloadHandle, out payloadInformation))
                    throw new IOException("Could not inspect the opened payload handle.", new Win32Exception(Marshal.GetLastWin32Error()));
                if ((payloadInformation.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                    throw new IOException("The verified payload is a directory or reparse point.");

                payloadStream = new FileStream(payloadHandle, FileAccess.Read, 4096, false);
                payloadHandle = null;
                string actualSha256;
                using (var sha256 = SHA256.Create())
                {
                    actualSha256 = ToHex(sha256.ComputeHash(payloadStream));
                }

                if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The payload bytes do not match the approved SHA-256.");

                payloadStream.Position = 0;
                return new VerifiedPayloadLock(fullPath, actualSha256, payloadStream, heldDirectories);
            }
            catch
            {
                if (payloadStream != null) payloadStream.Dispose();
                if (payloadHandle != null) payloadHandle.Dispose();
                DisposeHandles(heldDirectories);
                throw;
            }
        }

        /// <summary>Releases the held read handle. The caller must not do this while a consuming child may still use the path.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stream.Dispose();
            DisposeHandles(parentDirectoryHandles);
        }

        private static List<SafeFileHandle> AcquireParentDirectoryHandles(string fullPath)
        {
            var paths = new List<string>();
            var directory = System.IO.Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory)) throw new IOException("The payload has no parent directory.");
            var current = new DirectoryInfo(directory);
            while (current != null)
            {
                paths.Add(current.FullName);
                current = current.Parent;
            }
            paths.Reverse();

            var handles = new List<SafeFileHandle>();
            try
            {
                foreach (var directoryPath in paths)
                {
                    var handle = CreateFileW(directoryPath, FileReadAttributes, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
                    if (handle == null || handle.IsInvalid)
                    {
                        if (handle != null) handle.Dispose();
                        throw new IOException("Could not hold a parent directory against rename or reparse-point substitution: " + directoryPath, new Win32Exception(Marshal.GetLastWin32Error()));
                    }

                    ByHandleFileInformation information;
                    if (!GetFileInformationByHandle(handle, out information))
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw new IOException("Could not inspect a held parent directory: " + directoryPath, new Win32Exception(error));
                    }
                    if ((information.FileAttributes & FileAttributeDirectory) == 0 || (information.FileAttributes & FileAttributeReparsePoint) != 0)
                    {
                        handle.Dispose();
                        throw new IOException("A parent path component is not a plain directory: " + directoryPath);
                    }
                    handles.Add(handle);
                }
                return handles;
            }
            catch
            {
                DisposeHandles(handles);
                throw;
            }
        }

        private static void DisposeHandles(List<SafeFileHandle> handles)
        {
            for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
            handles.Clear();
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F'))) return false;
            }
            return true;
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private const uint GenericRead = 0x80000000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle fileHandle, out ByHandleFileInformation information);
    }

    /// <summary>Tracks a timed-out child until its exact process handle proves terminal completion.</summary>
    public sealed class InstallerChildCompletionState
    {
        /// <summary>Gets whether the bounded wait expired.</summary>
        public bool TimedOut { get; private set; }

        /// <summary>Gets whether the exact child process handle has proved terminal state.</summary>
        public bool TerminalProven { get; private set; }

        /// <summary>Gets the exit code after terminal state has been proven.</summary>
        public int? ExitCode { get; private set; }

        /// <summary>Marks a bounded wait timeout without asserting that the child has terminated.</summary>
        public void MarkTimedOut()
        {
            if (TerminalProven) throw new InvalidOperationException("A terminal child cannot subsequently time out.");
            TimedOut = true;
        }

        /// <summary>Records terminal state only when the caller observed the exact process handle signaled.</summary>
        public bool TryProveTerminal(bool exactProcessHandleSignaled, int exitCode)
        {
            if (!exactProcessHandleSignaled) return false;
            TerminalProven = true;
            ExitCode = exitCode;
            return true;
        }

        /// <summary>Indicates that a case timed out and must remain recorded as indeterminate.</summary>
        public bool MustRemainIndeterminate { get { return TimedOut; } }
    }
}
