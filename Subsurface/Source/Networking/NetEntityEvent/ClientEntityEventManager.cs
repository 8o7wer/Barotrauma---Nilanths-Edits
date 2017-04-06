﻿using Lidgren.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Barotrauma.Networking
{
    class ClientEntityEventManager : NetEntityEventManager
    {
        private List<ClientEntityEvent> events;

        private UInt16 ID;

        private GameClient thisClient;

        //when was a specific entity event last sent to the client
        //  key = event id, value = NetTime.Now when sending
        public Dictionary<UInt16, float> eventLastSent;

        public UInt16 LastReceivedID
        {
            get { return lastReceivedID; }
        }

        private UInt16 lastReceivedID;

        public ClientEntityEventManager(GameClient client) 
        {
            events = new List<ClientEntityEvent>();
            eventLastSent = new Dictionary<UInt16, float>();

            thisClient = client;
        }

        public void CreateEvent(IClientSerializable entity, object[] extraData = null)
        {
            if (GameMain.Client == null || GameMain.Client.Character == null) return;

            if (!(entity is Entity))
            {
                DebugConsole.ThrowError("Can't create an entity event for " + entity + "!");
                return;
            }

            ID++;
            var newEvent = new ClientEntityEvent(entity, ID);
            newEvent.CharacterStateID = GameMain.Client.Character.LastNetworkUpdateID;
            if (extraData != null) newEvent.SetData(extraData);

            events.Add(newEvent);
        }

        public void Write(NetOutgoingMessage msg, NetConnection serverConnection)
        {
            if (events.Count == 0 || serverConnection == null) return;

            List<NetEntityEvent> eventsToSync = new List<NetEntityEvent>();

            //find the index of the first event the server hasn't received
            int startIndex = events.Count;
            while (startIndex > 0 &&
                NetIdUtils.IdMoreRecent(events[startIndex-1].ID,thisClient.LastSentEntityEventID))
            {
                startIndex--;
            }

            for (int i = startIndex; i < events.Count; i++)
            {
                //find the first event that hasn't been sent in 1.5 * roundtriptime or at all
                float lastSent = 0;
                eventLastSent.TryGetValue(events[i].ID, out lastSent);

                if (lastSent > NetTime.Now - serverConnection.AverageRoundtripTime * 1.5f)
                {
                    continue;
                }

                eventsToSync.AddRange(events.GetRange(i, events.Count - i));
                break;
            }
            if (eventsToSync.Count == 0) return;

            //too many events for one packet
            if (eventsToSync.Count > MaxEventsPerWrite)
            {
                eventsToSync.RemoveRange(MaxEventsPerWrite, eventsToSync.Count - MaxEventsPerWrite);
            }
            if (eventsToSync.Count == 0) return;

            foreach (NetEntityEvent entityEvent in eventsToSync)
            {
                eventLastSent[entityEvent.ID] = (float)NetTime.Now;
            }

            msg.Write((byte)ClientNetObject.ENTITY_STATE);
            Write(msg, eventsToSync);
        }
        
        /// <summary>
        /// Read the events from the message, ignoring ones we've already received
        /// </summary>
        public void Read(ServerNetObject type, NetIncomingMessage msg, float sendingTime)
        {
            UInt16 unreceivedEntityEventCount = 0;
            UInt16 firstNewID = 0;

            if (type == ServerNetObject.ENTITY_EVENT_INITIAL)
            {
                unreceivedEntityEventCount = msg.ReadUInt16();
                firstNewID = msg.ReadUInt16();
            }

            UInt16 firstEventID = msg.ReadUInt16();
            int eventCount = msg.ReadByte();

            for (int i = 0; i < eventCount; i++)
            {
                UInt16 thisEventID = (UInt16)(firstEventID + (UInt16)i);
                UInt16 entityID = msg.ReadUInt16();

                if (entityID == 0)
                {
                    msg.ReadPadBits();
                    lastReceivedID++;
                    continue;
                }

                byte msgLength = msg.ReadByte();

                IServerSerializable entity = Entity.FindEntityByID(entityID) as IServerSerializable;

                //skip the event if we've already received it or if the entity isn't found
                if (thisEventID != lastReceivedID + 1 || entity == null)
                {
                    if (thisEventID != lastReceivedID + 1)
                    {
                        DebugConsole.NewMessage("received msg " + thisEventID + " (waiting for "+ (lastReceivedID+1) + ")", thisEventID<lastReceivedID+1 ? Microsoft.Xna.Framework.Color.Yellow : Microsoft.Xna.Framework.Color.Red);
                    }
                    else if (entity == null)
                    {
                        DebugConsole.NewMessage("received msg " + thisEventID + ", entity " + entityID + " not found", Microsoft.Xna.Framework.Color.Red);
                    }
                    msg.Position += msgLength * 8;
                }
                else
                {
                    long msgPosition = msg.Position;

                    DebugConsole.NewMessage("received msg " + thisEventID + " (" + entity.ToString() + ")", Microsoft.Xna.Framework.Color.Green);
                    lastReceivedID++;
                    try
                    {
                        ReadEvent(msg, entity, sendingTime);
                    }

                    catch (Exception e)
                    {
#if DEBUG
                        DebugConsole.ThrowError("Failed to read event for entity \"" + entity.ToString() + "\"!", e);
#endif
                        msg.Position = msgPosition + msgLength * 8;
                    }
                }
                msg.ReadPadBits();
            }

            if (type == ServerNetObject.ENTITY_EVENT_INITIAL)
            {
                if (lastReceivedID == unreceivedEntityEventCount - 1 ||
                    unreceivedEntityEventCount == 0)
                {
                    lastReceivedID = (UInt16)(firstNewID - 1);
                }
            }
        }

        protected override void WriteEvent(NetBuffer buffer, NetEntityEvent entityEvent, Client recipient = null)
        {
            var clientEvent = entityEvent as ClientEntityEvent;
            if (clientEvent == null) return;

            clientEvent.Write(buffer);
        }

        protected void ReadEvent(NetIncomingMessage buffer, IServerSerializable entity, float sendingTime)
        {
            entity.ClientRead(ServerNetObject.ENTITY_EVENT, buffer, sendingTime);
        }

        public void Clear()
        {
            ID = 0;

            lastReceivedID = 0;

            events.Clear();
            eventLastSent.Clear();
        }
    }
}