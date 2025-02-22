using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using SocketIOClient;

namespace CNC1MqttScript;

public class ConnectionEventArgs
{
    private static void SetupEventHandlers(IMqttClient client)
    {
        client.ConnectedAsync += async e =>
        {
            Console.WriteLine("### CONNECTED ###");
            // You can add logic to publish a message or subscribe to topics here.
            await Task.CompletedTask;
        };

        client.DisconnectedAsync += async e =>
        {
            Console.WriteLine("### DISCONNECTED ###");
            await Task.CompletedTask;
        };

        client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                Console.WriteLine($"Received on '{topic}': {payload}");

                // Example: parse JSON if needed:
                // var jsonDoc = JsonDocument.Parse(payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing message: {ex.Message}");
            }

            await Task.CompletedTask;
        };
    }
}