using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;

namespace CNC1MqttScript
{
    public class CNCAuthService
    {
        private readonly ILogger<CNCAuthService> _logger;
        private readonly IManagedMqttClient _mqttClient;

        // Values set from configuration or MQTT messages
        public string Host { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        
        // Store the current token in a field for reuse
        public string Token { get; private set; }

        public CNCAuthService(ILogger<CNCAuthService> logger, IManagedMqttClient mqttClient)
        {
            _logger = logger;
            _mqttClient = mqttClient;
        }

        /// <summary>
        /// Contacts the CNCjs server to generate and retrieve a new authentication token.
        /// </summary>
        /// <returns>The new token if successful, null otherwise.</returns>
        public async Task<string> GetAuthTokenAsync()
        {
            if (string.IsNullOrEmpty(Host) || string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password))
            {
                _logger.LogError("[ERROR] Missing Host, Username, or Password for authentication.");
                return null;
            }

            using var client = new HttpClient();
            var signinUrl = $"{Host}/api/signin";
            _logger.LogInformation($"[DEBUG] Sign-in URL: {signinUrl}");

            var credentials = new { name = Username, password = Password };
            var payload = JsonSerializer.Serialize(credentials);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            try
            {
                _logger.LogInformation("[DEBUG] Sending POST request to /api/signin...");
                var response = await client.PostAsync(signinUrl, content);
                _logger.LogInformation($"[DEBUG] Response Status: {response.StatusCode}");

                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"[DEBUG] Response Body: {responseBody}");

                var jsonDoc = JsonDocument.Parse(responseBody);
                if (jsonDoc.RootElement.TryGetProperty("token", out var tokenElement))
                {
                    Token = tokenElement.GetString();
                    _logger.LogInformation($"[DEBUG] Extracted token: {Token}");
                    return Token;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[DEBUG] Exception while retrieving token: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Subscribes to the MQTT topic that provides the authentication token.
        /// If a token is published to that topic, we store it locally.
        /// </summary>
        public async Task SubscribeToMqttTokenAsync()
        {
            // Register a message handler for token updates
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                if (e.ApplicationMessage.Topic.Equals("CNCS/CNC1/Server/MqttToken", StringComparison.InvariantCultureIgnoreCase))
                {
                    var receivedToken = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                    _logger.LogInformation($"[DEBUG] Received token from MQTT: {receivedToken}");
                    // Update the current token
                    Token = receivedToken;
                }
                await Task.CompletedTask;
            };

            // Subscribe to the token topic
            await _mqttClient.SubscribeAsync("CNCS/CNC1/Server/MqttToken");
            _logger.LogInformation("[DEBUG] Subscribed to MQTT topic: CNCS/CNC1/Server/MqttToken");
        }

        /// <summary>
        /// Publishes a new authentication token to the MQTT broker.
        /// </summary>
        /// <param name="newToken">The new token to publish.</param>
        public async Task PublishTokenAsync(string newToken)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic("CNCS/CNC1/Server/MqttToken")
                .WithPayload(newToken)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
                .WithRetainFlag()
                .Build();

            await _mqttClient.EnqueueAsync(message);
            _logger.LogInformation($"[DEBUG] Published new token to MQTT: {newToken}");
            // Update the stored token
            Token = newToken;
        }
    }
}
