using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HangfireOrchestrator.Models;

public enum WorkloadType
{
    Setup,
    PreparazioneGenerazioneContratti,
    GeneraContratti,
    InizioFirmaMassiva,
    FinalizzazioneFirmaMassiva,
    PreparazioneFirmaVolontari,
    InizioFirmaVolontari,
    FinalizzazioneFirmaVolontari,
    ChiusuraFaseDiFirmaDigitale,
    PreparazioneFirmaEnte,
    InizioFirmaEnti,
    FinalizzazioneFirmaEnti,
    FinalizzazioneWorkflow,
    ContrattiCleanup
}

public enum ExecutionMode
{
    Immediate,
    Scheduled,
    Recurring
}

public class WorkloadExecutionRequest
{
    [Required]
    public WorkloadType WorkloadType { get; set; }

    public ExecutionMode Mode { get; set; } = ExecutionMode.Immediate;

    public Dictionary<string, object>? Parameters { get; set; }

    // Per scheduled jobs
    public DateTime? ScheduledAt { get; set; }

    // Per recurring jobs
    public string? CronExpression { get; set; }
    public string? RecurringJobId { get; set; }
}

public class WorkloadExecutionResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public WorkloadType WorkloadType { get; set; }
    public ExecutionMode Mode { get; set; }
    public DateTime RequestedAt { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
}

public class JobStatusResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object>? Result { get; set; }
}

public class WorkflowPipelineRequest
{
    [Required]
    public string PipelineName { get; set; } = string.Empty;

    [Required]
    public List<WorkflowStep> Steps { get; set; } = new();

    public Dictionary<string, object>? GlobalParameters { get; set; }
}

public class WorkflowStep
{
    [Required]
    public WorkloadType WorkloadType { get; set; }

    public int Order { get; set; }

    public Dictionary<string, object>? Parameters { get; set; }

    public TimeSpan? DelayAfterCompletion { get; set; }

    public bool ContinueOnError { get; set; } = false;
}

public class PipelineExecutionResponse
{
    public string PipelineId { get; set; } = string.Empty;
    public string PipelineName { get; set; } = string.Empty;
    public List<string> JobIds { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
}