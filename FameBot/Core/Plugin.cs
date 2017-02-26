﻿using Lib_K_Relay.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib_K_Relay;
using System.Diagnostics;
using FameBot.Data.Enums;
using FameBot.Data.Models;
using Lib_K_Relay.Networking;
using System.Runtime.InteropServices;
using FameBot.Helpers;
using Lib_K_Relay.Networking.Packets;
using Lib_K_Relay.Networking.Packets.Server;
using Lib_K_Relay.Networking.Packets.DataObjects;
using Lib_K_Relay.Utilities;
using Lib_K_Relay.Networking.Packets.Client;
using FameBot.Services;
using FameBot.UserInterface;
using FameBot.Data.Events;

namespace FameBot.Core
{
    public class Plugin : IPlugin
    {
        #region IPlugin
        public string GetAuthor()
        {
            return "Chicken";
        }

        public string[] GetCommands()
        {
            return new string[]
            {
                "/activate - binds the bot to the client where the command is used.",
                "/start - starts the bot",
                "/gui - opens the gui"
            };
        }

        public string GetDescription()
        {
            return "A bot designed to automate the process of collecting fame.";
        }

        public string GetName()
        {
            return "FameBot by Chicken";
        }
        #endregion

        private IntPtr flashPtr;
        private bool followTarget;
        private List<Target> targets;
        private List<Portal> portals;
        private Dictionary<int, Target> playerPosisions;
        private Client connectedClient;
        private int tickCount;
        private Configuration config;
        private FameBotGUI gui;
        private bool autoConnect = true;
        private bool gotoRealm;
        private bool enabled;

        public static event HealthEventHandler healthChanged;
        public delegate void HealthEventHandler(object sender, HealthChangedEventArgs args);

        public static event KeyEventHandler keyChanged;
        public delegate void KeyEventHandler(object sender, KeyEventArgs args);

        private static event GuiEventHandler guiEvent;
        private delegate void GuiEventHandler(GuiEvent evt);

        public static event LogEventHandler logEvent;
        public delegate void LogEventHandler(object sender, LogEventArgs args);

        #region WINAPI
        // Get the focused window
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();
        // Send a message to a specific process via the handle
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, UInt32 Msg, int wParam, int lParam);
        #endregion

        #region Keys
        private bool wPressed;
        private bool aPressed;
        private bool sPressed;
        private bool dPressed;

        private bool W_PRESSED
        {
            get { return wPressed; }
            set
            {
                wPressed = value;
                keyChanged?.Invoke(this, new KeyEventArgs(Key.W, value));
            }
        }
        private bool A_PRESSED
        {
            get { return aPressed; }
            set
            {
                aPressed = value;
                keyChanged?.Invoke(this, new KeyEventArgs(Key.A, value));
            }
        }
        private bool S_PRESSED
        {
            get { return sPressed; }
            set
            {
                sPressed = value;
                keyChanged?.Invoke(this, new KeyEventArgs(Key.S, value));
            }
        }
        private bool D_PRESSED
        {
            get { return dPressed; }
            set
            {
                dPressed = value;
                keyChanged?.Invoke(this, new KeyEventArgs(Key.D, value));
            }
        }
        #endregion

        public void Initialize(Proxy proxy)
        {
            targets = new List<Target>();
            playerPosisions = new Dictionary<int, Target>();
            portals = new List<Portal>();

            gui = new FameBotGUI();
            PluginUtils.ShowGUI(gui);

            config = ConfigManager.GetConfiguration();

            Process[] processes = Process.GetProcessesByName("flash");
            if (processes.Length == 1)
            {
                Console.WriteLine("[FameBot] Flash process handle aquired automatically.");
                Log("Automatically bound to client");
                flashPtr = processes[0].MainWindowHandle;
            } else if(processes.Length > 1)
            {
                Log("Multiple clients running. use the /activate command on the client you want to use");
                Console.WriteLine("[FameBot] Multiple instances of flash are open. Please use the /activate command on the instance you want to use the bot with.");
            } else
            {
                Console.WriteLine("[FameBot] Couldn't find any instances of flash player. Use the /activate command when you have opened flash.");
                Console.WriteLine("[FameBot] FameBot will only detect instances of flash player which are called \"flash.exe\"");
            }

            proxy.HookCommand("activate", ReceiveCommand);
            proxy.HookCommand("start", ReceiveCommand);
            proxy.HookCommand("gui", ReceiveCommand);

            proxy.HookPacket(PacketType.UPDATE, OnUpdate);
            proxy.HookPacket(PacketType.NEWTICK, OnNewTick);
            proxy.HookPacket(PacketType.PLAYERHIT, OnHit);

            proxy.ClientConnected += (client) =>
            {
                connectedClient = client;
                targets.Clear();
                followTarget = false;
                A_PRESSED = false;
                D_PRESSED = false;
                W_PRESSED = false;
                S_PRESSED = false;
            };

            proxy.HookPacket(PacketType.MAPINFO, OnMapInfo);

            guiEvent += (evt) =>
            {
                switch (evt)
                {
                    case GuiEvent.StartBot:
                        Start();
                        break;
                    case GuiEvent.StopBot:
                        Stop();
                        break;
                    case GuiEvent.SettingsChanged:
                        config = ConfigManager.GetConfiguration();
                        break;
                }
            };
        }

        private void ReceiveCommand(Client client, string cmd, string[] args)
        {
            switch(cmd)
            {
                case "activate":
                    flashPtr = GetForegroundWindow();
                    client.Notify("FameBot is now active");
                    break;
                case "start":
                    Start();
                    client.Notify("FameBot is starting");
                    break;
                case "gui":
                    if (gui == null)
                        gui = new FameBotGUI();
                    gui.Show();
                    break;
            }
        }

        public static void InvokeGuiEvent(GuiEvent evt)
        {
            guiEvent?.Invoke(evt);
        }

        private void Stop()
        {
            Log("Stopping bot");
            followTarget = false;
            targets.Clear();
            enabled = false;
        }

        private void Start()
        {
            Log("Starting bot");
            targets.Clear();
            if (!autoConnect)
                followTarget = true;
            enabled = true;
        }

        private void Escape(Client client)
        {
            Console.WriteLine("[FameBot] Escaping to nexus.");
            Log("Escaping to nexus");
            client.SendToServer(Packet.Create(PacketType.ESCAPE));
        }

        private void Log(string message)
        {
            logEvent?.Invoke(this, new LogEventArgs(message));
        }

        #region PacketHookMethods
        private void OnUpdate(Client client, Packet p)
        {
            UpdatePacket packet = p as UpdatePacket;

            // Get new info
            foreach(Entity obj in packet.NewObjs)
            {
                if(Enum.IsDefined(typeof(Classes), obj.ObjectType))
                {
                    PlayerData playerData = new PlayerData(obj.Status.ObjectId);
                    playerData.Class = (Classes)obj.ObjectType;
                    playerData.Pos = obj.Status.Position;
                    foreach(var data in obj.Status.Data)
                    {
                        playerData.Parse(data.Id, data.IntValue, data.StringValue);
                    }

                    if (playerPosisions.ContainsKey(obj.Status.ObjectId))
                        playerPosisions.Remove(obj.Status.ObjectId);
                    playerPosisions.Add(obj.Status.ObjectId, new Target(obj.Status.ObjectId, playerData.Name, playerData.Pos));
                }
                if(obj.ObjectType == 1810)
                {
                    foreach(var data in obj.Status.Data)
                    {
                        if(data.StringValue != null)
                        {
                            // TODO: replace with regex
                            var strArray = data.StringValue.Split(' ');
                            var strCount = strArray[1].Split('/')[0].Remove(0, 1);
                            var name = strArray[0].Split('.')[1];
                            var portal = new Portal(obj.Status.ObjectId, int.Parse(strCount), name);
                            if (portals.Exists(ptl => ptl.ObjectId == obj.Status.ObjectId))
                                portals.Remove(portals.Find(ptl => ptl.ObjectId == obj.Status.ObjectId));
                            portals.Add(portal);
                        }
                    }
                }
            }

            // Remove old info
            foreach (int dropId in packet.Drops)
            {
                if (playerPosisions.ContainsKey(dropId))
                {
                    if(followTarget && targets.Exists(t => t.ObjectId == dropId))
                    {
                        targets.Remove(targets.Find(t => t.ObjectId == dropId));
                        if(targets.Count > 0)
                        {
                            Log(string.Format("Dropping {0} from targets", playerPosisions[dropId].Name));
                            Console.WriteLine("[FameBot] The player \"{0}\" was dropped from the target list.", playerPosisions[dropId].Name);
                        } else
                        {
                            Log("No targets in target list");
                            Console.WriteLine("[FameBot] There are no players left in the target list.");
                            if (config.EscapeIfNoTargets)
                                Escape(client);
                        }
                    }
                    playerPosisions.Remove(dropId);
                }
            }
        }

        private void OnMapInfo(Client client, Packet p)
        {
            MapInfoPacket packet = p as MapInfoPacket;
            if (packet == null)
                return;
            Task.Factory.StartNew(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                portals.Clear();
                if (packet.Name == "Nexus" && autoConnect)
                    gotoRealm = true;
                else
                {
                    gotoRealm = false;
                    if (enabled)
                        followTarget = true;
                }
            });
        }

        private void OnHit(Client client, Packet p)
        {
            // Autonexus
            float healthPercentage = (float)client.PlayerData.Health / (float)client.PlayerData.MaxHealth;
            if (healthPercentage * 100f < config.AutonexusThreshold * 1.25f)
                Log(string.Format("Health at {0}%", (int)(healthPercentage * 100f)));
            if (healthPercentage * 100f < config.AutonexusThreshold)
                Escape(client);
        }

        private void OnNewTick(Client client, Packet p)
        {
            NewTickPacket packet = p as NewTickPacket;
            tickCount++;

            // Health changed event
            float healthPercentage = (float)client.PlayerData.Health / (float)client.PlayerData.MaxHealth;
            healthChanged?.Invoke(this, new HealthChangedEventArgs(healthPercentage * 100f));

            if(autoConnect && gotoRealm)
            {
                MoveToRealms(client, healthPercentage);
            }

            if(tickCount % config.TickCountThreshold == 0)
            {
                if (followTarget && playerPosisions.Count > 0)
                {
                    List<Target> newTargets = D36n4.Invoke(playerPosisions.Values.ToList(), config.Epsilon, config.MinPoints, config.FindClustersNearCenter);
                    if(newTargets == null)
                    {
                        if (targets.Count != 0 && config.EscapeIfNoTargets)
                            Escape(client); 
                        targets.Clear();
                        Log("No valid clusters found");
                        Console.WriteLine("[FameBot] Player search didn't return any good results, If this keeps happening try adjusting your clustering settings.");
                    } else
                    {
                        targets = newTargets;
                        Console.WriteLine("[FameBot] Now targeting {0} players", targets.Count);
                    }
                }
                tickCount = 0;
            }

            // Update player positions
            foreach(Status status in packet.Statuses)
            {
                if (playerPosisions.ContainsKey(status.ObjectId))
                    playerPosisions[status.ObjectId].UpdatePosition(status.Position);
            }
            
            if(!followTarget && !gotoRealm)
            {
                if (W_PRESSED)
                {
                    W_PRESSED = false;
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.W, 0);
                }
                if (A_PRESSED)
                {
                    A_PRESSED = false;
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.A, 0);
                }
                if (S_PRESSED)
                {
                    S_PRESSED = false;
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.S, 0);
                }
                if (D_PRESSED)
                {
                    D_PRESSED = false;
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.D, 0);
                }
            }

            if(followTarget && targets.Count > 0)
            {
                var targetPosition = new Location(targets.Average(t => t.Position.X), targets.Average(t => t.Position.Y));

                if (client.PlayerData.Pos.DistanceTo(targetPosition) > config.TeleportDistanceThreshold)
                {
                    var tpPacket = (PlayerTextPacket)Packet.Create(PacketType.PLAYERTEXT);
                    tpPacket.Text = "/teleport " + targets.OrderBy(t => t.Position.DistanceTo(targetPosition)).First().Name;
                    client.SendToServer(tpPacket);
                }

                CalculateMovement(client, targetPosition);
            }
        }
        #endregion

        private void MoveToRealms(Client client, float healthPercentage)
        {
            Location target = new Location(134, 109);
            if (healthPercentage < 0.95f)
                target = new Location(134, 134);

            CalculateMovement(client, target);

            if(client.PlayerData.Pos.Y <= 109 && client.PlayerData.Pos.Y != 0)
            {
                gotoRealm = false;
                Task.Factory.StartNew(() =>
                {
                    AttemptConnection(client, portals.OrderByDescending(p => p.PlayerCount).First());
                });
            }
        }

        private async void AttemptConnection(Client client, Portal portal)
        {
            UsePortalPacket packet = (UsePortalPacket)Packet.Create(PacketType.USEPORTAL);
            packet.ObjectId = portal.ObjectId;
            client.SendToServer(packet);
            if(client.Connected)
            {
                await Task.Delay(TimeSpan.FromSeconds(0.2));
                AttemptConnection(client, portal);
            }
        }

        private void CalculateMovement(Client client, Location targetPosition)
        {
            // Left or right
            if (client.PlayerData.Pos.X < targetPosition.X - config.FollowDistanceThreshold)
            {
                // Move right
                if (!D_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyDown, (int)Key.D, 0);
                    D_PRESSED = true;
                }
                if (A_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.A, 0);
                    A_PRESSED = false;
                }
            }
            else if (client.PlayerData.Pos.X <= targetPosition.X + config.FollowDistanceThreshold)
            {
                if (D_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.D, 0);
                    D_PRESSED = false;
                }
            }
            if (client.PlayerData.Pos.X > targetPosition.X + config.FollowDistanceThreshold)
            {
                // Move left
                if (!A_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyDown, (int)Key.A, 0);
                    A_PRESSED = true;
                }
                if (D_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.D, 0);
                    D_PRESSED = false;
                }
            }
            else if (client.PlayerData.Pos.X >= targetPosition.X - config.FollowDistanceThreshold)
            {
                if (A_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.A, 0);
                    A_PRESSED = false;
                }
            }

            // Up or down
            if (client.PlayerData.Pos.Y < targetPosition.Y - config.FollowDistanceThreshold)
            {
                // Move down
                if (!S_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyDown, (int)Key.S, 0);
                    S_PRESSED = true;
                }
                if (W_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.W, 0);
                    W_PRESSED = false;
                }
            }
            else if (client.PlayerData.Pos.Y <= targetPosition.Y + config.FollowDistanceThreshold)
            {
                if (S_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.S, 0);
                    S_PRESSED = false;
                }
            }
            if (client.PlayerData.Pos.Y > targetPosition.Y + config.FollowDistanceThreshold)
            {
                // Move up
                if (!W_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyDown, (int)Key.W, 0);
                    W_PRESSED = true;
                }
                if (S_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.S, 0);
                    S_PRESSED = false;
                }
            }
            else if (client.PlayerData.Pos.Y >= targetPosition.Y - config.FollowDistanceThreshold)
            {
                if (W_PRESSED)
                {
                    PostMessage(flashPtr, (uint)Key.KeyUp, (int)Key.W, 0);
                    W_PRESSED = false;
                }
            }
        }
    }
}
