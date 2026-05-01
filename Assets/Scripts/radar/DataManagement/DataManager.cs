using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using radar.data;
using System;
using System.Text;
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

        public float sendFrequencyHz = 10f;
        private bool isDataUpdated_ = false;
        private int doubleDebuffActivedTimes = 0;
        private ushort lastRadarMarkProgress_ = ushort.MaxValue;
        [Header("TCP receiver settings")]
        public bool enableTcpBridge = true;
        public int tcpListenPort = 2000;
        [Header("TCP sender settings")]
        public bool enableTcpSend = true;
        public string tcpSendHost = "192.168.1.10";
        public int tcpSendPort = 2001;
        private TcpListener tcpListener_;
        private Thread tcpReceiveThread_;
        private volatile bool tcpRunning_;
        private TcpClient tcpSendClient_;
        private NetworkStream tcpSendStream_;
        private readonly object tcpSendLock_ = new();
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
                SendRobotPositionData();

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
            if(stateData_.radarInfo.DoubleDebuffChances > 0&&!stateData_.radarInfo.IsDoubleDebuffAble)
            {
                doubleDebuffActivedTimes++;//只有当雷达拥有双倍易伤机会但对方未被触发双倍易伤时，才增加已触发次数
                if(doubleDebuffActivedTimes >= 2)
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

        private void StartTcpReceiver()
        {
            if (!enableTcpBridge) return;
            try
            {
                tcpListener_ = new TcpListener(IPAddress.Any, tcpListenPort);
                tcpListener_.Start();
                tcpRunning_ = true;
                tcpReceiveThread_ = new Thread(TcpReceiveLoop)
                {
                    IsBackground = true
                };
                tcpReceiveThread_.Start();
                LogManager.Instance.log($"[TCP]TCP bridge started at 0.0.0.0:{tcpListenPort}");
            }
            catch (Exception ex)
            {
                tcpRunning_ = false;
                LogManager.Instance.log($"[TCP]Failed to start TCP bridge: {ex}");
            }
        }

        private void StartTcpSender()
        {
            if (!enableTcpSend) return;
            TryConnectTcpSender();
        }

        private void TryConnectTcpSender()
        {
            lock (tcpSendLock_)
            {
                if (tcpSendClient_ != null && tcpSendClient_.Connected) return;
                try
                {
                    tcpSendClient_?.Close();
                    tcpSendClient_ = new TcpClient();
                    tcpSendClient_.Connect(tcpSendHost, tcpSendPort);
                    tcpSendStream_ = tcpSendClient_.GetStream();
                    LogManager.Instance.log($"[TCP]TCP sender connected to {tcpSendHost}:{tcpSendPort}");
                }
                catch (Exception ex)
                {
                    tcpSendStream_ = null;
                    LogManager.Instance.warning($"[TCP]TCP sender connect warning: {ex.Message}");
                }
            }
        }

        public void SendTcpMessage(string message)
        {
            if (!enableTcpSend || string.IsNullOrEmpty(message)) return;
            TryConnectTcpSender();
            lock (tcpSendLock_)
            {
                if (tcpSendStream_ == null || !tcpSendClient_.Connected) return;
                try
                {
                    byte[] payload = Encoding.UTF8.GetBytes(message);
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
            string payload = $"RadarInfo,EncryptionRank={encryptionRank},IsModifyKeyAble={(isModifyKeyAble ? 1 : 0)}\n";
            SendTcpMessage(payload);
            lastSentEncryptionRank_ = encryptionRank;
            lastSentIsModifyKeyAble_ = isModifyKeyAble;
        }

        private void StopTcpReceiver()
        {
            tcpRunning_ = false;
            try
            {
                tcpListener_?.Stop();
            }
            catch (Exception ex)
            {
                LogManager.Instance.warning($"[TCP]Stop TCP listener warning: {ex.Message}");
            }

            try
            {
                if (tcpReceiveThread_ != null && tcpReceiveThread_.IsAlive)
                {
                    tcpReceiveThread_.Join(300);
                }
            }
            catch (Exception ex)
            {
                LogManager.Instance.warning($"[TCP]Join TCP thread warning: {ex.Message}");
            }

            tcpReceiveThread_ = null;
            tcpListener_ = null;
        }

        private void TcpReceiveLoop()
        {
            while (tcpRunning_)
            {
                try
                {
                    if (tcpListener_ == null)
                    {
                        Thread.Sleep(20);
                        continue;
                    }
                    if (!tcpListener_.Pending())
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    using TcpClient client = tcpListener_.AcceptTcpClient();
                    client.ReceiveTimeout = 500;
                    NetworkStream stream = client.GetStream();
                    byte[] readBuffer = new byte[1024];
                    List<byte> cache = new();

                    while (tcpRunning_ && client.Connected)
                    {
                        int readLen;
                        try
                        {
                            readLen = stream.Read(readBuffer, 0, readBuffer.Length);
                        }
                        catch (IOException)
                        {
                            continue;
                        }

                        if (readLen <= 0) break;
                        for (int i = 0; i < readLen; i++)
                        {
                            cache.Add(readBuffer[i]);
                        }
                        TryExtract0A06(cache);
                    }
                }
                catch (SocketException)
                {
                    if (!tcpRunning_) break;
                }
                catch (Exception ex)
                {
                    LogManager.Instance.warning($"[TCP]TCP receive warning: {ex.Message}");
                }
            }
        }

        private void TryExtract0A06(List<byte> cache)
        {
            // 从TCP字节流中查找0x0A06命令（小端序匹配06 0A），并提取其后7字节。
            int index = 0;
            while (cache.Count - index >= 9)
            {
                if (cache[index] == 0x06 && cache[index + 1] == 0x0A)
                {
                    lock (radarDecisionLock_)
                    {
                        for (int i = 0; i < 7; i++)
                        {
                            radarDecisionSuffix_[i] = cache[index + 2 + i];
                        }
                        hasRadarDecisionSuffix_ = true;
                        pendingDecisionCmdCount_++;// 每收到一个0x0A06命令，就增加一次待发送的雷达决策命令计数，确保每个决策命令都能携带最新的密码后缀  
                    }
                    LogManager.Instance.log("[TCP]TCP 0x0A06 parsed, updated 7-byte decision suffix.");
                    cache.RemoveRange(0, index + 9);
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
            StopTcpReceiver();
            lock (tcpSendLock_)
            {
                tcpSendStream_?.Close();
                tcpSendClient_?.Close();
                tcpSendStream_ = null;
                tcpSendClient_ = null;
            }
            instance_ = null;
        }
    }
}