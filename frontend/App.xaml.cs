using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using JMComicClient.Services;

namespace JMComicClient
{
    public partial class App : Application
    {
        public static ApiService Api { get; private set; }
        public static Task<bool> BackendReady { get; private set; }
        private Process _backendProcess;
        private string _extractedBackend;
        private string _apiToken;
        private bool _ownsExtractedBackend;

        protected override void OnStartup(StartupEventArgs e)
        {
            _apiToken = Environment.GetEnvironmentVariable("JMCOMIC_API_TOKEN");
            if (string.IsNullOrWhiteSpace(_apiToken))
            {
                _apiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                Environment.SetEnvironmentVariable("JMCOMIC_API_TOKEN", _apiToken);
            }

            Api = new ApiService("http://127.0.0.1:5000", _apiToken);
            BackendReady = TryStartBackendAsync();
            base.OnStartup(e);
        }

        private async Task<bool> TryStartBackendAsync()
        {
            // Check if backend is already running
            if (await Api.HealthCheckAsync())
                return true;

            // Try embedded resource first
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "JMComicBackend.exe";
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "JMComic");
                    Directory.CreateDirectory(tempDir);
                    _extractedBackend = Path.Combine(tempDir, "JMComicBackend.exe");
                    _ownsExtractedBackend = true;

                    // Only extract if not already there or size differs
                    if (!File.Exists(_extractedBackend) || new FileInfo(_extractedBackend).Length != stream.Length)
                    {
                        using var fs = File.Create(_extractedBackend);
                        stream.CopyTo(fs);
                    }
                }
            }

            // If no embedded resource, try exe in same directory
            if (_extractedBackend == null || !File.Exists(_extractedBackend))
            {
                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                var localExe = Path.Combine(appDir, "JMComicBackend.exe");
                if (File.Exists(localExe))
                {
                    _extractedBackend = localExe;
                    _ownsExtractedBackend = false;
                }
            }

            // Start backend
            if (_extractedBackend == null || !File.Exists(_extractedBackend))
                return false;

            try
            {
                _backendProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = _extractedBackend,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    Environment = { ["JMCOMIC_API_TOKEN"] = _apiToken },
                });
            }
            catch
            {
                return false;
            }

            // Wait for backend to be ready without blocking the UI thread.
            for (int i = 0; i < 30; i++)
            {
                if (_backendProcess.HasExited)
                    return false;
                if (await Api.HealthCheckAsync())
                    return true;
                await Task.Delay(500);
            }

            try
            {
                if (!_backendProcess.HasExited)
                    _backendProcess.Kill(entireProcessTree: true);
            }
            catch { }
            return false;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_backendProcess != null && !_backendProcess.HasExited)
                    _backendProcess.Kill(entireProcessTree: true);
                if (_ownsExtractedBackend && _extractedBackend != null && File.Exists(_extractedBackend))
                {
                    File.Delete(_extractedBackend);
                }
            }
            catch { }
            base.OnExit(e);
        }
    }
}
