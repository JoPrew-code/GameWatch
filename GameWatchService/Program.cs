using GameWatchService;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "GameWatchService";
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

host.Run();