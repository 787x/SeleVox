using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SeleVox.Core;
using SeleVox.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
try
{
    if (CliArguments.WantsHelp(args)) { Console.WriteLine(CliArguments.Help); return 0; }
    using var client = new AgentClient();
    if (args[0] == "mcp")
    {
        if (args.Length != 1) throw new InvalidDataException();
        var reply = await client.CallAsync("config.show", new { }, lifetime.Token);
        if (!reply.Success) { Console.Error.WriteLine(reply.Code); return 4; }
        var config = reply.Data.Deserialize<AgentConfiguration>(AgentJson.Options)!;
        var builder = Host.CreateApplicationBuilder();
        // SDK logs may contain tool arguments; suppress diagnostics entirely to protect input.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(client);
        var server = builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<AgentTools>();
        if (config.ExposeDeletePreset) server.WithTools<DeletePresetTool>();
        if (config.ExposeSetDefaults) server.WithTools<SetDefaultsTool>();
        await builder.Build().RunAsync(lifetime.Token); return 0;
    }
    var parsed = await CliArguments.ParseAsync(args, lifetime.Token);
    var result = await client.CallAsync(parsed.Command, parsed.Arguments, lifetime.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, AgentJson.Options));
    if (!result.Success) return result.Code switch { "invalid_input" => 2, "permission_denied" => 3, "busy" => 5, "cancelled" => 6, _ => 4 };
    if (result.Data.ValueKind == JsonValueKind.Object && result.Data.TryGetProperty("State", out var state))
        return state.GetString() switch { "failed" => 4, "cancelled" => 6, _ => 0 };
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled_or_timeout");
    if (args[0] != "mcp") Console.WriteLine(JsonSerializer.Serialize(new AgentReply(false, "cancelled_or_timeout", AgentJson.Empty), AgentJson.Options));
    return 6;
}
catch (Exception ex)
{
    var invalid = ex is InvalidDataException or JsonException or FormatException or ArgumentException;
    var code = invalid ? "invalid_input" : ex is FileNotFoundException file ? file.Message == "desktop_not_found" ? "desktop_not_found" : "input_file_unavailable" : ex is TimeoutException ? "desktop_unavailable_or_incompatible" : "connection_or_execution_failed";
    Console.Error.WriteLine(code);
    if (args[0] != "mcp") Console.WriteLine(JsonSerializer.Serialize(new AgentReply(false, code, AgentJson.Empty), AgentJson.Options));
    return invalid ? 2 : 4;
}
