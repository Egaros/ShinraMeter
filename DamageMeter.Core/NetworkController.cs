﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using DamageMeter.AutoUpdate;
using DamageMeter.Database.Structures;
using DamageMeter.Sniffing;
using DamageMeter.TeraDpsApi;
using Data;
using log4net;
using Lang;
using Tera.Game;
using Tera.Game.Abnormality;
using Tera.Game.Messages;
using Message = Tera.Message;
using DamageMeter.Processing;
using Data.Actions.Notify;

namespace DamageMeter
{
    public class NetworkController
    {
        public delegate void ConnectedHandler(string serverName);

        public delegate void SetClickThrouEvent();

        public delegate void UnsetClickThrouEvent();


        public delegate void UpdateUiHandler(
            StatsSummary statsSummary, Skills skills, List<NpcEntity> entities, bool timedEncounter,
            AbnormalityStorage abnormals,
            ConcurrentDictionary<string, NpcEntity> bossHistory, List<ChatMessage> chatbox, int packetWaiting, NotifyFlashMessage flash);

        public delegate void GuildIconEvent(Bitmap icon);

        private static NetworkController _instance;
        internal readonly AbnormalityStorage AbnormalityStorage;
        internal AbnormalityTracker AbnormalityTracker;
        public NotifyFlashMessage FlashMessage { get; set; }

        private bool _clickThrou;
        private static object pasteLock=new object();
        private bool _forceUiUpdate;
        internal bool NeedInit = true;
        private long _lastTick;
        internal MessageFactory MessageFactory = new MessageFactory();
        internal UserLogoTracker UserLogoTracker = new UserLogoTracker();

        internal readonly List<Player> MeterPlayers=new List<Player>();
        public ConcurrentDictionary<string, NpcEntity> BossLink = new ConcurrentDictionary<string, NpcEntity>();
        public CopyKey NeedToCopy;

        public DataExporter.Dest NeedToExport = DataExporter.Dest.None;
        public bool NeedToReset;
        public bool NeedToResetCurrent;
        public PlayerTracker PlayerTracker { get; internal set; }
        public Server Server;
        public GlyphBuild Glyphs = new GlyphBuild();

        private NetworkController()
        {
            TeraSniffer.Instance.NewConnection += HandleNewConnection;
            TeraSniffer.Instance.EndConnection += HandleEndConnection;
            AbnormalityStorage = new AbnormalityStorage();
            var packetAnalysis = new Thread(PacketAnalysisLoop);
            packetAnalysis.Start();
        }

        public TeraData TeraData { get; internal set; }
        public NpcEntity Encounter { get; private set; }
        public NpcEntity NewEncounter { get; set; }

        public bool TimedEncounter { get; set; }

        public static NetworkController Instance => _instance ?? (_instance = new NetworkController());

        public EntityTracker EntityTracker { get; internal set; }

        public event SetClickThrouEvent SetClickThrouAction;
        public event GuildIconEvent GuildIconAction;
        public event UnsetClickThrouEvent UnsetClickThrouAction;
        private bool KeepAlive = true;
        public bool SendFullDetails { get; set; }
        public void Exit()
        {
            BasicTeraData.Instance.WindowData.Save();
            BasicTeraData.Instance.HotkeysData.Save();
            TeraSniffer.Instance.Enabled = false;
            KeepAlive = false;
            Application.Exit();
        }

        internal void RaiseConnected(string message)
        {
            Connected(message);
        }

        public event ConnectedHandler Connected;
        public event UpdateUiHandler TickUpdated;

        protected virtual void HandleEndConnection()
        {
            NeedInit = true;
            MessageFactory = new MessageFactory();
            Connected?.Invoke(LP.SystemTray_No_server);
            OnGuildIconAction(null);
        }

        protected virtual void HandleNewConnection(Server server)
        {
            Server = server;
            NeedInit = true;
            MessageFactory = new MessageFactory();
            Connected?.Invoke(server.Name);
        }

        public void Reset()
        {
            DamageTracker.Instance.Reset();
            AbnormalityStorage.ClearEnded();
            _forceUiUpdate = true;
        }

        public void ResetCurrent()
        {
            DamageTracker.Instance.DeleteEntity(Encounter);
            _forceUiUpdate = true;
        }

        private void UpdateUi(int packetsWaiting = 0)
        {
            if (!NeedInit)
            {
                if (BasicTeraData.Instance.WindowData.EnableChat != MessageFactory.ChatEnabled)
                {
                    MessageFactory.ChatEnabled = BasicTeraData.Instance.WindowData.EnableChat;
                    if (BasicTeraData.Instance.WindowData.EnableChat)
                    {
                        AbnormalityTracker.AbnormalityAdded += NotifyProcessor.Instance.AbnormalityNotifierAdded;
                        AbnormalityTracker.AbnormalityRemoved += NotifyProcessor.Instance.AbnormalityNotifierRemoved;
                    }
                    else
                    {
                        AbnormalityTracker.AbnormalityAdded -= NotifyProcessor.Instance.AbnormalityNotifierAdded;
                        AbnormalityTracker.AbnormalityRemoved -= NotifyProcessor.Instance.AbnormalityNotifierRemoved;
                    }
                    PacketProcessing.Update();
                }
                NotifyProcessor.Instance.AbnormalityNotifierMissing();
            }
            _lastTick = DateTime.UtcNow.Ticks;
            var handler = TickUpdated;
            var currentBoss = Encounter;
            var timedEncounter = TimedEncounter;

            var entities = Database.Database.Instance.AllEntity();
            var filteredEntities = entities.Select(entityid => EntityTracker.GetOrNull(entityid)).OfType<NpcEntity>().Where(npc => npc.Info.Boss).ToList();
            if (packetsWaiting > 1500 && filteredEntities.Count > 1)
            {
                Database.Database.Instance.DeleteAllWhenTimeBelow(Encounter);
                entities = Database.Database.Instance.AllEntity();
                filteredEntities = entities.Select(entityid => EntityTracker.GetOrNull(entityid)).OfType<NpcEntity>().Where(npc => npc.Info.Boss).ToList();
            }

            var entityInfo = Database.Database.Instance.GlobalInformationEntity(currentBoss, timedEncounter);
            if (currentBoss != null)
            {
                long entityHP = 0;
                NotifyProcessor.Instance._lastBosses.TryGetValue(currentBoss.Id, out entityHP);
                var entityDamaged = currentBoss.Info.HP - entityHP;
                entityInfo.TimeLeft = entityDamaged == 0 ? 0 : entityInfo.Interval * entityHP / entityDamaged;
            }
            Skills skills = null; 
            if (SendFullDetails)
            {
                skills = Database.Database.Instance.GetSkills(entityInfo.BeginTime, entityInfo.EndTime);
                SendFullDetails = false;
            }
            var playersInfo = timedEncounter
                ? Database.Database.Instance.PlayerDamageInformation(entityInfo.BeginTime, entityInfo.EndTime)
                : Database.Database.Instance.PlayerDamageInformation(currentBoss);
            if (BasicTeraData.Instance.WindowData.MeterUserOnTop)
                playersInfo = playersInfo.OrderBy(x => MeterPlayers.Contains(x.Source) ? 0 : 1).ThenByDescending(x => x.Amount).ToList();

            var heals = Database.Database.Instance.PlayerHealInformation(entityInfo.BeginTime, entityInfo.EndTime);

            var flash = FlashMessage;
            FlashMessage = null;
       
            var statsSummary = new StatsSummary(playersInfo, heals, entityInfo);
            var teradpsHistory = BossLink;
            var chatbox = Chat.Instance.Get();
            var abnormals = AbnormalityStorage.Clone(currentBoss, entityInfo.BeginTime, entityInfo.EndTime);
            handler?.Invoke(statsSummary, skills, filteredEntities, timedEncounter, abnormals, teradpsHistory, chatbox, packetsWaiting, flash);
        }

        public void SwitchClickThrou()
        {
            if (_clickThrou)
            {
                UnsetClickThrou();
                _clickThrou = false;
                return;
            }
            SetClickThrou();
            _clickThrou = true;
        }

        public void SwitchClickThrou(bool value)
        {
            if (value)
            {
                SetClickThrou();
                _clickThrou = true;
                return;
            }
            UnsetClickThrou();
            _clickThrou = false;
            
        }

        protected virtual void SetClickThrou()
        {
            SetClickThrouAction?.Invoke();
        }

        protected virtual void UnsetClickThrou()
        {
            UnsetClickThrouAction?.Invoke();
        }

        public static void CopyThread(StatsSummary stats, Skills skills, AbnormalityStorage abnormals,
            bool timedEncounter, CopyKey copy)
        {
            if (BasicTeraData.Instance.HotDotDatabase == null) return;//no database loaded yet => no need to do anything
            lock (pasteLock)
            {
                var text = CopyPaste.Copy(stats, skills, abnormals, timedEncounter, copy.Header, copy.Content,
                    copy.Footer,
                    copy.OrderBy, copy.Order,copy.LowDpsContent,copy.LowDpsThreshold);
                for (var i = 0; i < 3; i++)
                {
                    try
                    {
                        Clipboard.SetText(text.Item2);
                        break;
                    }
                    catch
                    {
                        Thread.Sleep(100);
                        //Ignore
                    }
                }
                CopyPaste.Paste(text.Item1);
            }
        }

        internal PacketProcessingFactory PacketProcessing = new PacketProcessingFactory();

        private void PacketAnalysisLoop()
        {
            try { Database.Database.Instance.DeleteAll(); }
            catch (Exception ex)
            {
                BasicTeraData.LogError(ex.Message + "\r\n" + ex.StackTrace + "\r\n" + ex.Source + "\r\n" + ex + "\r\n" + ex.Data + "\r\n" + ex.InnerException + "\r\n" + ex.TargetSite, true);
                MessageBox.Show(LP.MainWindow_Fatal_error);
                Exit();
            }

            while (KeepAlive)
            {
                if (NeedToCopy != null)
                {
                    var currentBoss = Encounter;
                    var timedEncounter = TimedEncounter;

                    var entityInfo = Database.Database.Instance.GlobalInformationEntity(currentBoss, timedEncounter);
                    var skills = Database.Database.Instance.GetSkills(entityInfo.BeginTime, entityInfo.EndTime);
                    var playersInfo = timedEncounter
                        ? Database.Database.Instance.PlayerDamageInformation(entityInfo.BeginTime, entityInfo.EndTime)
                        : Database.Database.Instance.PlayerDamageInformation(currentBoss);
                    var heals = Database.Database.Instance.PlayerHealInformation(entityInfo.BeginTime,
                        entityInfo.EndTime);
                    var statsSummary = new StatsSummary(playersInfo, heals, entityInfo);

                    var tmpcopy = NeedToCopy;
                    var abnormals = AbnormalityStorage.Clone(currentBoss, entityInfo.BeginTime, entityInfo.EndTime);
                    var pasteThread =
                        new Thread(() => CopyThread(statsSummary, skills, abnormals, timedEncounter, tmpcopy))
                        {
                            Priority = ThreadPriority.Highest
                            
                        };
                    pasteThread.SetApartmentState(ApartmentState.STA);
                    pasteThread.Start();

                    NeedToCopy = null;
                }

                if (NeedToReset)
                {
                    Reset();
                    NeedToReset = false;
                }

                if (NeedToResetCurrent)
                {
                    ResetCurrent();
                    NeedToResetCurrent = false;
                }

                if (!NeedToExport.HasFlag(DataExporter.Dest.None))
                {
                    DataExporter.ManualExport(Encounter, AbnormalityStorage, NeedToExport);
                    NeedToExport = DataExporter.Dest.None;
                }

                Encounter = NewEncounter;

                var packetsWaiting = TeraSniffer.Instance.Packets.Count;
                if (packetsWaiting > 3000)
                {
                    MessageBox.Show(
                        LP.Your_computer_is_too_slow);
                    Exit();
                }

                if (_forceUiUpdate)
                {
                    UpdateUi(packetsWaiting);
                    _forceUiUpdate = false;
                }

                CheckUpdateUi(packetsWaiting);

                Message obj;
                var successDequeue = TeraSniffer.Instance.Packets.TryDequeue(out obj);
                if (!successDequeue)
                {
                    Thread.Sleep(1);
                    continue;
                }

                var message = MessageFactory.Create(obj);
                if (message.GetType() == typeof(UnknownMessage)) continue;

                if (!PacketProcessing.Process(message))
                {
                    //Unprocessed packet
                }


            }
        }
     
        public void CheckUpdateUi(int packetsWaiting)
        {
            var second = DateTime.UtcNow.Ticks;
            if (second - _lastTick < TimeSpan.TicksPerSecond) return;
            UpdateUi(packetsWaiting);
        }

        internal virtual void OnGuildIconAction(Bitmap icon)
        {
            GuildIconAction?.Invoke(icon);
        }
    }
}