﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ECommon.Extensions;
using ECommon.Remoting;
using ECommon.Scheduling;
using ECommon.Serializing;
using EQueue.Protocols;
using EQueue.Protocols.Brokers;
using EQueue.Protocols.Brokers.Requests;
using EQueue.Protocols.NameServers;
using EQueue.Protocols.NameServers.Requests;
using EQueue.Utils;

namespace EQueue.AdminWeb
{
    public class MessageService
    {
        class BrokerClient
        {
            public BrokerInfo BrokerInfo;
            public SocketRemotingClient RemotingClient;
        }
        private readonly object _lockObj = new object();
        private readonly byte[] EmptyBytes = new byte[0];
        private readonly IList<SocketRemotingClient> _nameServerRemotingClientList;
        private readonly ConcurrentDictionary<string /*clusterName*/, IList<BrokerClient>> _clusterBrokerDict;
        private readonly IBinarySerializer _binarySerializer;
        private readonly IScheduleService _scheduleService;
        private readonly EQueueSettingService _eQueueSettingService;
        private readonly SendEmailService _sendEmailService;
        private long _nameServerIndex;

        public MessageService(IBinarySerializer binarySerializer, IScheduleService scheduleService, EQueueSettingService eQueueSettingService, SendEmailService sendEmailService)
        {
            _eQueueSettingService = eQueueSettingService;
            _nameServerRemotingClientList = CreateRemotingClientList(_eQueueSettingService.NameServerList);
            _clusterBrokerDict = new ConcurrentDictionary<string, IList<BrokerClient>>();
            _binarySerializer = binarySerializer;
            _scheduleService = scheduleService;
            _sendEmailService = sendEmailService;
        }

        public void Start()
        {
            StartAllNameServerClients();

            if (_eQueueSettingService.EnableMonitorMessageAccumulate)
            {
                _scheduleService.StartTask("ScanAccumulateMessages", async () => await ScanAccumulateMessages(), 1000, _eQueueSettingService.ScanMessageAccumulateIntervalSeconds * 1000);
            }
        }
        public async Task<IEnumerable<string>> GetAllClusters()
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.GetAllClusters, EmptyBytes);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<string>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetAllClusters failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<BrokerStatusInfo>> GetClusterBrokerStatusInfoList(string clusterName, string topic = null, bool onlyFindMaster = false)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new GetClusterBrokersRequest { ClusterName = clusterName, Topic = topic, OnlyFindMaster = onlyFindMaster });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.GetClusterBrokerStatusInfoList, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<BrokerStatusInfo>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetClusterBrokers failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<BrokerStatisticInfo> QueryBrokerStatisticInfo(string clusterName, string brokerName)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.GetBrokerStatisticInfo, new byte[0]);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<BrokerStatisticInfo>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("QueryBrokerStatisticInfo failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<BrokerTopicQueueInfo>> GetTopicQueueInfoList(string clusterName, string topic)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new Protocols.NameServers.Requests.GetTopicQueueInfoRequest
            {
                ClusterName = clusterName,
                Topic = topic
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.GetTopicQueueInfo, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<BrokerTopicQueueInfo>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetTopicQueueInfo failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<TopicQueueInfo>> GetTopicQueueInfoList(string clusterName, string brokerName, string topic)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var requestData = _binarySerializer.Serialize(new Protocols.Brokers.Requests.GetTopicQueueInfoRequest(topic));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.GetTopicQueueInfo, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<TopicQueueInfo>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetTopicQueueInfo failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<BrokerTopicConsumeInfo>> GetTopicConsumeInfoList(string clusterName, string group, string topic)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new Protocols.NameServers.Requests.GetTopicConsumeInfoRequest
            {
                ClusterName = clusterName,
                ConsumerGroup = group,
                Topic = topic
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.GetTopicConsumeInfo, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<BrokerTopicConsumeInfo>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetTopicConsumeInfo failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<TopicConsumeInfo>> GetTopicConsumeInfoList(string clusterName, string brokerName, string group, string topic)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var requestData = _binarySerializer.Serialize(new Protocols.Brokers.Requests.GetTopicConsumeInfoRequest(group, topic));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.GetTopicConsumeInfo, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<TopicConsumeInfo>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetTopicConsumeInfo failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<BrokerProducerListInfo>> GetProducerInfoList(string clusterName)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new GetProducerListRequest
            {
                ClusterName = clusterName
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.GetProducerList, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<BrokerProducerListInfo>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetProducerInfo failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<string>> GetProducerInfoList(string clusterName, string brokerName)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.GetProducerList, EmptyBytes);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                var producerIds = Encoding.UTF8.GetString(remotingResponse.ResponseBody);
                return producerIds.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                throw new Exception(string.Format("GetProducerInfo failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<BrokerConsumerListInfo>> GetConsumerInfoList(string clusterName, string group, string topic)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new Protocols.NameServers.Requests.GetConsumerListRequest
            {
                ClusterName = clusterName,
                ConsumerGroup = group,
                Topic = topic
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.GetConsumerList, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<BrokerConsumerListInfo>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetConsumerInfo failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<ConsumerInfo>> GetConsumerInfoList(string clusterName, string brokerName, string group, string topic)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var requestData = _binarySerializer.Serialize(new Protocols.Brokers.Requests.GetConsumerListRequest(group, topic));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.GetConsumerList, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<ConsumerInfo>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetConsumerInfo failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<IEnumerable<string>> GetLatestSendMessagesList(string clusterName, string brokerName)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.GetLastestMessages, EmptyBytes);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                var messageIds = Encoding.UTF8.GetString(remotingResponse.ResponseBody);
                return messageIds.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                throw new Exception(string.Format("GetLatestSendMessagesList failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task CreateTopic(string clusterName, string topic, int? initialQueueCount)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new CreateTopicForClusterRequest
            {
                ClusterName = clusterName,
                Topic = topic,
                InitialQueueCount = initialQueueCount
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.CreateTopic, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("CreateTopic failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task CreateTopic(string clusterName, string brokerName, string topic, int? initialQueueCount)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var requestData = _binarySerializer.Serialize(new CreateTopicRequest(topic, initialQueueCount));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.CreateTopic, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("CreateTopic failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task DeleteTopic(string clusterName, string topic)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new DeleteTopicForClusterRequest
            {
                ClusterName = clusterName,
                Topic = topic
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.DeleteTopic, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("DeleteTopic failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task DeleteTopic(string clusterName, string brokerName, string topic)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var requestData = _binarySerializer.Serialize(new DeleteTopicRequest(topic));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.DeleteTopic, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("DeleteTopic failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task AddQueue(string clusterName, string topic)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new AddQueueForClusterRequest
            {
                ClusterName = clusterName,
                Topic = topic
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.AddQueue, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("AddQueue failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task AddQueue(string clusterName, string brokerName, string topic)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var requestData = _binarySerializer.Serialize(new AddQueueRequest(topic));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.AddQueue, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("AddQueue failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task DeleteQueue(string clusterName, string topic, int queueId)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new DeleteQueueForClusterRequest
            {
                ClusterName = clusterName,
                Topic = topic,
                QueueId = queueId
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.DeleteQueue, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("DeleteQueue failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task DeleteQueue(string clusterName, string brokerName, string topic, int queueId)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var requestData = _binarySerializer.Serialize(new DeleteQueueRequest(topic, queueId));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.DeleteQueue, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("DeleteQueue failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task SetQueueProducerVisible(string clusterName, string topic, int queueId, bool visible)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new SetQueueProducerVisibleForClusterRequest
            {
                ClusterName = clusterName,
                Topic = topic,
                QueueId = queueId,
                Visible = visible
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.SetQueueProducerVisible, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("SetQueueProducerVisible failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task SetQueueProducerVisible(string clusterName, string brokerName, string topic, int queueId, bool visible)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var requestData = _binarySerializer.Serialize(new SetQueueProducerVisibleRequest(topic, queueId, visible));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.SetQueueProducerVisible, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("SetQueueProducerVisible failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task SetQueueConsumerVisible(string clusterName, string topic, int queueId, bool visible)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new SetQueueConsumerVisibleForClusterRequest
            {
                ClusterName = clusterName,
                Topic = topic,
                QueueId = queueId,
                Visible = visible
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.SetQueueConsumerVisible, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("SetQueueConsumerVisible failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task SetQueueConsumerVisible(string clusterName, string brokerName, string topic, int queueId, bool visible)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            var requestData = _binarySerializer.Serialize(new SetQueueConsumerVisibleRequest(topic, queueId, visible));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.SetQueueConsumerVisible, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("SetQueueConsumerVisible failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task SetQueueNextConsumeOffset(string clusterName, string consumerGroup, string topic, int queueId, long nextOffset)
        {
            if (nextOffset < 0)
            {
                throw new ArgumentException("nextOffset cannot be small than zero.");
            }
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new SetQueueNextConsumeOffsetForClusterRequest
            {
                ClusterName = clusterName,
                ConsumerGroup = consumerGroup,
                Topic = topic,
                QueueId = queueId,
                NextOffset = nextOffset
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.SetQueueNextConsumeOffset, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("SetQueueNextConsumeOffset failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task SetQueueNextConsumeOffset(string clusterName, string brokerName, string consumerGroup, string topic, int queueId, long nextOffset)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            if (nextOffset < 0)
            {
                throw new ArgumentException("nextOffset cannot be small than zero.");
            }
            var requestData = _binarySerializer.Serialize(new SetQueueNextConsumeOffsetRequest(consumerGroup, topic, queueId, nextOffset));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.SetQueueNextConsumeOffset, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("SetQueueNextConsumeOffset failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task DeleteConsumerGroup(string clusterName, string consumerGroup)
        {
            if (string.IsNullOrEmpty(consumerGroup))
            {
                throw new ArgumentException("consumerGroup cannot be null or empty.");
            }
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new DeleteConsumerGroupForClusterRequest
            {
                ClusterName = clusterName,
                GroupName = consumerGroup
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.DeleteConsumerGroup, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("DeleteConsumerGroup failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task DeleteConsumerGroup(string clusterName, string brokerName, string consumerGroup)
        {
            var remotingClient = await GetBrokerByName(clusterName, brokerName);
            if (string.IsNullOrEmpty(consumerGroup))
            {
                throw new ArgumentException("consumerGroup cannot be null or empty.");
            }
            var requestData = _binarySerializer.Serialize(new DeleteConsumerGroupRequest(consumerGroup));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.DeleteConsumerGroup, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode != ResponseCode.Success)
            {
                throw new Exception(string.Format("DeleteConsumerGroup failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<QueueMessage> GetMessageDetail(string clusterName, string messageId)
        {
            var messageInfo = MessageIdUtil.ParseMessageId(messageId);
            var brokerClientList = await GetClusterBrokers(clusterName);
            var brokerClient = brokerClientList.SingleOrDefault(x => x.BrokerInfo.BrokerName == messageInfo.BrokerName);
            if (brokerClient == null)
            {
                return null;
            }
            var requestData = _binarySerializer.Serialize(new GetMessageDetailRequest(messageId));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.GetMessageDetail, requestData);
            var remotingResponse = await brokerClient.RemotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<QueueMessage>>(remotingResponse.ResponseBody).SingleOrDefault();
            }
            else
            {
                throw new Exception(string.Format("GetMessageDetail failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        public async Task<QueueMessage> GetMessageDetailByQueueOffset(string clusterName, string borkerName, string topic, int queueId, long queueOffset)
        {
            var brokerClientList = await GetClusterBrokers(clusterName);
            var brokerClient = brokerClientList.SingleOrDefault(x => x.BrokerInfo.BrokerName == borkerName);
            if (brokerClient == null)
            {
                return null;
            }
            var requestData = _binarySerializer.Serialize(new GetMessageDetailByQueueOffsetRequest(topic, queueId, queueOffset));
            var remotingRequest = new RemotingRequest((int)BrokerRequestCode.GetMessageDetailByQueueOffset, requestData);
            var remotingResponse = await brokerClient.RemotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<QueueMessage>>(remotingResponse.ResponseBody).SingleOrDefault();
            }
            else
            {
                throw new Exception(string.Format("GetMessageDetailByQueueOffset failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }

        private void StartAllNameServerClients()
        {
            foreach (var nameServerRemotingClient in _nameServerRemotingClientList)
            {
                nameServerRemotingClient.Start();
            }
        }
        private SocketRemotingClient GetAvailableNameServerRemotingClient()
        {
            var availableList = _nameServerRemotingClientList.Where(x => x.IsConnected).ToList();
            if (availableList.Count == 0)
            {
                throw new Exception("No available name server.");
            }
            return availableList[(int)(Interlocked.Increment(ref _nameServerIndex) % availableList.Count)];
        }
        private async Task RefreshClusterBrokers(string clusterName)
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new GetClusterBrokersRequest { ClusterName = clusterName });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.GetClusterBrokers, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);

            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                var brokerInfoList = _binarySerializer.Deserialize<IEnumerable<BrokerInfo>>(remotingResponse.ResponseBody);
                var brokerClientList = new List<BrokerClient>();
                foreach (var brokerInfo in brokerInfoList)
                {
                    var client = new SocketRemotingClient(brokerInfo.BrokerName, brokerInfo.AdminAddress.ToEndPoint(), _eQueueSettingService.SocketSetting).Start();
                    var brokerClient = new BrokerClient { BrokerInfo = brokerInfo, RemotingClient = client };
                    brokerClientList.Add(brokerClient);
                }
                IList<BrokerClient> removedList;
                if (_clusterBrokerDict.TryRemove(clusterName, out removedList))
                {
                    foreach (var brokerClient in removedList)
                    {
                        brokerClient.RemotingClient.Shutdown();
                    }
                }
                _clusterBrokerDict.TryAdd(clusterName, brokerClientList);
            }
            else
            {
                throw new Exception(string.Format("GetClusterBrokers failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
        private async Task<SocketRemotingClient> GetBrokerByName(string clusterName, string brokerName)
        {
            IList<BrokerClient> clientList;
            if (!_clusterBrokerDict.TryGetValue(clusterName, out clientList))
            {
                await RefreshClusterBrokers(clusterName);
                if (!_clusterBrokerDict.TryGetValue(clusterName, out clientList))
                {
                    return null;
                }
            }
            var brokerClient = clientList.SingleOrDefault(x => x.BrokerInfo.BrokerName == brokerName);
            if (brokerClient == null)
            {
                await RefreshClusterBrokers(clusterName);
                if (!_clusterBrokerDict.TryGetValue(clusterName, out clientList))
                {
                    return null;
                }
                brokerClient = clientList.SingleOrDefault(x => x.BrokerInfo.BrokerName == brokerName);
            }
            if (brokerClient != null)
            {
                return brokerClient.RemotingClient;
            }
            return null;
        }
        private async Task<IList<BrokerClient>> GetClusterBrokers(string clusterName)
        {
            if (!_clusterBrokerDict.TryGetValue(clusterName, out IList<BrokerClient> clientList))
            {
                await RefreshClusterBrokers(clusterName);
                if (!_clusterBrokerDict.TryGetValue(clusterName, out clientList))
                {
                    return null;
                }
            }
            return clientList;
        }
        private IList<SocketRemotingClient> CreateRemotingClientList(IEnumerable<IPEndPoint> endpointList)
        {
            var remotingClientList = new List<SocketRemotingClient>();
            foreach (var endpoint in endpointList)
            {
                var remotingClient = new SocketRemotingClient("EQueueWebAdminSocketRemotingClient", endpoint, _eQueueSettingService.SocketSetting);
                remotingClientList.Add(remotingClient);
            }
            return remotingClientList;
        }
        private async Task ScanAccumulateMessages()
        {
            var topicAccumulateInfoList = await GetTopicAccumulateInfoList();
            if (topicAccumulateInfoList.Count() == 0)
            {
                return;
            }
            foreach (var topicAccumulateInfo in topicAccumulateInfoList)
            {
                _sendEmailService.SendMessageAccumulateNotification(topicAccumulateInfo);
            }
        }
        private async Task<IEnumerable<TopicAccumulateInfo>> GetTopicAccumulateInfoList()
        {
            var remotingClient = GetAvailableNameServerRemotingClient();
            var requestData = _binarySerializer.Serialize(new GetTopicAccumulateInfoListRequest
            {
                AccumulateThreshold = _eQueueSettingService.MessageAccumulateThreshold
            });
            var remotingRequest = new RemotingRequest((int)NameServerRequestCode.GetTopicAccumulateInfoList, requestData);
            var remotingResponse = await remotingClient.InvokeAsync(remotingRequest, 30000);
            if (remotingResponse.ResponseCode == ResponseCode.Success)
            {
                return _binarySerializer.Deserialize<IEnumerable<TopicAccumulateInfo>>(remotingResponse.ResponseBody);
            }
            else
            {
                throw new Exception(string.Format("GetTopicAccumulateInfoList failed, errorMessage: {0}", Encoding.UTF8.GetString(remotingResponse.ResponseBody)));
            }
        }
    }
}
