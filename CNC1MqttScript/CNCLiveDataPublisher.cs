using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using SocketIOClient;

namespace CNC1MqttScript
{
    public class CNCLiveDataPublisher
    {
        private readonly ILogger<CNCLiveDataPublisher> _logger;
        private readonly IManagedMqttClient _mqttClient;
        private readonly SocketIOClient.SocketIO _socket; // CNCjs WebSocket Client

        // Track current milling state
        private bool _millingActive = false;
        private LiveData _lastPublishedData = new LiveData();

        public CNCLiveDataPublisher(
            ILogger<CNCLiveDataPublisher> logger,
            IManagedMqttClient mqttClient,
            SocketIOClient.SocketIO socket)
        {
            _logger = logger;
            _mqttClient = mqttClient;
            _socket = socket;
        }

        public async Task StartAsync()
        {
            // Listen to all CNCjs WebSocket events
            _socket.OnAny((string eventName, SocketIOResponse response) =>
            {
                try
                {
                    _logger.LogDebug($"[DEBUG] Received event: {eventName} with payload: {response}");

                    switch (eventName)
                    {
                        case "command":
                            HandleCommandEvent(response);
                            break;
                        case "sender:status":
                            HandleSenderStatusEvent(response);
                            break;
                        case "workflow:state":
                            HandleWorkflowStateEvent(response);
                            break;
                        case "task:finish":
                            HandleTaskFinishEvent(response);
                            break;
                        case "controller:state":
                        case "Grbl:state":
                            HandleLiveDataEvent(response);
                            break;
                        case "task:error":
                        case "serialport:error":
                            HandleErrorEvent(response);
                            break;
                        default:
                            _logger.LogDebug($"[DEBUG] Unhandled event: {eventName}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing socket event: {ex.Message}");
                }
            });
        }

        private void HandleCommandEvent(SocketIOResponse response)
        {
            try
            {
                JsonElement jsonArray = response.GetValue<JsonElement>();
                if (jsonArray.ValueKind == JsonValueKind.Array && jsonArray.GetArrayLength() > 2)
                {
                    string command = jsonArray[2].GetString();
                    _logger.LogInformation($"Command event detected: {command}");
                    if (command == "gcode:start")
                    {
                        _millingActive = true;
                        _logger.LogInformation("Milling started via command: gcode:start");
                    }
                    else if (command == "gcode:pause" || command == "gcode:stop")
                    {
                        _millingActive = false;
                        _logger.LogInformation($"Milling paused/stopped via command: {command}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error handling command event: {ex.Message}");
            }
        }

        private void HandleSenderStatusEvent(SocketIOResponse response)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(response.ToString());
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("sp", out JsonElement spElement))
                {
                    _millingActive = (spElement.GetInt32() == 1);
                    _logger.LogInformation($"Sender status: millingActive = {_millingActive}");
                }

                if (root.TryGetProperty("hold", out JsonElement holdElement) && holdElement.GetBoolean())
                {
                    _logger.LogInformation("Sender status: milling paused (hold = true).");
                }

                PublishLiveData(root);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error handling sender:status event: {ex.Message}");
            }
        }

        private void HandleWorkflowStateEvent(SocketIOResponse response)
        {
            try
            {
                string state = response.GetValue<string>();
                _logger.LogInformation($"Workflow state: {state}");
                _millingActive = (state == "running");

                PublishLiveData();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error handling workflow:state event: {ex.Message}");
            }
        }

        private void HandleTaskFinishEvent(SocketIOResponse response)
        {
            try
            {
                _millingActive = false;
                _logger.LogInformation("Task finished: Milling job complete.");
                PublishLiveData();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error handling task:finish event: {ex.Message}");
            }
        }

        private void HandleLiveDataEvent(SocketIOResponse response)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(response.ToString());
                JsonElement root = doc.RootElement;
                PublishLiveData(root);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error handling live data event: {ex.Message}");
            }
        }

        private void HandleErrorEvent(SocketIOResponse response)
        {
            try
            {
                _logger.LogError($"Error event received: {response}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing error event: {ex.Message}");
            }
        }

        private async void PublishLiveData(JsonElement? root = null)
        {
            try
            {
                var liveData = new LiveData(_lastPublishedData);

                if (root.HasValue && root.Value.TryGetProperty("status", out var statusEl))
                {
                    if (statusEl.TryGetProperty("spindle", out var spindleEl))
                        liveData.SpindleSpeed = spindleEl.GetInt32();

                    if (statusEl.TryGetProperty("total", out var totalEl))
                        liveData.TotalGcodeCommands = totalEl.GetInt32();

                    if (statusEl.TryGetProperty("sent", out var sentEl))
                        liveData.SentGcodeCommands = sentEl.GetInt32();

                    if (statusEl.TryGetProperty("received", out var recvEl))
                        liveData.ReceivedGcodeCommands = recvEl.GetInt32();

                    if (statusEl.TryGetProperty("elapsedTime", out var eTimeEl))
                        liveData.ElapsedTime = eTimeEl.GetInt64();

                    if (statusEl.TryGetProperty("remainingTime", out var rTimeEl))
                        liveData.RemainingTime = rTimeEl.GetInt64();
                }

                await PublishMetric("CurrentFederate", liveData.CurrentFederate);
                await PublishMetric("SpindleSpeed", liveData.SpindleSpeed.ToString());
                await PublishMetric("TotalGcodeCommands", liveData.TotalGcodeCommands.ToString());
                await PublishMetric("SentGcodeCommands", liveData.SentGcodeCommands.ToString());
                await PublishMetric("ReceivedGcodeCommands", liveData.ReceivedGcodeCommands.ToString());
                await PublishMetric("ElapsedTime", liveData.ElapsedTime.ToString());
                await PublishMetric("RemainingTime", liveData.RemainingTime.ToString());

                _logger.LogInformation("Published all live data metrics to MQTT.");
                _lastPublishedData = liveData;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error publishing live data: {ex.Message}");
            }
        }

        private async Task PublishMetric(string metricName, string value)
        {
            try
            {
                if (string.IsNullOrEmpty(value))
                {
                    _logger.LogWarning($"Skipping empty payload for {metricName}");
                    return;
                }

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic($"CNCS/CNC1/Data/{metricName}")
                    .WithPayload(value)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
                    .WithRetainFlag()
                    .Build();

                await _mqttClient.EnqueueAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error publishing {metricName}: {ex.Message}");
            }
        }

        private class LiveData
        {
            public string CurrentFederate { get; set; } = "0";
            public int SpindleSpeed { get; set; }
            public int TotalGcodeCommands { get; set; }
            public int SentGcodeCommands { get; set; }
            public int ReceivedGcodeCommands { get; set; }
            public long ElapsedTime { get; set; }
            public long RemainingTime { get; set; }

            public LiveData() { }
            public LiveData(LiveData other) => (CurrentFederate, SpindleSpeed, TotalGcodeCommands, SentGcodeCommands, ReceivedGcodeCommands, ElapsedTime, RemainingTime) = 
                (other.CurrentFederate, other.SpindleSpeed, other.TotalGcodeCommands, other.SentGcodeCommands, other.ReceivedGcodeCommands, other.ElapsedTime, other.RemainingTime);
        }
    }
}
