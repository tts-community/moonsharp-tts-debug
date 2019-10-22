using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using MoonSharp.VsCodeDebugger.SDK;

namespace MoonSharp.VsCodeDebugger.DebuggerLogic
{
	public class Session
	{
		public int port { get; }
		public string name { get; }

		public Session(int port, string name)
		{
			this.port = port;
			this.name = name;
		}
	}

	public class SessionsResponseBody : ResponseBody
	{
		public Session[] sessions { get; }

		public SessionsResponseBody(List<Session> sessions)
		{
			this.sessions = sessions.ToArray<Session>();
		}
	}

	internal abstract class MoonSharpDebugSession : DebugSession
	{
		readonly object _sendLock = new object();

		public int Port { get; }

		public MoonSharpVsCodeDebugServer Server { get; }

		public AsyncDebugger Debugger { get; } // Can be null!

		public abstract string Name { get; }

		internal MoonSharpDebugSession(int port, MoonSharpVsCodeDebugServer server, AsyncDebugger debugger)
		{
			Port = port;
			Server = server;
			Debugger = debugger;
		}

		protected override void SendMessage(ProtocolMessage message)
		{
			lock (_sendLock)
			{
				base.SendMessage(message);
			}
		}

		protected override void DispatchRequest(string command, Table args, Response response)
		{
			if (args == null)
			{
				args = new Table(null);
			}

			try
			{
				switch (command)
				{
					case "_sessions":
						Sessions(response, args);
						return;
				}
			}
			catch (Exception e)
			{
				SendErrorResponse(response, 1104, "error while processing request '{_request}' (exception: {_exception})", new { _request = command, _exception = e.Message });
			}

			base.DispatchRequest(command, args, response);
		}

		protected void SendTerminateEvent(bool restart)
		{
			SendEvent(new TerminatedEvent(restart ? new {} : null));
		}

		public void Sessions(Response response, Table arguments)
		{
			List<Session> sessions = Server.GetSessionsByPortAndName().Select((p => new Session(p.Key, p.Value))).ToList();
			SendResponse(response, new SessionsResponseBody(sessions));
		}

		public abstract void Terminate(bool restart = false);
	}
}
