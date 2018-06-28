﻿using Microsoft.AspNet.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace SignalR2Chat
{
    public class AdvancedChat : Hub
    {
        public override Task OnConnected()
        {
            return base.OnConnected();
        }

        public void BroadcastMessage(string name, string message)
        {
            Clients.All.broadcastMessage(name, message);
        }

        public void Echo(string name, string message)
        {
            Clients.Client(Context.ConnectionId).echo(name, message + " (echo from server)");
        }

        public async void JoinGroup(string name, string groupName)
        {
            await Groups.Add(Context.ConnectionId, groupName);
            await Clients.Group(groupName).echo("_SYSTEM_", $"{name} joined {groupName} with connectionId {Context.ConnectionId}");
        }

        public async void LeaveGroup(string name, string groupName)
        {
            await Groups.Remove(Context.ConnectionId, groupName);
            await Clients.Client(Context.ConnectionId).echo("_SYSTEM_", $"{name} leaved {groupName}");
            await Clients.Group(groupName).echo("_SYSTEM_", $"{name} leaved {groupName}");
        }

        public void SendGroup(string name, string groupName, string message)
        {
            Clients.Group(groupName).echo(name, message);
        }

        public void SendGroups(string name, IList<string> groups, string message)
        {
            Clients.Groups(groups).echo(name, message);
        }

        public void SendGroupExcept(string name, string groupName, string[] connectionIdExcept, string message)
        {
            Clients.Groups(new List<string> { groupName }, connectionIdExcept).echo(name, message);
        }

        public void SendUser(string name, string userId, string message)
        {
            Clients.User(userId).echo(name, message);
        }

        public void SendUsers(string name, IList<string> userIds, string message)
        {
            Clients.Users(userIds).echo(name, message);
        }

        public override Task OnDisconnected(bool stopCalled)
        {
            return base.OnDisconnected(stopCalled);
        }
    }
}