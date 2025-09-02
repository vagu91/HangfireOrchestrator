using HangfireOrchestrator.Models;
using System.Diagnostics;
using System.Text.Json;

namespace HangfireOrchestrator.Services;

public interface IWorkloadExecutorService
{
    Task<Dictionary<string, object>> ExecuteWorkloadAsync(WorkloadType workloadType, Dictionary<string, object>? parameters = null);
    Task<bool> IsExecutableAvailable(WorkloadType workloadType);
    string GetExecutablePath(WorkloadType workloadType);
}

public class WorkloadExecutorService : IWorkloadExecutorService
{
    private readonly ILogger<WorkloadExecutorService> _logger;
    private readonly IConfiguration _configuration;

    private readonly Dictionary<WorkloadType, string> _executableMapping = new()
    {
        [WorkloadType.Setup] = "Executors.SetupWorkload.exe",
        [WorkloadType.PreparazioneGenerazioneContratti] = "Executors.PreparazioneGenerazioneContrattiWorkload.exe",
        [WorkloadType.GeneraContratti] = "Executors.GeneraContrattiWorkload.exe",
        [WorkloadType.InizioFirmaMassiva] = "Executors.InizioFirmaMassivaWorkload.exe",
        [WorkloadType.FinalizzazioneFirmaMassiva] = "Executors.FinalizzazioneFirmaMassivaWorkload.exe",
        [WorkloadType.PreparazioneFirmaVolontari] = "Executors.PreparazioneFirmaVolontariWorkload.exe",
        [WorkloadType.InizioFirmaVolontari] = "Executors.InizioFirmaVolontariWorkload.exe",
        [WorkloadType.FinalizzazioneFirmaVolontari] = "Executors.FinalizzazioneFirmaVolontariWorkload.exe",
        [WorkloadType.ChiusuraFaseDiFirmaDigitale] = "Executors.ChiusuraFaseDiFirmaDigitale.exe",
        [WorkloadType.PreparazioneFirmaEnte] = "Executors.PreparazioneFirmaEnteWorkload.exe",
        [WorkloadType.InizioFirmaEnti] = "Executors.InizioFirmaEnteWorkload.exe",
        [WorkloadType.FinalizzazioneFirmaEnti] = "Executors.FinalizzazioneFirmaEnteWorkload.exe",
        [WorkloadType.FinalizzazioneWorkflow] = "Executors.FinalizzazioneWorkflowWorkload.exe",
        [WorkloadType.ContrattiCleanup] = "Executors.ContrattiCleanupWorkload.exe"
    };

    public WorkloadExecutorService(ILogger<WorkloadExecutorService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<Dictionary<string, object>> ExecuteWorkloadAsync(WorkloadType workloadType, Dictionary<string, object>? parameters = null)
    {
        var executablePath = GetExecutablePath(workloadType);

        if (!await IsExecutableAvailable(workloadType))
        {
            throw new FileNotFoundException($"Executable not found for workload {workloadType}: {executablePath}");
        }

        var result = new Dictionary<string, object>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Starting execution of {WorkloadType} at {ExecutablePath}", workloadType, executablePath);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)
            };

            // Aggiungi parametri come variabili d'ambiente se necessario
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    var envVarName = $"WORKLOAD_{param.Key.ToUpper()}";
                    var envVarValue = param.Value?.ToString() ?? string.Empty;
                    processStartInfo.EnvironmentVariables[envVarName] = envVarValue;
                    _logger.LogDebug("Added environment variable: {EnvVar} = {Value}", envVarName, envVarValue);
                }
            }

            using var process = new Process { StartInfo = processStartInfo };

            var outputData = new List<string>();
            var errorData = new List<string>();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputData.Add(e.Data);
                    _logger.LogInformation("[{WorkloadType}] {Output}", workloadType, e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorData.Add(e.Data);
                    _logger.LogError("[{WorkloadType}] {Error}", workloadType, e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Timeout configurabile
            var timeoutMinutes = _configuration.GetValue("WorkloadExecution:TimeoutMinutes", 60);
            var timeout = TimeSpan.FromMinutes(timeoutMinutes);

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                _logger.LogWarning("Process {WorkloadType} timed out after {Timeout} minutes. Attempting to kill.", workloadType, timeoutMinutes);
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"Process {workloadType} timed out after {timeout}");
            }

            stopwatch.Stop();

            result["ExitCode"] = process.ExitCode;
            result["ExecutionTimeMs"] = stopwatch.ElapsedMilliseconds;
            result["Output"] = outputData;
            result["Errors"] = errorData;
            result["Success"] = process.ExitCode == 0;

            if (process.ExitCode != 0)
            {
                var errorMessage = $"Process {workloadType} exited with code {process.ExitCode}";
                _logger.LogError(errorMessage);
                result["ErrorMessage"] = errorMessage;
                throw new InvalidOperationException(errorMessage);
            }

            _logger.LogInformation("Successfully completed {WorkloadType} in {ElapsedMs}ms", workloadType, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed to execute workload {WorkloadType} after {ElapsedMs}ms", workloadType, stopwatch.ElapsedMilliseconds);

            result["Success"] = false;
            result["ErrorMessage"] = ex.Message;
            result["ExecutionTimeMs"] = stopwatch.ElapsedMilliseconds;

            throw;
        }
    }

    public Task<bool> IsExecutableAvailable(WorkloadType workloadType)
    {
        try
        {
            var executablePath = GetExecutablePath(workloadType);
            return Task.FromResult(File.Exists(executablePath));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public string GetExecutablePath(WorkloadType workloadType)
    {
        if (!_executableMapping.TryGetValue(workloadType, out var executableName))
        {
            throw new NotSupportedException($"Workload type {workloadType} is not supported");
        }

        var baseDirectory = _configuration.GetValue("WorkloadExecution:ExecutablesPath", "Executables");

        if (!Path.IsPathRooted(baseDirectory))
        {
            baseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, baseDirectory);
        }

        return Path.Combine(baseDirectory, executableName);
    }
}