using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;
using radar.data;
using System;
using System.Diagnostics;
using Unity.VisualScripting;
using System.Collections.Concurrent;
using radar.serial.package;
using System.Runtime.InteropServices;
using radar.serial;
using radar.ui.panel;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.IO;

namespace radar.data
{
    public class DataManager : MonoBehaviour
    {
        private const string DefaultPythonExecutable = "python3";
        private const string DefaultPythonScriptPath = "/home/pinkpanda/linux-RADAR/RADAR-2026/RADAR-SDR/thread_init.py";
        public static DataManager Instance
        {
            get
            {
                if (instance_ == null)
                {
                    instance_ = FindAnyObjectByType<DataManager>();
                    if (instance_ == null)
                    {
                        GameObject obj = new("DataManager");
                        instance_ = obj.AddComponent<DataManager>();
                    }
                }
                return instance_;
            }
        }
        private static DataManager instance_;

        public ConcurrentQueue<StateDatas> updatedStateQueue = new();
        public event Action<StateDatas> OnDataUpdated;
        public event Action<int> OnDoubleDebuffChancesEnabled;
        public event Action<ushort> OnRadarMarkProgressUpdated;
        private StateDatas stateData_ = new();
        public ref StateDatas stateData => ref stateData_;    // Read-only property to access the state data

        public float sendFrequencyHz = 5f;
        // Rate limiting for specific serial commands to respect judge protocol limits
        private float lastSent0305Time = 0f; // timestamp of last 0x0305 send
        private readonly float minInterval0305 = 1f / 5f; // 0x0305 max 5Hz
        private bool isDataUpdated_ = false;
        private int doubleDebuffActivedTimes = 0;
        private ushort lastRadarMarkProgress_ = ushort.MaxValue;
        [Header("TCP receiver (client) settings")]
        public bool enableTcpBridge = true;
        public string tcpReceiveHost = "127.0.0.1";
        public int tcpReceivePort = 1400;
        [Header("TCP sender (server) settings")]
        public bool enableTcpSend = true;
        public int tcpSendListenPort = 1500;
        [Header("TCP reconnect settings")]
        public int tcpReconnectDelayMs = 100;
        [Header("Python process settings")]
        public bool enablePythonBridge = true;
        public string pythonExecutable = DefaultPythonExecutable;
        public string pythonScriptPath = DefaultPythonScriptPath;
        public string pythonExtraArgs = "";
        private Thread tcpReceiveThread_;
        private Thread tcpSendAcceptThread_;
        private volatile bool tcpReceiveRunning_;
        private volatile bool tcpSendRunning_;
        private TcpClient tcpReceiveClient_;
        private NetworkStream tcpReceiveStream_;
        private TcpListener tcpSendListener_;
        private TcpClient tcpSendClient_;
        private NetworkStream tcpSendStream_;
        private readonly object tcpSendLock_ = new();
        private Process pythonProcess_;
        private readonly object radarDecisionLock_ = new();
        private readonly byte[] radarDecisionSuffix_ = new byte[7];
        private bool hasRadarDecisionSuffix_ = false;
        private int pendingDecisionCmdCount_ = 0;
        private int lastSentEncryptionRank_ = int.MinValue;
        private bool? lastSentIsModifyKeyAble_ = null;
        public DateTime lastRecordTime = DateTime.Now;
        public int lastRecordTimeSeconds = 0;
        public void Start()
        {
            StartTcpReceiver();
            StartTcpSender();
            Debug.Log("[DataManager] Start called.");
        }

        public void Update()
        {
            DequeueData();
            UpdateData();

            if (isDataUpdated_)
            {
                if (stateData_.radarInfo.RadarMarkProgress != lastRadarMarkProgress_)
                {
                    lastRadarMarkProgress_ = stateData_.radarInfo.RadarMarkProgress;
                    OnRadarMarkProgressUpdated?.Invoke(lastRadarMarkProgress_);
                }
                OnDataUpdated?.Invoke(stateData_);
                isDataUpdated_ = false;
            }

            // Send data at the specified frequency
            if (Time.frameCount % Mathf.RoundToInt(60f / sendFrequencyHz) == 0)
            {
                SendRobotPositionData();
                SendSignalInfoCmd();
            }

            if (pendingDecisionCmdCount_ > 0)
                SendDoubleDebuffCmd();
        }

        public void UploadData<T>(T data, Action<T> updateAction)
        {
            updateAction?.Invoke(data);
            isDataUpdated_ = true;
        }

        // Get data from the queue
        private void DequeueData()
        {
            // Clear the queue if it exceeds 2 items
            while (updatedStateQueue.Count > 2) { updatedStateQueue.TryDequeue(out _); }
            if (updatedStateQueue.TryDequeue(out StateDatas updatedState))
            {
                stateData_ = updatedState;
                isDataUpdated_ = true;
            }
        }


        private void UpdateData()
        {
            TimeSpan timeSinceLastUpdate = DateTime.Now - lastRecordTime;
            stateData_.gameState.GameTimeSeconds = lastRecordTimeSeconds - (int)timeSinceLastUpdate.TotalSeconds;
            if (stateData_.gameState.GameTimeSeconds < 0)
                stateData_.gameState.GameTimeSeconds = 0;

            foreach (var robotState in stateData_.enemyRobots.Data)
            {
                if (!robotState.Value.IsTracked) continue;

                if (DateTime.Now - robotState.Value.LastUpdateTime > TimeSpan.FromSeconds(2))
                {
                    robotState.Value.IsTracked = false;
                    robotState.Value.Position = new Vector2(11.25f, 5.3f);
                    isDataUpdated_ = true;
                    continue;
                }

                // TODO: Predict or flilter
            }

        }

        private void SendDoubleDebuffCmd()
        {
            if (!SerialHandler.Instance.isConnected) return;
            RadarDecisionCmd0121 decisionData;
            if (stateData_.radarInfo.DoubleDebuffChances > 0 && !stateData_.radarInfo.IsDoubleDebuffAble)
            {
                doubleDebuffActivedTimes++;//只有当雷达拥有双倍易伤机会但对方未被触发双倍易伤时，才增加已触发次数
                if (doubleDebuffActivedTimes >= 2)
                {
                    doubleDebuffActivedTimes = 2;//雷达最多只能触发两次双倍易伤
                }
            }
            lock (radarDecisionLock_)
            {
                if (!hasRadarDecisionSuffix_ || pendingDecisionCmdCount_ <= 0) return;
                pendingDecisionCmdCount_--;
                decisionData = BuildRadarDecisionData((byte)doubleDebuffActivedTimes);
            }

            RobotInteraction_Radar robotInteractionData = new()
            {
                dataCmdId = 0x0121,
                senderId = (ushort)(Instance.stateData.gameState.EnemySide == Team.Blue ? 9 : 109),
                receiverId = 0x8080,
                data = decisionData
            };


            byte[] dataToSend = new byte[Marshal.SizeOf(typeof(RobotInteraction_Radar))];
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RobotInteraction_Radar)));
            Marshal.StructureToPtr(robotInteractionData, ptr, true);
            Marshal.Copy(ptr, dataToSend, 0, Marshal.SizeOf(typeof(RobotInteraction_Radar)));
            Marshal.FreeHGlobal(ptr);

            SerialHandler.Instance.SendData(0x0301, dataToSend);

            LogManager.Instance.log($"[DataManager]Send double debuff command:{doubleDebuffActivedTimes}");

            OnDoubleDebuffChancesEnabled?.Invoke(doubleDebuffActivedTimes);
        }

        private RadarDecisionCmd0121 BuildRadarDecisionData(byte radarCmd)
        {
            RadarDecisionCmd0121 data = new() { radar_cmd = radarCmd };
            lock (radarDecisionLock_)
            {
                data.password_cmd = radarDecisionSuffix_[0];
                data.password_1 = radarDecisionSuffix_[1];
                data.password_2 = radarDecisionSuffix_[2];
                data.password_3 = radarDecisionSuffix_[3];
                data.password_4 = radarDecisionSuffix_[4];
                data.password_5 = radarDecisionSuffix_[5];
                data.password_6 = radarDecisionSuffix_[6];
            }
            return data;
        }

        private void SendSignalInfoCmd()
        {
            if (!SerialHandler.Instance.isConnected) return;
            MultiRobotCommData0200 signalData;
            signalData = buildSignalData();
            RobotInteraction_MultiRobot robotInteractionData = new()
            {
                dataCmdId = 0x0200,
                senderId = (ushort)(Instance.stateData.gameState.EnemySide == Team.Blue ? 9 : 109),
                receiverId = (ushort)(Instance.stateData.gameState.EnemySide == Team.Blue ? 107 : 7),
                data = signalData
            };
            byte[] dataToSend = new byte[Marshal.SizeOf(typeof(RobotInteraction_MultiRobot))];
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RobotInteraction_MultiRobot)));
            Marshal.StructureToPtr(robotInteractionData, ptr, true);
            Marshal.Copy(ptr, dataToSend, 0, Marshal.SizeOf(typeof(RobotInteraction_MultiRobot)));
            Marshal.FreeHGlobal(ptr);

            SerialHandler.Instance.SendData(0x0301, dataToSend);
            LogManager.Instance.log(
                $"[DataManager]Send multirobot command: " +
                $"Hero_position: ({signalData.HeroPositionX}, {signalData.HeroPositionY}), " +
                $"Engineer_position: ({signalData.EngineerPositionX}, {signalData.EngineerPositionY}), " +
                $"Soldier3_position: ({signalData.Soldier3PositionX}, {signalData.Soldier3PositionY}), " +
                $"Soldier4_position: ({signalData.Soldier4PositionX}, {signalData.Soldier4PositionY}), " +
                $"Drone_position: ({signalData.DronePositionX}, {signalData.DronePositionY}), " +
                $"Sentry_position: ({signalData.SentryPositionX}, {signalData.SentryPositionY}), " +
                $"Hero_HP: {signalData.HeroHp}, Engineer_HP: {signalData.EngineerHp}, Soldier3_HP: {signalData.Soldier3Hp}, Soldier4_HP: {signalData.Soldier4Hp}, Sentry_HP: {signalData.SentryHp}, " +
                $"Hero_defense_gain: {signalData.HeroDefenseGain}, Engineer_defense_gain: {signalData.EngineerDefenseGain}, Soldier3_defense_gain: {signalData.Soldier3DefenseGain}, Soldier4_defense_gain: {signalData.Soldier4DefenseGain}, Sentry_defense_gain: {signalData.SentryDefenseGain}"
            );
        }
        private MultiRobotCommData0200 buildSignalData()
        {
            GnuradioSignalInfo signalInfo = stateData_.gnuradioSignalInfo;

            MultiRobotCommData0200 data = new()
            {
                HeroPositionX = (ushort)Mathf.Clamp(signalInfo.HeroPositionX, 0, ushort.MaxValue),
                HeroPositionY = (ushort)Mathf.Clamp(signalInfo.HeroPositionY, 0, ushort.MaxValue),
                EngineerPositionX = (ushort)Mathf.Clamp(signalInfo.EngineerPositionX, 0, ushort.MaxValue),
                EngineerPositionY = (ushort)Mathf.Clamp(signalInfo.EngineerPositionY, 0, ushort.MaxValue),
                Soldier3PositionX = (ushort)Mathf.Clamp(signalInfo.Infantry3PositionX, 0, ushort.MaxValue),
                Soldier3PositionY = (ushort)Mathf.Clamp(signalInfo.Infantry3PositionY, 0, ushort.MaxValue),
                Soldier4PositionX = (ushort)Mathf.Clamp(signalInfo.Infantry4PositionX, 0, ushort.MaxValue),
                Soldier4PositionY = (ushort)Mathf.Clamp(signalInfo.Infantry4PositionY, 0, ushort.MaxValue),
                DronePositionX = (ushort)Mathf.Clamp(signalInfo.DronePositionX, 0, ushort.MaxValue),
                DronePositionY = (ushort)Mathf.Clamp(signalInfo.DronePositionY, 0, ushort.MaxValue),
                SentryPositionX = (ushort)Mathf.Clamp(signalInfo.SentryPositionX, 0, ushort.MaxValue),
                SentryPositionY = (ushort)Mathf.Clamp(signalInfo.SentryPositionY, 0, ushort.MaxValue),
                HeroHp = (byte)Mathf.Clamp(signalInfo.HeroHp, 0, byte.MaxValue),
                EngineerHp = (byte)Mathf.Clamp(signalInfo.EngineerHp, 0, byte.MaxValue),
                Soldier3Hp = (byte)Mathf.Clamp(signalInfo.Infantry3Hp, 0, byte.MaxValue),
                Soldier4Hp = (byte)Mathf.Clamp(signalInfo.Infantry4Hp, 0, byte.MaxValue),
                SentryHp = (byte)Mathf.Clamp(signalInfo.SentryHp, 0, byte.MaxValue),
                HeroDefenseGain = (byte)Mathf.Clamp(signalInfo.HeroDefenseGain, 0, byte.MaxValue),
                EngineerDefenseGain = (byte)Mathf.Clamp(signalInfo.EngineerDefenseGain, 0, byte.MaxValue),
                Soldier3DefenseGain = (byte)Mathf.Clamp(signalInfo.Infantry3DefenseGain, 0, byte.MaxValue),
                Soldier4DefenseGain = (byte)Mathf.Clamp(signalInfo.Infantry4DefenseGain, 0, byte.MaxValue),
                SentryDefenseGain = (byte)Mathf.Clamp(signalInfo.SentryDefenseGain, 0, byte.MaxValue)
            };

            return data;
        }
        private void StartTcpReceiver()
        {
            if (!enableTcpBridge) return;
            tcpReceiveRunning_ = true;
            tcpReceiveThread_ = new Thread(TcpReceiveLoop)
            {
                IsBackground = true
            };
            tcpReceiveThread_.Start();
            LogManager.Instance.log($"[TCP]TCP receiver client starting, target {tcpReceiveHost}:{tcpReceivePort}");
        }

        private void StartTcpSender()
        {
            if (!enableTcpSend) return;
            try
            {
                tcpSendListener_ = new TcpListener(IPAddress.Any, tcpSendListenPort);
                tcpSendListener_.Start();
                tcpSendRunning_ = true;
                tcpSendAcceptThread_ = new Thread(TcpSendAcceptLoop)
                {
                    IsBackground = true
                };
                tcpSendAcceptThread_.Start();
                LogManager.Instance.log($"[TCP]TCP sender server listening at 0.0.0.0:{tcpSendListenPort}");
            }
            catch (Exception ex)
            {
                tcpSendRunning_ = false;
                LogManager.Instance.warning($"[TCP]Failed to start TCP sender server: {ex}");
            }
        }

        public void StartPythonProcess()
        {
            if (!enablePythonBridge) return;
            if (pythonProcess_ != null && !pythonProcess_.HasExited) return;
            string resolvedExecutable = string.IsNullOrWhiteSpace(pythonExecutable)
                ? DefaultPythonExecutable
                : pythonExecutable;
            string resolvedScriptPath = string.IsNullOrWhiteSpace(pythonScriptPath)
                ? DefaultPythonScriptPath
                : pythonScriptPath;

            string enemySideText = stateData_.gameState.EnemySide == Team.Blue ? "Blue" : "Red";
            string args = $"\"{resolvedScriptPath}\" --enemySide \"{enemySideText}\"";
            if (!string.IsNullOrWhiteSpace(pythonExtraArgs))
                args += " " + pythonExtraArgs;

            LogManager.Instance.log($"[Python]Launching with enemySide={enemySideText}");
            LogManager.Instance.log($"[Python]Script: {resolvedScriptPath}");

            ProcessStartInfo startInfo = new()
            {
                FileName = resolvedExecutable,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            try
            {
                pythonProcess_ = Process.Start(startInfo);
                if (pythonProcess_ != null)
                {
                    pythonProcess_.OutputDataReceived += OnPythonOutputDataReceived;
                    pythonProcess_.ErrorDataReceived += OnPythonErrorDataReceived;
                    pythonProcess_.BeginOutputReadLine();
                    pythonProcess_.BeginErrorReadLine();
                }
                LogManager.Instance.log($"[Python]Started: {resolvedExecutable} {args}");
            }
            catch (Exception ex)
            {
                LogManager.Instance.warning($"[Python]Start failed: {ex.Message}");
            }
        }

        private void OnPythonOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            LogManager.Instance.log("[Python]" + e.Data);
        }

        private void OnPythonErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            LogManager.Instance.warning("[Python]" + e.Data);
        }

        private void StopPythonProcess()
        {
            if (pythonProcess_ == null) return;
            try
            {
                if (!pythonProcess_.HasExited)
                {
                    try
                    {
                        pythonProcess_.CancelOutputRead();
                    }
                    catch
                    {
                    }
                    try
                    {
                        pythonProcess_.CancelErrorRead();
                    }
                    catch
                    {
                    }
                    pythonProcess_.Kill();
                    pythonProcess_.WaitForExit(1000);
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.warning($"[Python]Stop warning: {ex.Message}");
            }
            finally
            {
                pythonProcess_.OutputDataReceived -= OnPythonOutputDataReceived;
                pythonProcess_.ErrorDataReceived -= OnPythonErrorDataReceived;
                pythonProcess_.Dispose();
                pythonProcess_ = null;
            }
        }

        public void SendTcpBytes(byte[] payload)
        {
            if (!enableTcpSend || payload == null || payload.Length == 0) return;
            lock (tcpSendLock_)
            {
                if (tcpSendStream_ == null || !tcpSendClient_.Connected) return;
                try
                {
                    tcpSendStream_.Write(payload, 0, payload.Length);
                    LogManager.Instance.log($"[TCP]TCP sender wrote {payload.Length} bytes.");
                }
                catch (Exception ex)
                {
                    LogManager.Instance.warning($"[TCP]TCP sender write warning: {ex.Message}");
                }
            }
        }

        public void SendRadarInfoToTcp(int encryptionRank, bool isModifyKeyAble)
        {
            byte[] payload =
            {
                0x02,
                0x0E,
                (byte)encryptionRank,
                (byte)(isModifyKeyAble ? 1 : 0),
            };
            SendTcpBytes(payload);
            lastSentEncryptionRank_ = encryptionRank;
            lastSentIsModifyKeyAble_ = isModifyKeyAble;
        }

        private void StopTcpReceiver()
        {
            tcpReceiveRunning_ = false;
            try
            {
                if (tcpReceiveThread_ != null && tcpReceiveThread_.IsAlive)
                {
                    tcpReceiveThread_.Join(300);
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.warning($"[TCP]Join receiver thread warning: {ex.Message}");
            }

            CloseTcpReceiveConnection();

            tcpReceiveThread_ = null;
        }

        private void CloseTcpReceiveConnection()
        {
            lock (tcpSendLock_)
            {
                tcpReceiveStream_?.Close();
                tcpReceiveClient_?.Close();
                tcpReceiveStream_ = null;
                tcpReceiveClient_ = null;
            }
        }

        private void TcpReceiveLoop()
        {
            byte[] readBuffer = new byte[64];
            List<byte> cache = new();
            while (tcpReceiveRunning_)
            {
                try
                {
                    if (tcpReceiveClient_ == null || !tcpReceiveClient_.Connected)
                    {
                        CloseTcpReceiveConnection();

                        try
                        {
                            TcpClient client = new TcpClient();
                            client.Connect(tcpReceiveHost, tcpReceivePort);
                            client.ReceiveTimeout = 1000;
                            lock (tcpSendLock_)
                            {
                                tcpReceiveClient_ = client;
                                tcpReceiveStream_ = client.GetStream();
                                tcpReceiveStream_.ReadTimeout = 1000;
                            }
                            LogManager.Instance.log($"[TCP]TCP receiver client connected to {tcpReceiveHost}:{tcpReceivePort}");
                        }
                        catch (Exception ex)
                        {
                            LogManager.Instance.warning($"[TCP]TCP receiver connect warning: {ex.Message}");
                            Thread.Sleep(tcpReconnectDelayMs);
                            continue;
                        }
                    }

                    NetworkStream stream;
                    lock (tcpSendLock_)
                    {
                        stream = tcpReceiveStream_;
                    }
                    if (stream == null)
                    {
                        Thread.Sleep(tcpReconnectDelayMs);
                        continue;
                    }

                    int readLen;
                    try
                    {
                        readLen = stream.Read(readBuffer, 0, readBuffer.Length);
                    }
                    catch (IOException ex) when (ex.InnerException is SocketException socketEx
                                                 && socketEx.SocketErrorCode == SocketError.TimedOut)
                    {
                        continue;
                    }
                    catch (IOException)
                    {
                        CloseTcpReceiveConnection();
                        Thread.Sleep(tcpReconnectDelayMs);
                        continue;
                    }

                    if (readLen <= 0)
                    {
                        CloseTcpReceiveConnection();
                        Thread.Sleep(tcpReconnectDelayMs);
                        continue;
                    }

                    for (int i = 0; i < readLen; i++)
                    {
                        cache.Add(readBuffer[i]);
                    }
                    //LogManager.Instance.log($"[TCP]TCP cache size: {cache.Count} bytes.");
                    //LogManager.Instance.log($"[TCP]TCP cache: {string.Join(", ", cache)}");
                    TryExtractNoiseKey(cache);
                    TryExtractSignalInfo(cache);
                }
                catch (SocketException)
                {
                    if (!tcpReceiveRunning_) break;
                    Thread.Sleep(tcpReconnectDelayMs);
                }
                catch (Exception ex)
                {
                    LogManager.Instance.warning($"[TCP]TCP receive warning: {ex.Message}");
                    Thread.Sleep(tcpReconnectDelayMs);
                }
            }
        }

        private void TcpSendAcceptLoop()
        {
            while (tcpSendRunning_)
            {
                try
                {
                    if (tcpSendListener_ == null)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    if (!tcpSendListener_.Pending())
                    {
                        Thread.Sleep(20);
                        continue;
                    }

                    TcpClient client = tcpSendListener_.AcceptTcpClient();
                    client.SendTimeout = 500;
                    lock (tcpSendLock_)
                    {
                        tcpSendStream_?.Close();
                        tcpSendClient_?.Close();
                        tcpSendClient_ = client;
                        tcpSendStream_ = client.GetStream();
                    }
                    LogManager.Instance.log("[TCP]TCP sender accepted client.");
                }
                catch (SocketException)
                {
                    if (!tcpSendRunning_) break;
                    Thread.Sleep(tcpReconnectDelayMs);
                }
                catch (Exception ex)
                {
                    LogManager.Instance.warning($"[TCP]TCP sender accept warning: {ex.Message}");
                    Thread.Sleep(tcpReconnectDelayMs);
                }
            }
        }

        private void TryExtractNoiseKey(List<byte> cache)
        {
            // 从TCP字节流中按帧查找0x0A06 + 7字节密码。
            const int DecisionBytes = 7;
            const int FrameBytes = 2 + DecisionBytes;
            int index = 0;
            while (cache.Count - index >= 2)
            {
                if (cache[index] == 0x0A && cache[index + 1] == 0x06)
                {
                    if (cache.Count - index < FrameBytes)
                    {
                        break;
                    }

                    lock (radarDecisionLock_)
                    {
                        for (int i = 0; i < DecisionBytes; i++)
                        {
                            radarDecisionSuffix_[i] = cache[index + 2 + i];
                        }
                        hasRadarDecisionSuffix_ = true;
                        pendingDecisionCmdCount_++;// 每收到一个0x0A06命令，就增加一次待发送的雷达决策命令计数，确保每个决策命令都能携带最新的密码后缀
                    }
                    stateData_.gnuradioNoiseKey.Behavior = radarDecisionSuffix_[0];
                    stateData_.gnuradioNoiseKey.Key1 = radarDecisionSuffix_[1];
                    stateData_.gnuradioNoiseKey.Key2 = radarDecisionSuffix_[2];
                    stateData_.gnuradioNoiseKey.Key3 = radarDecisionSuffix_[3];
                    stateData_.gnuradioNoiseKey.Key4 = radarDecisionSuffix_[4];
                    stateData_.gnuradioNoiseKey.Key5 = radarDecisionSuffix_[5];
                    stateData_.gnuradioNoiseKey.Key6 = radarDecisionSuffix_[6];
                    LogManager.Instance.log("[TCP]TCP 0x0A06 parsed, updated 7-byte decision suffix.");
                    LogManager.Instance.log($"[TCP]New decision suffix: {string.Join(", ", radarDecisionSuffix_)}");

                    cache.RemoveRange(0, index + FrameBytes);
                    index = 0;
                    continue;
                }
                index++;
            }
        }

        private void TryExtractSignalInfo(List<byte> cache)
        {
            // 从TCP字节流中按帧查找0x0A07 + 24字节位置 + 5字节血量 + 5字节防御增益。
            const int SignalInfoBytes = 34;
            const int FrameBytes = 2 + SignalInfoBytes;
            int index = 0;

            while (cache.Count - index >= 2)
            {
                if (cache[index] == 0x0A && cache[index + 1] == 0x07)
                {
                    if (cache.Count - index < FrameBytes)
                    {
                        break;
                    }
                    stateData_.gnuradioSignalInfo.HeroPositionX = (int)cache[index + 2] << 8 | cache[index + 3];
                    stateData_.gnuradioSignalInfo.HeroPositionY = (int)cache[index + 4] << 8 | cache[index + 5];
                    stateData_.gnuradioSignalInfo.EngineerPositionX = (int)cache[index + 6] << 8 | cache[index + 7];
                    stateData_.gnuradioSignalInfo.EngineerPositionY = (int)cache[index + 8] << 8 | cache[index + 9];
                    stateData_.gnuradioSignalInfo.Infantry3PositionX = (int)cache[index + 10] << 8 | cache[index + 11];
                    stateData_.gnuradioSignalInfo.Infantry3PositionY = (int)cache[index + 12] << 8 | cache[index + 13];
                    stateData_.gnuradioSignalInfo.Infantry4PositionX = (int)cache[index + 14] << 8 | cache[index + 15];
                    stateData_.gnuradioSignalInfo.Infantry4PositionY = (int)cache[index + 16] << 8 | cache[index + 17];
                    stateData_.gnuradioSignalInfo.DronePositionX = (int)cache[index + 18] << 8 | cache[index + 19];
                    stateData_.gnuradioSignalInfo.DronePositionY = (int)cache[index + 20] << 8 | cache[index + 21];
                    stateData_.gnuradioSignalInfo.SentryPositionX = (int)cache[index + 22] << 8 | cache[index + 23];
                    stateData_.gnuradioSignalInfo.SentryPositionY = (int)cache[index + 24] << 8 | cache[index + 25];
                    stateData_.gnuradioSignalInfo.HeroHp = cache[index + 26];
                    stateData_.gnuradioSignalInfo.EngineerHp = cache[index + 27];
                    stateData_.gnuradioSignalInfo.Infantry3Hp = cache[index + 28];
                    stateData_.gnuradioSignalInfo.Infantry4Hp = cache[index + 29];
                    stateData_.gnuradioSignalInfo.SentryHp = cache[index + 30];
                    stateData_.gnuradioSignalInfo.HeroDefenseGain = cache[index + 31];
                    stateData_.gnuradioSignalInfo.EngineerDefenseGain = cache[index + 32];
                    stateData_.gnuradioSignalInfo.Infantry3DefenseGain = cache[index + 33];
                    stateData_.gnuradioSignalInfo.Infantry4DefenseGain = cache[index + 34];
                    stateData_.gnuradioSignalInfo.SentryDefenseGain = cache[index + 35];
                    LogManager.Instance.log("[TCP]TCP 0x0A07 parsed, updated signal info.");
                    LogManager.Instance.log($"[TCP]New signal info: " +
                        $"Hero: ({stateData_.gnuradioSignalInfo.HeroPositionX}, {stateData_.gnuradioSignalInfo.HeroPositionY}, HP: {stateData_.gnuradioSignalInfo.HeroHp}, DefGain: {stateData_.gnuradioSignalInfo.HeroDefenseGain}), " +
                        $"Engineer: ({stateData_.gnuradioSignalInfo.EngineerPositionX}, {stateData_.gnuradioSignalInfo.EngineerPositionY}, HP: {stateData_.gnuradioSignalInfo.EngineerHp}, DefGain: {stateData_.gnuradioSignalInfo.EngineerDefenseGain}), " +
                        $"Infantry3: ({stateData_.gnuradioSignalInfo.Infantry3PositionX}, {stateData_.gnuradioSignalInfo.Infantry3PositionY}, HP: {stateData_.gnuradioSignalInfo.Infantry3Hp}, DefGain: {stateData_.gnuradioSignalInfo.Infantry3DefenseGain}), " +
                        $"Infantry4: ({stateData_.gnuradioSignalInfo.Infantry4PositionX}, {stateData_.gnuradioSignalInfo.Infantry4PositionY}, HP: {stateData_.gnuradioSignalInfo.Infantry4Hp}, DefGain: {stateData_.gnuradioSignalInfo.Infantry4DefenseGain}), " +
                        $"Drone: ({stateData_.gnuradioSignalInfo.DronePositionX}, {stateData_.gnuradioSignalInfo.DronePositionY}), " +
                        $"Sentry: ({stateData_.gnuradioSignalInfo.SentryPositionX}, {stateData_.gnuradioSignalInfo.SentryPositionY}, HP: {stateData_.gnuradioSignalInfo.SentryHp}, DefGain: {stateData_.gnuradioSignalInfo.SentryDefenseGain})");
                    cache.RemoveRange(0, index + FrameBytes);
                    index = 0;
                    continue;
                }
                index++;
            }

            if (index > 0)
            {
                cache.RemoveRange(0, index);
            }
            if (cache.Count > 256)
            {
                cache.RemoveRange(0, cache.Count - 256);
            }
        }

        private void SendRobotPositionData()
        {
            if (!SerialHandler.Instance.isConnected) return;
            // Enforce 0x0305 rate limit (max 5Hz) to comply with judge protocol
            if (Time.time - lastSent0305Time < minInterval0305) return;

            Vector2 ToRobotCoordinate(Vector3 position)
            {
                Vector2 location =
                    stateData.gameState.EnemySide == Team.Blue
                        ? new Vector2(position.x + 14f, position.y + 7.5f)
                        : new Vector2(28f - (position.x + 14f), 15f - (position.y + 7.5f));
                return new Vector2(location.x * 100f, location.y * 100f);
            }

            Vector2 enemyHero = ToRobotCoordinate(stateData_.enemyRobots.Data[RobotType.Hero].Position);
            Vector2 enemyEngineer = ToRobotCoordinate(stateData_.enemyRobots.Data[RobotType.Engineer].Position);
            Vector2 enemyInfantry3 = ToRobotCoordinate(stateData_.enemyRobots.Data[RobotType.Infantry3].Position);
            Vector2 enemyInfantry4 = ToRobotCoordinate(stateData_.enemyRobots.Data[RobotType.Infantry4].Position);
            Vector2 enemyAerial = ToRobotCoordinate(stateData_.enemyFacilities.Data[RobotType.Drone].Position);
            Vector2 enemySentry = ToRobotCoordinate(stateData_.enemyRobots.Data[RobotType.Sentry].Position);

            Vector2 allyHero = ToRobotCoordinate(stateData_.allieRobots.Data[RobotType.Hero].Position);
            Vector2 allyEngineer = ToRobotCoordinate(stateData_.allieRobots.Data[RobotType.Engineer].Position);
            Vector2 allyInfantry3 = ToRobotCoordinate(stateData_.allieRobots.Data[RobotType.Infantry3].Position);
            Vector2 allyInfantry4 = ToRobotCoordinate(stateData_.allieRobots.Data[RobotType.Infantry4].Position);
            Vector2 allyAerial = ToRobotCoordinate(stateData_.allieFacilities.Data[RobotType.Drone].Position);
            Vector2 allySentry = ToRobotCoordinate(stateData_.allieRobots.Data[RobotType.Sentry].Position);

            MapRobotData mapDataToSend = new()
            {
                OpponentHeroPositionX = (ushort)enemyHero.x,
                OpponentHeroPositionY = (ushort)enemyHero.y,
                OpponentEngineerPositionX = (ushort)enemyEngineer.x,
                OpponentEngineerPositionY = (ushort)enemyEngineer.y,
                OpponentInfantry3PositionX = (ushort)enemyInfantry3.x,
                OpponentInfantry3PositionY = (ushort)enemyInfantry3.y,
                OpponentInfantry4PositionX = (ushort)enemyInfantry4.x,
                OpponentInfantry4PositionY = (ushort)enemyInfantry4.y,
                OpponentAerialPositionX = (ushort)enemyAerial.x,
                OpponentAerialPositionY = (ushort)enemyAerial.y,
                OpponentSentryPositionX = (ushort)enemySentry.x,
                OpponentSentryPositionY = (ushort)enemySentry.y,
                AllyHeroPositionX = (ushort)allyHero.x,
                AllyHeroPositionY = (ushort)allyHero.y,
                AllyEngineerPositionX = (ushort)allyEngineer.x,
                AllyEngineerPositionY = (ushort)allyEngineer.y,
                AllyInfantry3PositionX = (ushort)allyInfantry3.x,
                AllyInfantry3PositionY = (ushort)allyInfantry3.y,
                AllyInfantry4PositionX = (ushort)allyInfantry4.x,
                AllyInfantry4PositionY = (ushort)allyInfantry4.y,
                AllyAerialPositionX = (ushort)allyAerial.x,
                AllyAerialPositionY = (ushort)allyAerial.y,
                AllySentryPositionX = (ushort)allySentry.x,
                AllySentryPositionY = (ushort)allySentry.y
            };

            byte[] dataToSend = new byte[Marshal.SizeOf(typeof(MapRobotData))];
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MapRobotData)));
            Marshal.StructureToPtr(mapDataToSend, ptr, true);
            Marshal.Copy(ptr, dataToSend, 0, Marshal.SizeOf(typeof(MapRobotData)));
            Marshal.FreeHGlobal(ptr);

            SerialHandler.Instance.SendData(0x0305, dataToSend);
            lastSent0305Time = Time.time;

            LogManager.Instance.log("[DataManager]Send data: {" +
                $"OpponentHero: ({mapDataToSend.OpponentHeroPositionX}, {mapDataToSend.OpponentHeroPositionY}), " +
                $"OpponentEngineer: ({mapDataToSend.OpponentEngineerPositionX}, {mapDataToSend.OpponentEngineerPositionY}), " +
                $"OpponentInfantry3: ({mapDataToSend.OpponentInfantry3PositionX}, {mapDataToSend.OpponentInfantry3PositionY}), " +
                $"OpponentInfantry4: ({mapDataToSend.OpponentInfantry4PositionX}, {mapDataToSend.OpponentInfantry4PositionY}), " +
                $"OpponentAerial: ({mapDataToSend.OpponentAerialPositionX}, {mapDataToSend.OpponentAerialPositionY}), " +
                $"OpponentSentry: ({mapDataToSend.OpponentSentryPositionX}, {mapDataToSend.OpponentSentryPositionY}), " +
                $"AllyHero: ({mapDataToSend.AllyHeroPositionX}, {mapDataToSend.AllyHeroPositionY}), " +
                $"AllyEngineer: ({mapDataToSend.AllyEngineerPositionX}, {mapDataToSend.AllyEngineerPositionY}), " +
                $"AllyInfantry3: ({mapDataToSend.AllyInfantry3PositionX}, {mapDataToSend.AllyInfantry3PositionY}), " +
                $"AllyInfantry4: ({mapDataToSend.AllyInfantry4PositionX}, {mapDataToSend.AllyInfantry4PositionY}), " +
                $"AllyAerial: ({mapDataToSend.AllyAerialPositionX}, {mapDataToSend.AllyAerialPositionY}), " +
                $"AllySentry: ({mapDataToSend.AllySentryPositionX}, {mapDataToSend.AllySentryPositionY})" +
                "}");
        }
        private void OnDestroy()
        {
            StopPythonProcess();
            StopTcpReceiver();
            tcpSendRunning_ = false;
            try
            {
                tcpSendListener_?.Stop();
            }
            catch (Exception ex)
            {
                LogManager.Instance.warning($"[TCP]Stop sender listener warning: {ex.Message}");
            }

            try
            {
                if (tcpSendAcceptThread_ != null && tcpSendAcceptThread_.IsAlive)
                {
                    tcpSendAcceptThread_.Join(300);
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.warning($"[TCP]Join sender thread warning: {ex.Message}");
            }

            lock (tcpSendLock_)
            {
                tcpSendStream_?.Close();
                tcpSendClient_?.Close();
                tcpSendStream_ = null;
                tcpSendClient_ = null;
            }
            tcpSendAcceptThread_ = null;
            tcpSendListener_ = null;
            instance_ = null;
        }
    }
}