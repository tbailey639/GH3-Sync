using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace GH3_Sync;

public static class GpuAccelerationService
{
    public static string GpuName { get; private set; } = "Detecting GPU...";
    public static string GpuDriverVersion { get; private set; } = "";
    public static int GpuMemoryMb { get; private set; } = 0;
    public static bool IsCudaAvailable { get; private set; } = false;
    public static bool IsNvidia { get; private set; } = false;
    public static int WpfRenderTier { get; private set; } = 2; // Default assume modern hardware
    private static bool _initialized = false;

    public static void ApplyRenderMode(bool enableGpu)
    {
        try
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = enableGpu
                ? System.Windows.Interop.RenderMode.Default
                : System.Windows.Interop.RenderMode.SoftwareOnly;
        }
        catch { }
    }

    public static void Initialize(SyncLogger? logger = null)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // 1. Check WPF Direct3D Hardware Rendering Tier
            if (OperatingSystem.IsWindows())
            {
                int tierValue = (RenderCapability.Tier >> 16);
                WpfRenderTier = tierValue;
            }
        }
        catch { }

        // 2. Query GPU via nvidia-smi first (fastest and most accurate for CUDA)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,driver_version,memory.total --format=csv,noheader",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(2500);

                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    // Format: NVIDIA GeForce RTX 5070, 621.14, 12227 MiB
                    var parts = output.Split(',');
                    if (parts.Length >= 1) GpuName = parts[0].Trim();
                    if (parts.Length >= 2) GpuDriverVersion = parts[1].Trim();
                    if (parts.Length >= 3)
                    {
                        var memPart = parts[2].Trim().Replace("MiB", "", StringComparison.OrdinalIgnoreCase).Trim();
                        if (int.TryParse(memPart, out var mb)) GpuMemoryMb = mb;
                    }

                    IsNvidia = true;
                    IsCudaAvailable = true;
                    logger?.LogInfo($"[GPU ACCELERATION] Detected {GpuName} (Driver {GpuDriverVersion}, {GpuMemoryMb} MB VRAM, CUDA Active). WPF Tier {WpfRenderTier}.");
                    return;
                }
            }
        }
        catch { }

        // 3. Registry fallback for AMD Radeon / Intel Arc
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
            if (key != null)
            {
                var desc = key.GetValue("DriverDesc") as string;
                var ver = key.GetValue("DriverVersion") as string;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    GpuName = desc.Trim();
                    GpuDriverVersion = ver ?? "";
                    IsNvidia = GpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                    logger?.LogInfo($"[GPU ACCELERATION] Detected display adapter {GpuName} (WPF Hardware Tier {WpfRenderTier}).");
                    return;
                }
            }
        }
        catch { }

        GpuName = "Universal Direct3D Display Adapter";
        logger?.LogInfo($"[GPU ACCELERATION] Standard display adapter active. WPF Tier {WpfRenderTier}.");
    }

    private static bool? _isPyTorchCudaReady;

    public static bool VerifyPyTorchCudaSupport(string? pythonExe = null, bool forceRecheck = false)
    {
        if (!forceRecheck && _isPyTorchCudaReady.HasValue) return _isPyTorchCudaReady.Value;

        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
        {
            var defaultPy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "tools", "ai", "python", "python.exe");
            if (File.Exists(defaultPy)) pythonExe = defaultPy;
            else
            {
                _isPyTorchCudaReady = false;
                return false;
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "-c \"import torch; x = torch.zeros(1, device='cuda'); print('CUDA_OK')\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) { _isPyTorchCudaReady = false; return false; }
            var outText = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(4000);
            _isPyTorchCudaReady = outText.Contains("CUDA_OK");
            return _isPyTorchCudaReady.Value;
        }
        catch
        {
            _isPyTorchCudaReady = false;
            return false;
        }
    }

    public static string GetDemucsDeviceArgs(string? pythonExe = null)
    {
        bool gpuEnabled = AppConfig.Current.StemSeparationGpuEnabled;
        if (gpuEnabled && IsNvidia && VerifyPyTorchCudaSupport(pythonExe))
        {
            return "-d cuda";
        }
        return "-d cpu";
    }

    public static string GetStatusBadge()
    {
        bool cudaReady = IsNvidia && VerifyPyTorchCudaSupport();
        if (cudaReady && AppConfig.Current.StemSeparationGpuEnabled)
        {
            return $"⚡ GPU Accelerated: {GpuName} (CUDA Active • {GpuMemoryMb:N0} MB VRAM • D3D Tier {WpfRenderTier})";
        }
        if (WpfRenderTier >= 2)
        {
            return $"⚡ UI Hardware Accelerated: {GpuName} (Direct3D Tier 2)";
        }
        return "Universal CPU Processing (Software Mode)";
    }
}
