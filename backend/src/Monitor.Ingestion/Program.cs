using Monitor.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.AddIngestion();

await builder.Build().RunAsync();