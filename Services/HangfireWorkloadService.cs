using Hangfire;
using Hangfire.Server;
using HangfireOrchestrator.Models;
using Serilog.Context;

namespace HangfireOrchestrator.Services;

public interface IHangfireWorkloadService
{
    string EnqueueWorkload(WorkloadExecutionRequest request);
    string ScheduleWorkload(WorkloadExecutionRequest request);
    string AddRecurringWorkload(WorkloadExecutionRequest request);
    string ExecutePipeline(WorkflowPipelineRequest pipelineRequest);
    JobStatusResponse GetJobStatus(string jobId);
    bool DeleteJob(string jobId, bool isRecurring = false);
    List<WorkloadType> GetAvailableWorkloads();
}

public class HangfireWorkloadService : IHangfireWorkloadService
{
    private readonly IWorkloadExecutorService _executorService;
    private readonly ILogger<HangfireWorkloadService> _logger;

    public HangfireWorkloadService(IWorkloadExecutorService executorService, ILogger<HangfireWorkloadService> logger)
    {
        _executorService = executorService;
        _logger = logger;
    }

    public string EnqueueWorkload(WorkloadExecutionRequest request)
    {
        var jobId = BackgroundJob.Enqueue(() => ExecuteWorkloadJob(request.WorkloadType, request.Parameters, null));
        _logger.LogInformation("Enqueued workload {WorkloadType} with job ID {JobId}", request.WorkloadType, jobId);
        return jobId;
    }

    public string ScheduleWorkload(WorkloadExecutionRequest request)
    {
        if (!request.ScheduledAt.HasValue)
            throw new ArgumentException("ScheduledAt must be provided for scheduled jobs");

        var jobId = BackgroundJob.Schedule(() => ExecuteWorkloadJob(request.WorkloadType, request.Parameters, null), request.ScheduledAt.Value);
        _logger.LogInformation("Scheduled workload {WorkloadType} for {ScheduledAt} with job ID {JobId}", request.WorkloadType, request.ScheduledAt, jobId);
        return jobId;
    }

    public string AddRecurringWorkload(WorkloadExecutionRequest request)
    {
        if (string.IsNullOrEmpty(request.CronExpression))
            throw new ArgumentException("CronExpression must be provided for recurring jobs");

        var recurringJobId = request.RecurringJobId ?? $"{request.WorkloadType}-{Guid.NewGuid():N}";

        RecurringJob.AddOrUpdate(
            recurringJobId,
            () => ExecuteWorkloadJob(request.WorkloadType, request.Parameters, null),
            request.CronExpression);

        _logger.LogInformation("Added recurring workload {WorkloadType} with cron '{CronExpression}' and ID {JobId}", request.WorkloadType, request.CronExpression, recurringJobId);
        return recurringJobId;
    }

    public string ExecutePipeline(WorkflowPipelineRequest pipelineRequest)
    {
        var pipelineId = $"pipeline-{Guid.NewGuid():N}";
        var sortedSteps = pipelineRequest.Steps.OrderBy(s => s.Order).ToList();

        if (sortedSteps.Count == 0)
            throw new ArgumentException("Pipeline must contain at least one step");

        // Combina parametri globali con parametri specifici del primo step
        var firstStepParams = CombineParameters(pipelineRequest.GlobalParameters, sortedSteps[0].Parameters);

        var firstJobId = BackgroundJob.Enqueue(() => ExecuteWorkloadJob(sortedSteps[0].WorkloadType, firstStepParams, null));

        _logger.LogInformation("Started pipeline {PipelineName} ({PipelineId}) with first job {JobId}",
            pipelineRequest.PipelineName, pipelineId, firstJobId);

        // Crea la catena di job per i rimanenti step
        var previousJobId = firstJobId;

        for (int i = 1; i < sortedSteps.Count; i++)
        {
            var step = sortedSteps[i];
            var stepParams = CombineParameters(pipelineRequest.GlobalParameters, step.Parameters);

            if (step.DelayAfterCompletion.HasValue && step.DelayAfterCompletion.Value > TimeSpan.Zero)
            {
                // Aggiungi un job di delay se specificato
                var delayJobId = BackgroundJob.ContinueJobWith(previousJobId, () => DelayJob(step.DelayAfterCompletion.Value));
                previousJobId = BackgroundJob.ContinueJobWith(delayJobId, () => ExecuteWorkloadJob(step.WorkloadType, stepParams, null));
            }
            else
            {
                previousJobId = BackgroundJob.ContinueJobWith(previousJobId, () => ExecuteWorkloadJob(step.WorkloadType, stepParams, null));
            }

            _logger.LogInformation("Added step {Order} ({WorkloadType}) to pipeline {PipelineId} with job {JobId}",
                step.Order, step.WorkloadType, pipelineId, previousJobId);
        }

        return pipelineId;
    }

    [Queue("default")]
    public async Task<Dictionary<string, object>> ExecuteWorkloadJob(WorkloadType workloadType, Dictionary<string, object>? parameters, PerformContext? performContext)
    {
        var jobId = performContext?.BackgroundJob?.Id ?? Guid.NewGuid().ToString("N")[..8];

        using (LogContext.PushProperty("JobId", jobId))
        using (LogContext.PushProperty("WorkloadType", workloadType))
        {
            _logger.LogInformation("Starting execution of workload {WorkloadType} with job ID {JobId}", workloadType, jobId);

            try
            {
                if (!await _executorService.IsExecutableAvailable(workloadType))
                {
                    var error = $"Executable not available for workload {workloadType}";
                    _logger.LogError(error);
                    throw new FileNotFoundException(error);
                }

                var result = await _executorService.ExecuteWorkloadAsync(workloadType, parameters);

                _logger.LogInformation("Successfully completed workload {WorkloadType} with job ID {JobId}", workloadType, jobId);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute workload {WorkloadType} with job ID {JobId}", workloadType, jobId);
                throw;
            }
        }
    }

    [Queue("delay")]
    public async Task DelayJob(TimeSpan delay)
    {
        _logger.LogInformation("Delaying execution for {Delay}", delay);
        await Task.Delay(delay);
        _logger.LogInformation("Delay completed for {Delay}", delay);
    }

    public JobStatusResponse GetJobStatus(string jobId)
    {
        try
        {
            var monitoringApi = JobStorage.Current.GetMonitoringApi();
            var jobDetails = monitoringApi.JobDetails(jobId);

            if (jobDetails == null)
            {
                return new JobStatusResponse
                {
                    JobId = jobId,
                    Status = "NotFound"
                };
            }

            var latestState = jobDetails.History.FirstOrDefault();

            return new JobStatusResponse
            {
                JobId = jobId,
                Status = latestState?.StateName ?? "Unknown",
                CreatedAt = jobDetails.CreatedAt,
                ErrorMessage = latestState?.Reason
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving job status for {JobId}", jobId);
            return new JobStatusResponse
            {
                JobId = jobId,
                Status = "Error",
                ErrorMessage = ex.Message
            };
        }
    }

    public bool DeleteJob(string jobId, bool isRecurring = false)
    {
        try
        {
            if (isRecurring)
            {
                RecurringJob.RemoveIfExists(jobId);
                _logger.LogInformation("Deleted recurring job {JobId}", jobId);
            }
            else
            {
                BackgroundJob.Delete(jobId);
                _logger.LogInformation("Deleted job {JobId}", jobId);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete job {JobId}", jobId);
            return false;
        }
    }

    public List<WorkloadType> GetAvailableWorkloads()
    {
        var availableWorkloads = new List<WorkloadType>();

        foreach (var workloadType in Enum.GetValues<WorkloadType>())
        {
            if (_executorService.IsExecutableAvailable(workloadType).Result)
            {
                availableWorkloads.Add(workloadType);
            }
        }

        return availableWorkloads;
    }

    private static Dictionary<string, object>? CombineParameters(Dictionary<string, object>? globalParams, Dictionary<string, object>? stepParams)
    {
        if (globalParams == null && stepParams == null) return null;

        var combined = new Dictionary<string, object>();

        if (globalParams != null)
        {
            foreach (var kvp in globalParams)
                combined[kvp.Key] = kvp.Value;
        }

        if (stepParams != null)
        {
            foreach (var kvp in stepParams)
                combined[kvp.Key] = kvp.Value; // Step parameters override global ones
        }

        return combined;
    }
}