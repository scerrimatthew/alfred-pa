using Alfred.Functions.Configuration;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.Pdf;
using Alfred.Functions.Services.State;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Configuration
        services.Configure<AlfredOptions>(context.Configuration.GetSection(AlfredOptions.SectionName));
        services.Configure<GoogleOptions>(context.Configuration.GetSection(GoogleOptions.SectionName));

        // Azure Table Storage
        var storageConnectionString = context.Configuration["AzureWebJobsStorage"]!;
        services.AddSingleton(new TableServiceClient(storageConnectionString));

        // Services
        services.AddSingleton<IStateService, TableStorageStateService>();
        services.AddSingleton<IPdfExtractorService, PdfExtractorService>();
        services.AddSingleton<IGmailReaderService, GmailReaderService>();
        services.AddSingleton<ISummarizerService, ClaudeSummarizerService>();
        services.AddSingleton<INewsResearchService, ClaudeNewsResearchService>();
        services.AddSingleton<ICalendarService, GoogleCalendarService>();
        services.AddSingleton<INotificationService, TelegramNotificationService>();
    })
    .Build();

host.Run();
