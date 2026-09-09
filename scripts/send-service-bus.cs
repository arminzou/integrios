#:package Azure.Messaging.ServiceBus@7.20.2

using Azure.Messaging.ServiceBus;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run scripts/send-service-bus.cs -- <connection-string> <queue>");
    return 2;
}

await using var client = new ServiceBusClient(args[0]);
await using ServiceBusSender sender = client.CreateSender(args[1]);

while (await Console.In.ReadLineAsync() is { } body)
{
    using JsonDocument document = JsonDocument.Parse(body);
    string messageId = document.RootElement.GetProperty("source_event_id").GetString()
        ?? throw new InvalidOperationException("source_event_id must be a string.");
    await sender.SendMessageAsync(new ServiceBusMessage(body)
    {
        ContentType = "application/json",
        MessageId = messageId,
    });
}

return 0;
