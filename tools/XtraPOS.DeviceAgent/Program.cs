using System.Net;
using System.Text;

var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XtraPOS", "device-config.json");
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
var listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:47321/");
listener.Start();
Console.WriteLine("XtraPOS Device Agent listening on 127.0.0.1:47321");

while (true)
{
    var context = await listener.GetContextAsync();
    var origin = context.Request.Headers["Origin"];
    if (!string.IsNullOrEmpty(origin) && !origin.Contains("localhost", StringComparison.OrdinalIgnoreCase) && !origin.Contains("sharbelfashion.com", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 403;
        context.Response.Close();
        continue;
    }

    context.Response.Headers.Add("Access-Control-Allow-Origin", origin ?? "http://localhost");
    context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, PUT, OPTIONS");
    context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");
    if (context.Request.HttpMethod == "OPTIONS")
    {
        context.Response.StatusCode = 204;
        context.Response.Close();
        continue;
    }

    if (context.Request.Url?.AbsolutePath != "/device-config")
    {
        context.Response.StatusCode = 404;
        context.Response.Close();
        continue;
    }

    if (context.Request.HttpMethod == "GET")
    {
        var json = File.Exists(path) ? await File.ReadAllTextAsync(path) : "{}";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
        continue;
    }

    if (context.Request.HttpMethod == "PUT")
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        if (body.Contains("Password", StringComparison.OrdinalIgnoreCase) || body.Contains("connectionString", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            continue;
        }

        await File.WriteAllTextAsync(path, body);
        context.Response.StatusCode = 204;
        context.Response.Close();
        continue;
    }

    context.Response.StatusCode = 405;
    context.Response.Close();
}
