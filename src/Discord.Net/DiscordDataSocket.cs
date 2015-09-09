﻿using Discord.API.Models;
using Discord.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;
using WebSocketMessage = Discord.API.Models.TextWebSocketCommands.WebSocketMessage;

namespace Discord
{
	internal sealed partial class DiscordDataSocket : DiscordWebSocket
	{
		private readonly ManualResetEventSlim _connectWaitOnLogin, _connectWaitOnLogin2;
		private string _lastSession, _redirectServer;
		private int _lastSeq;

		public DiscordDataSocket(DiscordClient client, int timeout, int interval, bool isDebug)
			: base(client, timeout, interval, isDebug)
		{
			_connectWaitOnLogin = new ManualResetEventSlim(false);
			_connectWaitOnLogin2 = new ManualResetEventSlim(false);
        }

		public override async Task ConnectAsync(string url)
		{
			_lastSeq = 0;
			_lastSession = null;
			_redirectServer = null;
			await BeginConnect().ConfigureAwait(false);
			await base.ConnectAsync(url).ConfigureAwait(false);
		}
		public async Task Login(string token)
		{
			var cancelToken = _disconnectToken.Token;

			_connectWaitOnLogin.Reset();
			_connectWaitOnLogin2.Reset();

			TextWebSocketCommands.Login msg = new TextWebSocketCommands.Login();
			msg.Payload.Token = token;
			msg.Payload.Properties["$os"] = "";
			msg.Payload.Properties["$browser"] = "";
			msg.Payload.Properties["$device"] = "Discord.Net";
			msg.Payload.Properties["$referrer"] = "";
			msg.Payload.Properties["$referring_domain"] = "";
			await SendMessage(msg, cancelToken).ConfigureAwait(false);

			try
			{
				if (!_connectWaitOnLogin.Wait(_timeout, cancelToken)) //Waiting on READY message
					throw new Exception("No reply from Discord server");
			}
			catch (OperationCanceledException)
			{
				if (_disconnectReason == null)
					throw new Exception("An unknown websocket error occurred.");
				else
					_disconnectReason.Throw();
			}
			try { _connectWaitOnLogin2.Wait(cancelToken); } //Waiting on READY handler
			catch (OperationCanceledException) { return; }
			
			if (_isDebug)
				RaiseOnDebugMessage(DebugMessageType.Connection, $"Logged in.");

			SetConnected();
		}

		protected override Task ProcessMessage(string json)
		{
			var msg = JsonConvert.DeserializeObject<WebSocketMessage>(json);
			if (msg.Sequence.HasValue)
				_lastSeq = msg.Sequence.Value;
			switch (msg.Operation)
			{
				case 0:
					{
						if (msg.Type == "READY")
						{
							var payload = (msg.Payload as JToken).ToObject<TextWebSocketEvents.Ready>();
							_lastSession = payload.SessionId;
							_heartbeatInterval = payload.HeartbeatInterval;
							QueueMessage(new TextWebSocketCommands.UpdateStatus());
							//QueueMessage(GetKeepAlive());
							_connectWaitOnLogin.Set(); //Pre-Event
						}
						RaiseGotEvent(msg.Type, msg.Payload as JToken);
						if (msg.Type == "READY")
							_connectWaitOnLogin2.Set(); //Post-Event
					}
					break;
				case 7:
					{
						var payload = (msg.Payload as JToken).ToObject<TextWebSocketEvents.Redirect>();
						if (_isDebug)
							RaiseOnDebugMessage(DebugMessageType.Connection, $"Redirected to {payload.Url}.");
						_host = payload.Url;
						DisconnectInternal(new Exception("Server is redirecting."), true);
					}
					break;
				default:
					if (_isDebug)
						RaiseOnDebugMessage(DebugMessageType.WebSocketUnknownOpCode, "Unknown Opcode: " + msg.Operation);
					break;
			}
			return TaskHelper.CompletedTask;
		}

		protected override object GetKeepAlive()
		{
			return new TextWebSocketCommands.KeepAlive();
        }

		public void JoinVoice(Channel channel)
		{
			var joinVoice = new TextWebSocketCommands.JoinVoice();
			joinVoice.Payload.ServerId = channel.ServerId;
			joinVoice.Payload.ChannelId = channel.Id;
            QueueMessage(joinVoice);
		}
		public void LeaveVoice()
		{
			var joinVoice = new TextWebSocketCommands.JoinVoice();
			QueueMessage(joinVoice);
		}

		protected override void OnConnect()
		{
			if (_redirectServer != null)
			{
				var resumeMsg = new TextWebSocketCommands.Resume();
				resumeMsg.Payload.SessionId = _lastSession;
				resumeMsg.Payload.Sequence = _lastSeq;
				SendMessage(resumeMsg, _disconnectToken.Token);
			}
			_redirectServer = null;
		}
	}
}