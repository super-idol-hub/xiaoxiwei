using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace XiaoXiWei.Standalone
{
    internal sealed class PortableRemoteAssistance : IDisposable
    {
        private const string RustDeskVersion = "1.4.9";
        private const string RustDeskSha256 =
            "EAEDEB0088E687BF46F7C46A9C6EA5493CE51F3134DFD6ACBEDB47B5B9136274";
        private const string ExecutableResource =
            "XiaoXiWei.Standalone.Support.RustDesk.exe";
        private const string LicenseResource =
            "XiaoXiWei.Standalone.Support.RustDesk.LICENSE.txt";
        private const string NoticeResource =
            "XiaoXiWei.Standalone.Support.RustDesk.NOTICE.txt";

        private Process _process;
        private string _executablePath;

        public event EventHandler Exited;

        public bool IsRunning
        {
            get
            {
                if (_process == null)
                {
                    return false;
                }

                try
                {
                    return !_process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public string ExecutablePath
        {
            get { return _executablePath ?? string.Empty; }
        }

        public void Start()
        {
            if (IsRunning)
            {
                TryBringToFront();
                return;
            }

            _executablePath = EnsureSupportFiles();
            ProcessStartInfo startInfo =
                new ProcessStartInfo(_executablePath);
            startInfo.UseShellExecute = true;
            startInfo.WorkingDirectory =
                Path.GetDirectoryName(_executablePath);

            Process process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException(
                    "无法启动内置的 RustDesk 远程协助组件。");
            }

            _process = process;
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
        }

        public void Stop()
        {
            Process activeProcess = _process;
            _process = null;

            if (activeProcess != null)
            {
                TryStopProcess(activeProcess);
                activeProcess.Dispose();
            }

            foreach (Process process in FindOwnedProcesses())
            {
                TryStopProcess(process);
                process.Dispose();
            }

            RaiseExited();
        }

        public void TryBringToFront()
        {
            Process process = _process;
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(process.MainWindowHandle, 9);
                    NativeMethods.SetForegroundWindow(process.MainWindowHandle);
                }
            }
            catch
            {
                // The helper can exit while the window is being activated.
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private string EnsureSupportFiles()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Anbunensi",
                "XiaoXiWeiPet",
                "Support",
                "RustDesk",
                RustDeskVersion);
            Directory.CreateDirectory(directory);

            string executablePath =
                Path.Combine(directory, "XiaoXiWeiSupport.exe");
            if (!File.Exists(executablePath)
                || !HashMatches(executablePath, RustDeskSha256))
            {
                WriteEmbeddedFile(
                    ExecutableResource,
                    executablePath,
                    RustDeskSha256);
            }

            WriteEmbeddedTextIfMissing(
                LicenseResource,
                Path.Combine(directory, "LICENSE-RustDesk-AGPL-3.0.txt"));
            WriteEmbeddedTextIfMissing(
                NoticeResource,
                Path.Combine(directory, "THIRD-PARTY-NOTICES.txt"));
            return executablePath;
        }

        private static void WriteEmbeddedFile(
            string resourceName,
            string targetPath,
            string expectedSha256)
        {
            string temporaryPath = targetPath + ".extracting";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            using (Stream input = OpenResource(resourceName))
            using (FileStream output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                input.CopyTo(output);
            }

            if (!HashMatches(temporaryPath, expectedSha256))
            {
                File.Delete(temporaryPath);
                throw new InvalidDataException(
                    "内置 RustDesk 组件完整性校验失败，已停止启动。");
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            File.Move(temporaryPath, targetPath);
        }

        private static void WriteEmbeddedTextIfMissing(
            string resourceName,
            string targetPath)
        {
            if (File.Exists(targetPath))
            {
                return;
            }

            using (Stream input = OpenResource(resourceName))
            using (FileStream output = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read))
            {
                input.CopyTo(output);
            }
        }

        private static Stream OpenResource(string resourceName)
        {
            Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidDataException(
                    "缺少内置远程协助资源：" + resourceName);
            }
            return stream;
        }

        private static bool HashMatches(
            string filePath,
            string expectedSha256)
        {
            try
            {
                using (SHA256 algorithm = SHA256.Create())
                using (FileStream stream = File.OpenRead(filePath))
                {
                    byte[] hash = algorithm.ComputeHash(stream);
                    return string.Equals(
                        ToHex(hash),
                        expectedSha256,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ToHex(byte[] bytes)
        {
            char[] characters = new char[bytes.Length * 2];
            const string alphabet = "0123456789ABCDEF";
            for (int index = 0; index < bytes.Length; index++)
            {
                characters[index * 2] =
                    alphabet[(bytes[index] >> 4) & 0x0F];
                characters[index * 2 + 1] =
                    alphabet[bytes[index] & 0x0F];
            }
            return new string(characters);
        }

        private List<Process> FindOwnedProcesses()
        {
            List<Process> owned = new List<Process>();
            if (string.IsNullOrWhiteSpace(_executablePath))
            {
                return owned;
            }

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.MainModule != null
                        && string.Equals(
                            Path.GetFullPath(process.MainModule.FileName),
                            Path.GetFullPath(_executablePath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        owned.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                    process.Dispose();
                }
            }
            return owned;
        }

        private static void TryStopProcess(Process process)
        {
            try
            {
                if (process.HasExited)
                {
                    return;
                }

                process.CloseMainWindow();
                if (!process.WaitForExit(1200))
                {
                    process.Kill();
                    process.WaitForExit(1500);
                }
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch
                {
                    // The process already ended or belongs to another session.
                }
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            RaiseExited();
        }

        private void RaiseExited()
        {
            EventHandler handler = Exited;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern bool SetForegroundWindow(IntPtr window);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern bool ShowWindow(
                IntPtr window,
                int command);
        }
    }
}
