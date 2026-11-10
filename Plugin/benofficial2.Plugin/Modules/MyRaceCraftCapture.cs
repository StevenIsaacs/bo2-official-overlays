/*
    benofficial2's Official Overlays
    Copyright (C) 2025-2026 benofficial2

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using GameReaderCommon;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace benofficial2.Plugin
{
    public class MyRaceCraftCaptureSettings : ModuleSettings
    {
        public bool Enabled { get; set; } = true;
        public string ListenerUrl { get; set; } = "ws://127.0.0.1:9898/ws/v1/capture";
        public string Token { get; set; } = string.Empty;
        public int CarsIntervalMs { get; set; } = 100;
    }

    public class MyRaceCraftCaptureModule : PluginModuleBase
    {
        private const int MaxDrivers = 128;
        private const int BufferCapacity = 1024;

        private readonly object _wsLock = new object();

        public MyRaceCraftCaptureSettings Settings { get; set; }

        private Guid _captureId = Guid.NewGuid();
        private long _sequence = 0;

        private DateTime _lastCarsUpdate = DateTime.MinValue;
        private TimeSpan _carsInterval = TimeSpan.FromMilliseconds(100);

        private string _lastSubSessionId = string.Empty;
        private int _lastSessionNum = -1;
        private int _lastSessionUniqueId = -1;
        private string _runFingerprint = string.Empty;

        private BlockingCollection<string> _sendQueue;
        private Thread _senderThread;
        private CancellationTokenSource _senderCts;
        private ClientWebSocket _webSocket;

        public override void Init(PluginManager pluginManager, benofficial2 plugin)
        {
            Settings = plugin.ReadCommonSettings<MyRaceCraftCaptureSettings>("MyRaceCraftCapture", () => new MyRaceCraftCaptureSettings());
            _carsInterval = TimeSpan.FromMilliseconds(Math.Max(10, Settings.CarsIntervalMs));
            _captureId = Guid.NewGuid();
            _sequence = 0;
            StartSender();
        }

        public override void DataUpdate(PluginManager pluginManager, benofficial2 plugin, ref GameData data)
        {
            if (!Settings.Enabled)
                return;
            if (data.NewData == null)
                return;

            RawDataHelper.TryGetSessionData<string>(ref data, out string subSessionId, "WeekendInfo", "SubSessionID");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int sessionNum, "SessionNum");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int sessionUniqueId, "SessionUniqueID");
            RawDataHelper.TryGetSessionData<int>(ref data, out int playerCarIdx, "DriverInfo", "DriverCarIdx");

            if (subSessionId != _lastSubSessionId || sessionNum != _lastSessionNum || sessionUniqueId != _lastSessionUniqueId)
            {
                _lastSubSessionId = subSessionId;
                _lastSessionNum = sessionNum;
                _lastSessionUniqueId = sessionUniqueId;
                _captureId = Guid.NewGuid();
                _sequence = 0;
                _runFingerprint = string.IsNullOrEmpty(subSessionId) || subSessionId == "0"
                    ? ComputeRunFingerprint(ref data, subSessionId, sessionNum, sessionUniqueId, playerCarIdx)
                    : string.Empty;
            }

            JArray cars = null;
            if ((data.FrameTime - _lastCarsUpdate) >= _carsInterval)
            {
                _lastCarsUpdate = data.FrameTime;
                cars = BuildCars(ref data);
            }

            JObject frame = BuildFrame(ref data, subSessionId, sessionUniqueId, sessionNum, playerCarIdx, cars);
            EnqueueFrame(frame);
        }

        public override void End(PluginManager pluginManager, benofficial2 plugin)
        {
            StopSender();
            plugin.SaveCommonSettings("MyRaceCraftCapture", Settings);
        }

        private JObject BuildFrame(ref GameData data, string subSessionId, int sessionUniqueId, int sessionNum, int playerCarIdx, JArray cars)
        {
            RawDataHelper.TryGetTelemetryData<double>(ref data, out double sessionTime, "SessionTime");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int sessionFlags, "SessionFlags");

            JObject frame = new JObject();
            frame["type"] = "state_sample";
            frame["capture_id"] = _captureId.ToString();
            frame["sequence"] = _sequence++;
            frame["subsession_id"] = subSessionId;
            frame["session_unique_id"] = sessionUniqueId;
            frame["session_num"] = sessionNum;
            frame["run_fingerprint"] = _runFingerprint;
            frame["session_time"] = sessionTime;
            frame["player_car_idx"] = playerCarIdx;
            frame["flag"] = GetFlagString(sessionFlags);

            if (cars != null)
                frame["cars"] = cars;

            frame["player"] = BuildPlayer(ref data);
            return frame;
        }

        private JArray BuildCars(ref GameData data)
        {
            JArray cars = new JArray();

            if (!RawDataHelper.TryGetSessionData<List<object>>(ref data, out List<object> drivers, "DriverInfo", "Drivers"))
                return cars;
            if (drivers == null)
                return cars;

            for (int i = 0; i < drivers.Count; i++)
            {
                RawDataHelper.TryGetValue<int>(drivers, out int carIdx, i, "CarIdx");
                if (carIdx < 0 || carIdx >= MaxDrivers)
                    continue;

                RawDataHelper.TryGetTelemetryData<int>(ref data, out int lap, "CarIdxLap", carIdx);
                RawDataHelper.TryGetTelemetryData<double>(ref data, out double lapDistPct, "CarIdxLapDistPct", carIdx);
                RawDataHelper.TryGetTelemetryData<int>(ref data, out int classPosition, "CarIdxClassPosition", carIdx);
                RawDataHelper.TryGetTelemetryData<int>(ref data, out int trackSurface, "CarIdxTrackSurface", carIdx);
                RawDataHelper.TryGetTelemetryData<bool>(ref data, out bool onPitRoad, "CarIdxOnPitRoad", carIdx);
                RawDataHelper.TryGetTelemetryData<double>(ref data, out double estTime, "CarIdxEstTime", carIdx);
                RawDataHelper.TryGetTelemetryData<double>(ref data, out double f2Time, "CarIdxF2Time", carIdx);

                JObject car = new JObject();
                car["idx"] = carIdx;
                car["lap"] = lap;
                car["lap_dist_pct"] = lapDistPct;
                car["class_position"] = classPosition;
                car["track_surface"] = trackSurface;
                car["on_pit_road"] = onPitRoad;
                car["est_time"] = estTime;
                car["f2_time"] = f2Time;
                cars.Add(car);
            }

            return cars;
        }

        private JObject BuildPlayer(ref GameData data)
        {
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int incidents, "PlayerCarMyIncidentCount");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int teamIncidents, "PlayerCarTeamIncidentCount");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int driverIncidents, "PlayerCarDriverIncidentCount");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int playerIncidents, "PlayerIncidents");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int position, "PlayerCarPosition");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int classPosition, "PlayerCarClassPosition");
            RawDataHelper.TryGetTelemetryData<double>(ref data, out double speed, "Speed");
            RawDataHelper.TryGetTelemetryData<double>(ref data, out double velocityX, "VelocityX");
            RawDataHelper.TryGetTelemetryData<double>(ref data, out double velocityY, "VelocityY");
            RawDataHelper.TryGetTelemetryData<double>(ref data, out double yawRate, "YawRate");
            RawDataHelper.TryGetTelemetryData<double>(ref data, out double latAccel, "LatAccel");
            RawDataHelper.TryGetTelemetryData<double>(ref data, out double longAccel, "LongAccel");
            RawDataHelper.TryGetTelemetryData<double>(ref data, out double steer, "SteeringWheelAngle");
            RawDataHelper.TryGetTelemetryData<bool>(ref data, out bool onPitRoad, "OnPitRoad");
            RawDataHelper.TryGetTelemetryData<bool>(ref data, out bool pitstopActive, "PitstopActive");
            RawDataHelper.TryGetTelemetryData<bool>(ref data, out bool inPitStall, "PlayerCarInPitStall");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int leftRight, "CarLeftRight");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int lap, "Lap");
            RawDataHelper.TryGetTelemetryData<int>(ref data, out int lapCompleted, "LapCompleted");

            JObject player = new JObject();
            player["incidents"] = incidents;
            player["team_incidents"] = teamIncidents;
            player["driver_incidents"] = driverIncidents;
            player["player_incidents"] = playerIncidents;
            player["position"] = position;
            player["class_position"] = classPosition;
            player["speed"] = speed;
            player["velocity_x"] = velocityX;
            player["velocity_y"] = velocityY;
            player["yaw_rate"] = yawRate;
            player["lat_accel"] = latAccel;
            player["long_accel"] = longAccel;
            player["steer"] = steer;
            player["on_pit_road"] = onPitRoad;
            player["pitstop_active"] = pitstopActive;
            player["in_pit_stall"] = inPitStall;
            player["left_right"] = leftRight;
            player["lap"] = lap;
            player["lap_completed"] = lapCompleted;
            return player;
        }

        private string ComputeRunFingerprint(ref GameData data, string subSessionId, int sessionNum, int sessionUniqueId, int playerCarIdx)
        {
            RawDataHelper.TryGetSessionData<int>(ref data, out int trackId, "WeekendInfo", "TrackID");
            RawDataHelper.TryGetSessionData<string>(ref data, out string sessionId, "WeekendInfo", "SessionID");
            RawDataHelper.TryGetTelemetryData<double>(ref data, out double sessionTimeTotal, "SessionTimeTotal");
            string sessionTypeName = data.NewData?.SessionTypeName ?? string.Empty;
            string carId = data.NewData?.CarId ?? string.Empty;

            string raw = string.Join("|",
                trackId,
                sessionId,
                subSessionId,
                sessionNum,
                sessionUniqueId,
                sessionTypeName,
                carId,
                playerCarIdx,
                sessionTimeTotal.ToString("F3", CultureInfo.InvariantCulture));

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return "sha256:" + BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string GetFlagString(int flags)
        {
            if ((flags & 0x0001) != 0) return "checkered";
            if ((flags & 0x0008) != 0 || (flags & 0x0100) != 0 || (flags & 0x4000) != 0 || (flags & 0x8000) != 0) return "yellow";
            if ((flags & 0x0010) != 0) return "red";
            if ((flags & 0x0020) != 0) return "blue";
            return "green";
        }

        private void EnqueueFrame(JObject frame)
        {
            if (_sendQueue == null)
                return;

            try
            {
                string json = frame.ToString(Formatting.None);
                if (!_sendQueue.TryAdd(json))
                {
                    _sendQueue.TryTake(out string dropped);
                    _sendQueue.TryAdd(json);
                }
            }
            catch { }
        }

        private void StartSender()
        {
            _sendQueue = new BlockingCollection<string>(BufferCapacity);
            _senderCts = new CancellationTokenSource();
            _senderThread = new Thread(() => SenderLoop(_senderCts.Token));
            _senderThread.IsBackground = true;
            _senderThread.Start();
        }

        private void StopSender()
        {
            try
            {
                _senderCts?.Cancel();
                _sendQueue?.CompleteAdding();
                if (_senderThread != null && _senderThread.IsAlive)
                    _senderThread.Join(2000);
            }
            catch { }
            finally
            {
                CloseWebSocket();
            }
        }

        private void SenderLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                string message;
                try
                {
                    if (!_sendQueue.TryTake(out message, 500, token))
                        continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    if (!EnsureConnected(token))
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                    SendMessage(message, token);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error($"MyRaceCraftCapture send error: {ex.Message}");
                    CloseWebSocket();
                }
            }
        }

        private bool EnsureConnected(CancellationToken token)
        {
            lock (_wsLock)
            {
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                    return true;

                CloseWebSocketUnsafe();
                try
                {
                    if (!Uri.TryCreate(Settings.ListenerUrl, UriKind.Absolute, out Uri uri))
                        return false;

                    _webSocket = new ClientWebSocket();
                    if (!string.IsNullOrEmpty(Settings.Token))
                        _webSocket.Options.SetRequestHeader("Authorization", "Bearer " + Settings.Token);

                    _webSocket.ConnectAsync(uri, token).GetAwaiter().GetResult();
                    return _webSocket.State == WebSocketState.Open;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error($"MyRaceCraftCapture connect error: {ex.Message}");
                    CloseWebSocketUnsafe();
                    return false;
                }
            }
        }

        private void SendMessage(string message, CancellationToken token)
        {
            lock (_wsLock)
            {
                if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                    return;

                byte[] bytes = Encoding.UTF8.GetBytes(message);
                _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).GetAwaiter().GetResult();
            }
        }

        private void CloseWebSocket()
        {
            lock (_wsLock)
            {
                CloseWebSocketUnsafe();
            }
        }

        private void CloseWebSocketUnsafe()
        {
            try
            {
                if (_webSocket != null)
                {
                    if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
                        _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "plugin stopping", CancellationToken.None).GetAwaiter().GetResult();
                    _webSocket.Dispose();
                }
            }
            catch { }
            finally
            {
                _webSocket = null;
            }
        }
    }
}
