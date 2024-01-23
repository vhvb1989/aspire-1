var builder = DistributedApplication.CreateBuilder(args);

var catalogDb = builder.AddPostgres("postgres")
                       .WithPgAdmin()
                       .AddDatabase("catalogdb");

var basketCache = builder.AddRedis("basketcache")
                         .WithRedisCommander();

var catalogService = builder.AddProject<Projects.CatalogService>("catalogservice")
                            .WithReference(catalogDb)
                            .WithReplicas(2);

var messaging = builder.AddRabbitMQ("messaging");

var basketService = builder.AddProject("basketservice", @"..\BasketService\BasketService.csproj")
                           .WithReference(basketCache)
                           .WithReference(messaging);

builder.AddProject<Projects.MyFrontend>("frontend")
       .WithReference(basketService)
       .WithReference(catalogService.GetEndpoint("http"));

builder.AddProject<Projects.OrderProcessor>("orderprocessor")
       .WithReference(messaging)
       .WithLaunchProfile("OrderProcessor");

builder.AddProject<Projects.ApiGateway>("apigateway")
       .WithReference(basketService)
       .WithReference(catalogService);

builder.AddProject<Projects.CatalogDb>("catalogdbapp")
       .WithReference(catalogDb);

// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// to test end developer dashboard launch experience. Refer to WorkloadAttributes.cs
// for the path to the dashboard binary (defaults to a relative path to the artifacts
// directory).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard)
    .WithEnvironment("DOTNET_DASHBOARD_GRPC_ENDPOINT_URL", "http://localhost:5555")
    .ExcludeFromManifest();

builder.Build().Run();
