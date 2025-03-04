using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace CNC1MqttScript
{
    public class ConnectionEventArgs
    {
        private readonly ILogger<ConnectionEventArgs> _logger;

        public ConnectionEventArgs(ILogger<ConnectionEventArgs> logger)
        {
            _logger = logger;
        }

        public void SetupEventHandlers(IMqttClient client)
        {
            client.ConnectedAsync += async e =>
            {
                _logger.LogInformation("✅ MQTT Connected.");
                await Task.CompletedTask;
            };

            client.DisconnectedAsync += async e =>
            {
                _logger.LogError("⚠️ MQTT Disconnected.");
                await Task.CompletedTask;
            };

            client.ApplicationMessageReceivedAsync += async e =>
            {
                try
                {
                    // Retrieve the topic (or assign a default if null)
                    string topic = e.ApplicationMessage.Topic ?? "Unknown Topic";
                    // Extract the payload using the PayloadSegment API
                    string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray()).Trim();
                
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        _logger.LogWarning($"⚠️ Empty message received on topic: {topic}");
                        return;
                    }

                    _logger.LogDebug($"📩 Received on '{topic}': {payload}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Error processing MQTT message: {ex.Message}");
                }

                await Task.CompletedTask;
            };
        }
    }
}