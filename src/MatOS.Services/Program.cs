using MatOS.Services;

// The image has no curl: the health check of the container is this program asking itself.
if (args.Contains("--healthcheck"))
{
    return await ServicesHost.CheckHealthAsync();
}

var app = ServicesHost.Build(args);
await app.RunAsync();
return 0;
