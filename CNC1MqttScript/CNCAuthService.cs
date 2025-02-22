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
        public required string Host { get; set; }
        public required string Username { get; set; }
        public required string Password { get; set; }
        public required string Token { get; set; }

        public CNCAuthService(string host, string username, string password, string token)
        {
            Host = host;
            Username = username;
            Password = password;
            Token = token;
        }

        public CNCAuthService(ILogger<CNCAuthService> logger, IManagedMqttClient mqttClient)
        {
            _logger = logger;
            _mqttClient = mqttClient;
        }

        /// <summary>
        /// Contacts the CNCjs server to generate and retrieve a new authentication token.
        /// </summary>
        /// <returns>The new token if successful, null otherwise.</returns>
        public async Task<string?> GetAuthTokenAsync()
        {
            if (string.IsNullOrEmpty(Host) || string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password))
            {
                _logger.LogError("Missing Host, Username, or Password for authentication.");
                return null;
            }

            using var client = new HttpClient();
            var signinUrl = $"{Host}/api/signin";

            var credentials = new { name = Username, password = Password };
            var payload = JsonSerializer.Serialize(credentials);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync(signinUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to authenticate: {response.StatusCode}");
                    return null;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                var jsonDoc = JsonDocument.Parse(responseBody);

                if (jsonDoc.RootElement.TryGetProperty("token", out var tokenElement))
                {
                    Token = tokenElement.GetString() ?? string.Empty;
                    _logger.LogInformation("Successfully retrieved authentication token.");
                    return Token;
                }

                _logger.LogError("Token was not found in the response.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception while retrieving token: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Subscribes to the MQTT topic that provides the authentication token.
        /// If a token is published to that topic, we store it locally.
        /// </summary>
        public async Task SubscribeToMqttTokenAsync()
        {
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                if (e.ApplicationMessage.Topic.Equals("CNCS/CNC1/Server/MqttToken", StringComparison.InvariantCultureIgnoreCase))
                {
                    var receivedToken = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray());
                    
                    if (!string.IsNullOrWhiteSpace(receivedToken))
                    {
                        Token = receivedToken;
                        _logger.LogInformation("Updated authentication token via MQTT.");
                    }
                    else
                    {
                        _logger.LogWarning("Received an empty token from MQTT.");
                    }
                }
                await Task.CompletedTask;
            };

            await _mqttClient.SubscribeAsync("CNCS/CNC1/Server/MqttToken");
            _logger.LogInformation("Subscribed to MQTT token updates.");
        }

        /// <summary>
        /// Publishes a new authentication token to the MQTT broker.
        /// </summary>
        /// <param name="newToken">The new token to publish.</param>
        public async Task PublishTokenAsync(string newToken)
        {
            if (string.IsNullOrWhiteSpace(newToken))
            {
                _logger.LogError("Attempted to publish an empty token.");
                return;
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic("CNCS/CNC1/Server/MqttToken")
                .WithPayload(newToken)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
                .WithRetainFlag()
                .Build();

            await _mqttClient.EnqueueAsync(message);
            _logger.LogInformation("Published new authentication token via MQTT.");
            Token = newToken;
        }
    }
}
