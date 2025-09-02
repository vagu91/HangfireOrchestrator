using HangfireOrchestrator.Models;
using HangfireOrchestrator.Services;
using Microsoft.AspNetCore.Mvc;

namespace HangfireOrchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorkloadOrchestratorController : ControllerBase
{
    private readonly IHangfireWorkloadService _hangfireService;
    private readonly ILogger<WorkloadOrchestratorController> _logger;

    public WorkloadOrchestratorController(
        IHangfireWorkloadService hangfireService,
        ILogger<WorkloadOrchestratorController> logger)
    {
        _hangfireService = hangfireService;
        _logger = logger;
    }

    /// <summary>
    /// Esegue un workload immediatamente, con scheduling o ricorrente
    /// </summary>
    [HttpPost("execute")]
    public IActionResult ExecuteWorkload([FromBody] WorkloadExecutionRequest request)
    {
        try
        {
            string jobId = request.Mode switch
            {
                ExecutionMode.Immediate => _hangfireService.EnqueueWorkload(request),
                ExecutionMode.Scheduled => _hangfireService.ScheduleWorkload(request),
                ExecutionMode.Recurring => _hangfireService.AddRecurringWorkload(request),
                _ => throw new ArgumentException($"Invalid execution mode: {request.Mode}")
            };

            var response = new WorkloadExecutionResponse
            {
                JobId = jobId,
                Message = $"Workload {request.WorkloadType} {request.Mode.ToString().ToLower()} successfully",
                WorkloadType = request.WorkloadType,
                Mode = request.Mode,
                RequestedAt = DateTime.UtcNow,
                Parameters = request.Parameters
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing workload {WorkloadType}", request.WorkloadType);
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Esegue una pipeline di workload in sequenza
    /// </summary>
    [HttpPost("pipeline")]
    public IActionResult ExecutePipeline([FromBody] WorkflowPipelineRequest pipelineRequest)
    {
        try
        {
            var pipelineId = _hangfireService.ExecutePipeline(pipelineRequest);

            var response = new PipelineExecutionResponse
            {
                PipelineId = pipelineId,
                PipelineName = pipelineRequest.PipelineName,
                Message = $"Pipeline '{pipelineRequest.PipelineName}' started successfully",
                StartedAt = DateTime.UtcNow
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing pipeline {PipelineName}", pipelineRequest.PipelineName);
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Ottiene lo stato di un job
    /// </summary>
    [HttpGet("jobs/{jobId}/status")]
    public IActionResult GetJobStatus(string jobId)
    {
        try
        {
            var status = _hangfireService.GetJobStatus(jobId);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving job status for {JobId}", jobId);
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Elimina un job (normale o ricorrente)
    /// </summary>
    [HttpDelete("jobs/{jobId}")]
    public IActionResult DeleteJob(string jobId, [FromQuery] bool isRecurring = false)
    {
        try
        {
            var success = _hangfireService.DeleteJob(jobId, isRecurring);

            if (success)
            {
                return Ok(new { Message = $"Job {jobId} deleted successfully", JobId = jobId });
            }

            return BadRequest(new { Error = $"Failed to delete job {jobId}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting job {JobId}", jobId);
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Ottiene la lista dei workload disponibili
    /// </summary>
    [HttpGet("workloads")]
    public IActionResult GetAvailableWorkloads()
    {
        try
        {
            var workloads = _hangfireService.GetAvailableWorkloads();

            var workloadInfo = workloads.Select(w => new
            {
                Name = w.ToString(),
                Value = (int)w,
                Description = GetWorkloadDescription(w)
            }).ToList();

            return Ok(workloadInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving available workloads");
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Ottiene esempi di pipeline predefinite
    /// </summary>
    [HttpGet("pipelines/examples")]
    public IActionResult GetPipelineExamples()
    {
        var examples = new[]
        {
            new WorkflowPipelineRequest
            {
                PipelineName = "Complete Contract Workflow",
                Steps = new List<WorkflowStep>
                {
                    new() { WorkloadType = WorkloadType.Setup, Order = 1 },
                    new() { WorkloadType = WorkloadType.PreparazioneGenerazioneContratti, Order = 2, DelayAfterCompletion = TimeSpan.FromMinutes(2) },
                    new() { WorkloadType = WorkloadType.GeneraContratti, Order = 3, DelayAfterCompletion = TimeSpan.FromMinutes(5) },
                    new() { WorkloadType = WorkloadType.InizioFirmaMassiva, Order = 4 },
                    new() { WorkloadType = WorkloadType.FinalizzazioneFirmaMassiva, Order = 5 }
                },
                GlobalParameters = new Dictionary<string, object>
                {
                    ["DataInizioServizio"] = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"),
                    ["MaxConcurrentTasks"] = 3
                }
            },
            new WorkflowPipelineRequest
            {
                PipelineName = "Volunteer Digital Signature Process",
                Steps = new List<WorkflowStep>
                {
                    new() { WorkloadType = WorkloadType.PreparazioneFirmaVolontari, Order = 1 },
                    new() { WorkloadType = WorkloadType.InizioFirmaVolontari, Order = 2, DelayAfterCompletion = TimeSpan.FromMinutes(1) },
                    new() { WorkloadType = WorkloadType.FinalizzazioneFirmaVolontari, Order = 3 }
                },
                GlobalParameters = new Dictionary<string, object>
                {
                    ["MaxDegreeOfParallelism"] = 4,
                    ["ThreadSleepSeconds"] = 5
                }
            },
            new WorkflowPipelineRequest
            {
                PipelineName = "Entity Digital Signature Process",
                Steps = new List<WorkflowStep>
                {
                    new() { WorkloadType = WorkloadType.PreparazioneFirmaEnte, Order = 1 },
                    new() { WorkloadType = WorkloadType.InizioFirmaEnti, Order = 2, DelayAfterCompletion = TimeSpan.FromMinutes(1) },
                    new() { WorkloadType = WorkloadType.FinalizzazioneFirmaEnti, Order = 3 }
                },
                GlobalParameters = new Dictionary<string, object>
                {
                    ["MaxDegreeOfParallelism"] = 4
                }
            }
        };

        return Ok(examples);
    }

    private static string GetWorkloadDescription(WorkloadType workloadType)
    {
        return workloadType switch
        {
            WorkloadType.Setup => "Applica eventuali cambiamenti sulla base dati e sul filesystem",
            WorkloadType.PreparazioneGenerazioneContratti => "Prepara i dati per la generazione contratti",
            WorkloadType.GeneraContratti => "Genera i contratti PDF",
            WorkloadType.InizioFirmaMassiva => "Avvia il processo di firma massiva",
            WorkloadType.FinalizzazioneFirmaMassiva => "Finalizza la firma massiva",
            WorkloadType.PreparazioneFirmaVolontari => "Prepara la firma digitale volontari",
            WorkloadType.InizioFirmaVolontari => "Avvia firma digitale volontari",
            WorkloadType.FinalizzazioneFirmaVolontari => "Finalizza firma volontari",
            WorkloadType.ChiusuraFaseDiFirmaDigitale => "Chiude la fase di firma digitale",
            WorkloadType.PreparazioneFirmaEnte => "Prepara firma digitale enti",
            WorkloadType.InizioFirmaEnti => "Avvia firma digitale enti",
            WorkloadType.FinalizzazioneFirmaEnti => "Finalizza firma enti",
            WorkloadType.FinalizzazioneWorkflow => "Pone i contratti firmati dagli enti nello stato WorkflowCompletato",
            WorkloadType.ContrattiCleanup => "Pulisce i contratti obsoleti",
            _ => "Workload generico"
        };
    }
}