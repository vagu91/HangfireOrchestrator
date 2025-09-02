using Hangfire;
using Hangfire.SqlServer;
using HangfireOrchestrator.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Hangfire Workload Orchestrator API",
        Version = "v1",
        Description = "API per l'orchestrazione dei workload tramite Hangfire"
    });

    // Include XML comments if available
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
});

// Configure Hangfire
var connectionString = builder.Configuration.GetConnectionString("HangfireConnection");

// Handle connection string with placeholder for password
if (connectionString?.Contains("{0}") == true)
{
    var passwordEnvVar = builder.Configuration.GetValue<string>("HangfirePasswordEnvVar");
    if (!string.IsNullOrEmpty(passwordEnvVar))
    {
        var password = Environment.GetEnvironmentVariable(passwordEnvVar);
        if (!string.IsNullOrEmpty(password))
        {
            connectionString = string.Format(connectionString, password);
        }
    }
}

builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.Zero,
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true,
        PrepareSchemaIfNecessary = true
    }));

// Add Hangfire server
builder.Services.AddHangfireServer(options =>
{
    options.Queues = new[] { "default", "delay", "critical" };
    options.WorkerCount = Environment.ProcessorCount * 2;
});

// Register custom services
builder.Services.AddScoped<IWorkloadExecutorService, WorkloadExecutorService>();
builder.Services.AddScoped<IHangfireWorkloadService, HangfireWorkloadService>();

// Add HTTP client for potential external calls
builder.Services.AddHttpClient();

// Add CORS if needed
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Hangfire Workload Orchestrator API v1");
        c.RoutePrefix = "swagger";
    });
}

// Enable CORS
app.UseCors("AllowAll");

app.UseRouting();

// Configure Hangfire Dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new Hangfire.Dashboard.LocalRequestsOnlyAuthorizationFilter() },
    DisplayStorageConnectionString = false,
    DashboardTitle = "Workload Orchestrator Dashboard"
});

app.MapControllers();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Timestamp = DateTime.UtcNow,
    Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
    Environment = app.Environment.EnvironmentName
}));

// Hangfire info endpoint
app.MapGet("/hangfire/info", () =>
{
    var monitoringApi = JobStorage.Current.GetMonitoringApi();
    var statistics = monitoringApi.GetStatistics();

    return Results.Ok(new
    {
        Servers = statistics.Servers,
        Queues = statistics.Queues,
        Jobs = new
        {
            Enqueued = statistics.Enqueued,
            Failed = statistics.Failed,
            Processing = statistics.Processing,
            Scheduled = statistics.Scheduled,
            Succeeded = statistics.Succeeded
        }
    });
});

Log.Information("Starting Hangfire Workload Orchestrator");

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }