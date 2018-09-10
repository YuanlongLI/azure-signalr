﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Infrastructure;
using Microsoft.AspNet.SignalR.Messaging;
using Microsoft.Azure.SignalR.Protocol;

namespace Microsoft.Azure.SignalR.AspNet
{
    internal class ServiceMessageBus : MessageBus
    {
        private readonly IMessageParser _parser;
        private readonly IServiceConnectionManager _serviceConnectionManager;
        private readonly IAckHandler _ackHandler;

        public ServiceMessageBus(IDependencyResolver resolver) : base(resolver)
        {
            _serviceConnectionManager = resolver.Resolve<IServiceConnectionManager>() ?? throw new ArgumentNullException(nameof(IServiceConnectionManager));
            _parser = resolver.Resolve<IMessageParser>() ?? throw new ArgumentNullException(nameof(IMessageParser));
            _ackHandler = resolver.Resolve<IAckHandler>() ?? throw new ArgumentNullException(nameof(IAckHandler));
        }

        public override Task Publish(Message message)
        {
            var messages = _parser.GetMessages(message).ToList();
            if (messages.Count == 0)
            {
                return Task.CompletedTask;
            }

            if (messages.Count == 1)
            {
                return ProcessMessage(messages[0]);
            }

            return Task.WhenAll(messages.Select(m => ProcessMessage(m)));
        }

        private Task ProcessMessage(AppMessage message)
        {
            if (message is HubMessage hubMessage)
            {
                return WriteMessage(_serviceConnectionManager.WithHub(hubMessage.HubName), message);
            }

            return WriteMessage(_serviceConnectionManager, message);
        }

        private async Task WriteMessage(IServiceConnectionContainer connection, AppMessage appMessage)
        {
            var message = appMessage.Message;
            switch (message)
            {
                // For group related messages, make sure messages are written to the same partition
                case JoinGroupMessage joinGroupMessage:
                    await connection.WriteAsync(joinGroupMessage.GroupName, joinGroupMessage);
                    _ackHandler.TriggerAck(appMessage.RawMessage.CommandId);
                    break;
                case LeaveGroupMessage leaveGroupMessage:
                    await connection.WriteAsync(leaveGroupMessage.GroupName, leaveGroupMessage);
                    break;
                case GroupBroadcastDataMessage groupBroadcastMessage:
                    await connection.WriteAsync(groupBroadcastMessage.GroupName, groupBroadcastMessage);
                    break;
                default:
                    await connection.WriteAsync(message);
                    break;
            }
        }
    }
}
