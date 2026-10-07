using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using JMComicClient.Services;

namespace JMComicClient
{
    public partial class App : Application
    {
        public static ApiService Api { get; private set; }
        private Process _backendProcess;
        private string _extractedBackend;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Api = new ApiService("http://127.0.0.1:5000");
            TryStartBackend();
        }

        private void TryStartBackend()
        {
            // Check if backend is already running
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                if (http.GetAsync("http://127.0.0.1:5000/api/health").Result.IsSuccessStatusCode)
                    return;
            }
            catch { }

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
                    _extractedBackend = localExe;
            }

            // Start backend
            if (_extractedBackend != null && File.Exists(_extractedBackend))
            {
                _backendProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = _extractedBackend,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                // Wait for backend to be ready
                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(500);
                    try
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                        if (http.GetAsync("http://127.0.0.1:5000/api/health").Result.IsSuccessStatusCode)
                            return;
                    }
                    catch { }
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _backendProcess?.Kill();
                if (_extractedBackend != null && File.Exists(_extractedBackend))
                    File.Delete(_extractedBackend);
            }
            catch { }
            base.OnExit(e);
        }
    }
}
