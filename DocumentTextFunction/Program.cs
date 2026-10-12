using System.Text;
using Microsoft.Extensions.Hosting;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .Build();

host.Run();
