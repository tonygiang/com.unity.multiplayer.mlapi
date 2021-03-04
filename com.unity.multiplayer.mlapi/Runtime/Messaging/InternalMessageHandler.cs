using System;
using System.IO;
using MLAPI.Connection;
using MLAPI.Logging;
using MLAPI.SceneManagement;
using MLAPI.Serialization.Pooled;
using MLAPI.Spawning;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using MLAPI.Messaging.Buffering;
using MLAPI.Profiling;
using MLAPI.Serialization;
using Unity.Profiling;

namespace MLAPI.Messaging
{
    internal static class InternalMessageHandler
    {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        static ProfilerMarker s_HandleConnectionRequest = new ProfilerMarker("InternalMessageHandler.HandleConnectionRequest");
        static ProfilerMarker s_HandleConnectionApproved = new ProfilerMarker("InternalMessageHandler.HandleConnectionApproved");
        static ProfilerMarker s_HandleAddObject = new ProfilerMarker("InternalMessageHandler.HandleAddObject");
        static ProfilerMarker s_HandleDestroyObject = new ProfilerMarker("InternalMessageHandler.HandleDestroyObject");
        static ProfilerMarker s_HandleSwitchScene = new ProfilerMarker("InternalMessageHandler.HandleSwitchScene");
        static ProfilerMarker s_HandleClientSwitchSceneCompleted = new ProfilerMarker("InternalMessageHandler.HandleClientSwitchSceneCompleted");
        static ProfilerMarker s_HandleChangeOwner = new ProfilerMarker("InternalMessageHandler.HandleChangeOwner");
        static ProfilerMarker s_HandleAddObjects = new ProfilerMarker("InternalMessageHandler.HandleAddObjects");
        static ProfilerMarker s_HandleDestroyObjects = new ProfilerMarker("InternalMessageHandler.HandleDestroyObjects");
        static ProfilerMarker s_HandleTimeSync = new ProfilerMarker("InternalMessageHandler.HandleTimeSync");
        static ProfilerMarker s_HandleNetworkVariableDelta = new ProfilerMarker("InternalMessageHandler.HandleNetworkVariableDelta");
        static ProfilerMarker s_HandleNetworkVariableUpdate = new ProfilerMarker("InternalMessageHandler.HandleNetworkVariableUpdate");
        static ProfilerMarker s_HandleUnnamedMessage = new ProfilerMarker("InternalMessageHandler.HandleUnnamedMessage");
        static ProfilerMarker s_HandleNamedMessage = new ProfilerMarker("InternalMessageHandler.HandleNamedMessage");
        static ProfilerMarker s_HandleNetworkLog = new ProfilerMarker("InternalMessageHandler.HandleNetworkLog");

#endif

        internal static void HandleConnectionRequest(ulong clientId, Stream stream)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleConnectionRequest.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                ulong configHash = reader.ReadUInt64Packed();
                if (!NetworkManager.Singleton.NetworkConfig.CompareConfig(configHash))
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("NetworkConfiguration mismatch. The configuration between the server and client does not match");
                    NetworkManager.Singleton.DisconnectClient(clientId);
                    return;
                }

                if (NetworkManager.Singleton.NetworkConfig.ConnectionApproval)
                {
                    byte[] connectionBuffer = reader.ReadByteArray();
                    NetworkManager.Singleton.InvokeConnectionApproval(connectionBuffer, clientId, (createPlayerObject, playerPrefabHash, approved, position, rotation) =>
                    {
                        NetworkManager.Singleton.HandleApproval(clientId, createPlayerObject, playerPrefabHash, approved, position, rotation);
                    });
                }
                else
                {
                    NetworkManager.Singleton.HandleApproval(clientId, NetworkManager.Singleton.NetworkConfig.CreatePlayerPrefab, null, true, null, null);
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleConnectionRequest.End();
#endif
        }

        internal static void HandleConnectionApproved(ulong clientId, Stream stream, float receiveTime)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleConnectionApproved.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                NetworkManager.Singleton.LocalClientId = reader.ReadUInt64Packed();

                uint sceneIndex = 0;
                Guid sceneSwitchProgressGuid = new Guid();

                if (NetworkManager.Singleton.NetworkConfig.EnableSceneManagement)
                {
                    sceneIndex = reader.ReadUInt32Packed();
                    sceneSwitchProgressGuid = new Guid(reader.ReadByteArray());
                }

                bool sceneSwitch = NetworkManager.Singleton.NetworkConfig.EnableSceneManagement && NetworkSceneManager.HasSceneMismatch(sceneIndex);

                float netTime = reader.ReadSinglePacked();
                NetworkManager.Singleton.UpdateNetworkTime(clientId, netTime, receiveTime, true);

                NetworkManager.Singleton.ConnectedClients.Add(NetworkManager.Singleton.LocalClientId, new NetworkClient() { ClientId = NetworkManager.Singleton.LocalClientId });


                void DelayedSpawnAction(Stream continuationStream)
                {
                    using (PooledNetworkReader continuationReader = PooledNetworkReader.Get(continuationStream))
                    {
                        if (!NetworkManager.Singleton.NetworkConfig.EnableSceneManagement || NetworkManager.Singleton.NetworkConfig.UsePrefabSync)
                        {
                            NetworkSpawnManager.DestroySceneObjects();
                        }
                        else
                        {
                            NetworkSpawnManager.ClientCollectSoftSyncSceneObjectSweep(null);
                        }

                        uint objectCount = continuationReader.ReadUInt32Packed();
                        for (int i = 0; i < objectCount; i++)
                        {
                            bool isPlayerObject = continuationReader.ReadBool();
                            ulong networkId = continuationReader.ReadUInt64Packed();
                            ulong ownerId = continuationReader.ReadUInt64Packed();
                            bool hasParent = continuationReader.ReadBool();
                            ulong? parentNetworkId = null;

                            if (hasParent)
                            {
                                parentNetworkId = continuationReader.ReadUInt64Packed();
                            }

                            ulong prefabHash;
                            ulong instanceId;
                            bool softSync;

                            if (!NetworkManager.Singleton.NetworkConfig.EnableSceneManagement || NetworkManager.Singleton.NetworkConfig.UsePrefabSync)
                            {
                                softSync = false;
                                instanceId = 0;
                                prefabHash = continuationReader.ReadUInt64Packed();
                            }
                            else
                            {
                                softSync = continuationReader.ReadBool();

                                if (softSync)
                                {
                                    instanceId = continuationReader.ReadUInt64Packed();
                                    prefabHash = 0;
                                }
                                else
                                {
                                    prefabHash = continuationReader.ReadUInt64Packed();
                                    instanceId = 0;
                                }
                            }

                            Vector3? pos = null;
                            Quaternion? rot = null;
                            if (continuationReader.ReadBool())
                            {
                                pos = new Vector3(continuationReader.ReadSinglePacked(), continuationReader.ReadSinglePacked(), continuationReader.ReadSinglePacked());
                                rot = Quaternion.Euler(continuationReader.ReadSinglePacked(), continuationReader.ReadSinglePacked(), continuationReader.ReadSinglePacked());
                            }

                            NetworkObject netObject = NetworkSpawnManager.CreateLocalNetworkObject(softSync, instanceId, prefabHash, parentNetworkId, pos, rot);
                            NetworkSpawnManager.SpawnNetworkObjectLocally(netObject, networkId, softSync, isPlayerObject, ownerId, continuationStream, false, 0, true, false);

                            Queue<BufferManager.BufferedMessage> bufferQueue = BufferManager.ConsumeBuffersForNetworkId(networkId);

                            // Apply buffered messages
                            if (bufferQueue != null)
                            {
                                while (bufferQueue.Count > 0)
                                {
                                    BufferManager.BufferedMessage message = bufferQueue.Dequeue();

                                    NetworkManager.Singleton.HandleIncomingData(message.sender, message.networkChannel, new ArraySegment<byte>(message.payload.GetBuffer(), (int)message.payload.Position, (int)message.payload.Length), message.receiveTime, false);

                                    BufferManager.RecycleConsumedBufferedMessage(message);
                                }
                            }
                        }

                        NetworkSpawnManager.CleanDiffedSceneObjects();

                        NetworkManager.Singleton.IsConnectedClient = true;

                        NetworkManager.Singleton.InvokeOnClientConnectedCallback(NetworkManager.Singleton.LocalClientId);
                    }
                }

                if (sceneSwitch)
                {
                    UnityAction<Scene, Scene> onSceneLoaded = null;

                    var continuationBuffer = new NetworkBuffer();
                    continuationBuffer.CopyUnreadFrom(stream);
                    continuationBuffer.Position = 0;

                    void OnSceneLoadComplete()
                    {
                        SceneManager.activeSceneChanged -= onSceneLoaded;
                        NetworkSceneManager.isSpawnedObjectsPendingInDontDestroyOnLoad = false;
                        DelayedSpawnAction(continuationBuffer);
                    }

                    onSceneLoaded = (oldScene, newScene) => { OnSceneLoadComplete(); };

                    SceneManager.activeSceneChanged += onSceneLoaded;

                    NetworkSceneManager.OnFirstSceneSwitchSync(sceneIndex, sceneSwitchProgressGuid);
                }
                else
                {
                    DelayedSpawnAction(stream);
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleConnectionApproved.End();
#endif
        }

        internal static void HandleAddObject(ulong clientId, Stream stream)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleAddObject.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                bool isPlayerObject = reader.ReadBool();
                ulong networkId = reader.ReadUInt64Packed();
                ulong ownerId = reader.ReadUInt64Packed();
                bool hasParent = reader.ReadBool();
                ulong? parentNetworkId = null;

                if (hasParent)
                {
                    parentNetworkId = reader.ReadUInt64Packed();
                }

                ulong prefabHash;
                ulong instanceId;
                bool softSync;

                if (!NetworkManager.Singleton.NetworkConfig.EnableSceneManagement || NetworkManager.Singleton.NetworkConfig.UsePrefabSync)
                {
                    softSync = false;
                    instanceId = 0;
                    prefabHash = reader.ReadUInt64Packed();
                }
                else
                {
                    softSync = reader.ReadBool();

                    if (softSync)
                    {
                        instanceId = reader.ReadUInt64Packed();
                        prefabHash = 0;
                    }
                    else
                    {
                        prefabHash = reader.ReadUInt64Packed();
                        instanceId = 0;
                    }
                }

                Vector3? pos = null;
                Quaternion? rot = null;
                if (reader.ReadBool())
                {
                    pos = new Vector3(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());
                    rot = Quaternion.Euler(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());
                }

                bool hasPayload = reader.ReadBool();
                int payLoadLength = hasPayload ? reader.ReadInt32Packed() : 0;

                NetworkObject netObject = NetworkSpawnManager.CreateLocalNetworkObject(softSync, instanceId, prefabHash, parentNetworkId, pos, rot);
                NetworkSpawnManager.SpawnNetworkObjectLocally(netObject, networkId, softSync, isPlayerObject, ownerId, stream, hasPayload, payLoadLength, true, false);

                Queue<BufferManager.BufferedMessage> bufferQueue = BufferManager.ConsumeBuffersForNetworkId(networkId);

                // Apply buffered messages
                if (bufferQueue != null)
                {
                    while (bufferQueue.Count > 0)
                    {
                        BufferManager.BufferedMessage message = bufferQueue.Dequeue();

                        NetworkManager.Singleton.HandleIncomingData(message.sender, message.networkChannel, new ArraySegment<byte>(message.payload.GetBuffer(), (int)message.payload.Position, (int)message.payload.Length), message.receiveTime, false);

                        BufferManager.RecycleConsumedBufferedMessage(message);
                    }
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleAddObject.End();
#endif
        }

        internal static void HandleDestroyObject(ulong clientId, Stream stream)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleDestroyObject.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                NetworkSpawnManager.OnDestroyObject(networkId, true);
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleDestroyObject.End();
#endif
        }

        internal static void HandleSwitchScene(ulong clientId, Stream stream)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleSwitchScene.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                uint sceneIndex = reader.ReadUInt32Packed();
                Guid switchSceneGuid = new Guid(reader.ReadByteArray());

                var objectBuffer = new NetworkBuffer();
                objectBuffer.CopyUnreadFrom(stream);
                objectBuffer.Position = 0;

                NetworkSceneManager.OnSceneSwitch(sceneIndex, switchSceneGuid, objectBuffer);
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleSwitchScene.End();
#endif
        }

        internal static void HandleClientSwitchSceneCompleted(ulong clientId, Stream stream)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleClientSwitchSceneCompleted.Begin();
#endif
            if(NetworkManager.Singleton.ConnectedClients.ContainsKey(clientId))
            {
                NetworkManager.Singleton.ConnectedClients[clientId].IsClientDoneLoadingScene = true;
            }
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                NetworkSceneManager.OnClientSwitchSceneCompleted(clientId, new Guid(reader.ReadByteArray()));
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleClientSwitchSceneCompleted.End();
#endif
        }

        internal static void HandleChangeOwner(ulong clientId, Stream stream)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleChangeOwner.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                ulong ownerClientId = reader.ReadUInt64Packed();

                if (NetworkSpawnManager.SpawnedObjects[networkId].OwnerClientId == NetworkManager.Singleton.LocalClientId)
                {
                    //We are current owner.
                    NetworkSpawnManager.SpawnedObjects[networkId].InvokeBehaviourOnLostOwnership();
                }

                if (ownerClientId == NetworkManager.Singleton.LocalClientId)
                {
                    //We are new owner.
                    NetworkSpawnManager.SpawnedObjects[networkId].InvokeBehaviourOnGainedOwnership();
                }

                NetworkSpawnManager.SpawnedObjects[networkId].OwnerClientId = ownerClientId;
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleChangeOwner.End();
#endif
        }

        internal static void HandleAddObjects(ulong clientId, Stream stream)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleAddObjects.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                ushort objectCount = reader.ReadUInt16Packed();

                for (int i = 0; i < objectCount; i++)
                {
                    HandleAddObject(clientId, stream);
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleAddObjects.End();
#endif
        }

        internal static void HandleDestroyObjects(ulong clientId, Stream stream)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleDestroyObjects.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                ushort objectCount = reader.ReadUInt16Packed();

                for (int i = 0; i < objectCount; i++)
                {
                    HandleDestroyObject(clientId, stream);
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleDestroyObjects.End();
#endif
        }

        internal static void HandleTimeSync(ulong clientId, Stream stream, float receiveTime)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleTimeSync.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                float netTime = reader.ReadSinglePacked();
                NetworkManager.Singleton.UpdateNetworkTime(clientId, netTime, receiveTime);
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleTimeSync.End();
#endif
        }

        internal static void HandleNetworkVariableDelta(ulong clientId, Stream stream, Action<ulong, PreBufferPreset> bufferCallback, PreBufferPreset bufferPreset)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleNetworkVariableDelta.Begin();
#endif
            if (!NetworkManager.Singleton.NetworkConfig.EnableNetworkVariable)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("NetworkVariable delta received but EnableNetworkVariable is false");
                return;
            }

            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                ushort orderIndex = reader.ReadUInt16Packed();

                if (NetworkSpawnManager.SpawnedObjects.ContainsKey(networkId))
                {
                    NetworkBehaviour instance = NetworkSpawnManager.SpawnedObjects[networkId].GetNetworkBehaviourAtOrderIndex(orderIndex);

                    if (instance == null)
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("NetworkVariableDelta message received for a non-existent behaviour. NetworkId: " + networkId + ", behaviourIndex: " + orderIndex);
                    }
                    else
                    {
                        NetworkBehaviour.HandleNetworkVariableDeltas(instance.networkVariableFields, stream, clientId, instance);
                    }
                }
                else if (NetworkManager.Singleton.IsServer || !NetworkManager.Singleton.NetworkConfig.EnableMessageBuffering)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("NetworkVariableDelta message received for a non-existent object with id: " + networkId + ". This delta was lost.");
                }
                else
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("NetworkVariableDelta message received for a non-existent object with id: " + networkId + ". This delta will be buffered and might be recovered.");
                    bufferCallback(networkId, bufferPreset);
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleNetworkVariableDelta.End();
#endif
        }

        internal static void HandleNetworkVariableUpdate(ulong clientId, Stream stream, Action<ulong, PreBufferPreset> bufferCallback, PreBufferPreset bufferPreset)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleNetworkVariableUpdate.Begin();
#endif
            if (!NetworkManager.Singleton.NetworkConfig.EnableNetworkVariable)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("NetworkVariable update received but EnableNetworkVariable is false");
                return;
            }

            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                ushort orderIndex = reader.ReadUInt16Packed();

                if (NetworkSpawnManager.SpawnedObjects.ContainsKey(networkId))
                {
                    NetworkBehaviour instance = NetworkSpawnManager.SpawnedObjects[networkId].GetNetworkBehaviourAtOrderIndex(orderIndex);

                    if (instance == null)
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("NetworkVariableUpdate message received for a non-existent behaviour. NetworkId: " + networkId + ", behaviourIndex: " + orderIndex);
                    }
                    else
                    {
                        NetworkBehaviour.HandleNetworkVariableUpdate(instance.networkVariableFields, stream, clientId, instance);
                    }
                }
                else if (NetworkManager.Singleton.IsServer || !NetworkManager.Singleton.NetworkConfig.EnableMessageBuffering)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("NetworkVariableUpdate message received for a non-existent object with id: " + networkId + ". This delta was lost.");
                }
                else
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("NetworkVariableUpdate message received for a non-existent object with id: " + networkId + ". This delta will be buffered and might be recovered.");
                    bufferCallback(networkId, bufferPreset);
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleNetworkVariableUpdate.End();
#endif
        }

        /// <summary>
        /// Converts the stream to a PerformanceQueueItem and adds it to the receive queue
        /// </summary>
        /// <param name="clientId"></param>
        /// <param name="stream"></param>
        /// <param name="receiveTime"></param>
        internal static void RpcReceiveQueueItem(ulong clientId, Stream stream, float receiveTime, RpcQueueContainer.QueueItemType queueItemType)
        {
            if (NetworkManager.Singleton.IsServer && clientId == NetworkManager.Singleton.ServerClientId)
            {
                return;
            }

            ProfilerStatManager.rpcsRcvd.Record();
            PerformanceDataManager.Increment(ProfilerConstants.NumberOfRPCsReceived);

            var rpcQueueContainer = NetworkManager.Singleton.rpcQueueContainer;
            rpcQueueContainer.AddQueueItemToInboundFrame(queueItemType, receiveTime, clientId, (NetworkBuffer)stream);
        }

        internal static void HandleUnnamedMessage(ulong clientId, Stream stream)
        {
            PerformanceDataManager.Increment(ProfilerConstants.NumberOfUnnamedMessages);
            ProfilerStatManager.unnamedMessage.Record();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleUnnamedMessage.Begin();
#endif
            CustomMessagingManager.InvokeUnnamedMessage(clientId, stream);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleUnnamedMessage.End();
#endif
        }

        internal static void HandleNamedMessage(ulong clientId, Stream stream)
        {
            PerformanceDataManager.Increment(ProfilerConstants.NumberOfNamedMessages);
            ProfilerStatManager.namedMessage.Record();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleNamedMessage.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                ulong hash = reader.ReadUInt64Packed();

                CustomMessagingManager.InvokeNamedMessage(hash, clientId, stream);
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleNamedMessage.End();
#endif
        }

        internal static void HandleNetworkLog(ulong clientId, Stream stream)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleNetworkLog.Begin();
#endif
            using (PooledNetworkReader reader = PooledNetworkReader.Get(stream))
            {
                NetworkLog.LogType logType = (NetworkLog.LogType)reader.ReadByte();
                string message = reader.ReadStringPacked().ToString();

                switch (logType)
                {
                    case NetworkLog.LogType.Info:
                        NetworkLog.LogInfoServerLocal(message, clientId);
                        break;
                    case NetworkLog.LogType.Warning:
                        NetworkLog.LogWarningServerLocal(message, clientId);
                        break;
                    case NetworkLog.LogType.Error:
                        NetworkLog.LogErrorServerLocal(message, clientId);
                        break;
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleNetworkLog.End();
#endif
        }
    }
}
