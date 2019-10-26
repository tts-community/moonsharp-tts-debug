#if (!UNITY_5) || UNITY_STANDALONE

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MoonSharp.VsCodeDebugger.DebuggerLogic;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.CoreLib;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.VsCodeDebugger.SDK;

namespace MoonSharp.VsCodeDebugger
{
	/// <summary>
	/// Class implementing a debugger allowing attaching from a Visual Studio Code debugging session.
	/// </summary>
	public class MoonSharpVsCodeDebugServer : IDisposable
	{
		readonly int m_Port;
		readonly object m_Lock = new object();

		readonly Dictionary<int, ListenerSessionPair> m_PortSessionDictionary = new Dictionary<int, ListenerSessionPair>();
		readonly List<AsyncDebugger> m_PendingDebuggerList = new List<AsyncDebugger>();

		MoonSharpDebugSession m_MasterSession;

		/// <summary>
		/// Initializes a new instance of the <see cref="MoonSharpVsCodeDebugServer" /> class.
		/// </summary>
		/// <param name="port">The port on which the debugger listens. It's recommended to use 41912.</param>
		public MoonSharpVsCodeDebugServer(int port = 41912)
		{
			m_Port = port;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="MoonSharpVsCodeDebugServer" /> class with a default script.
		/// Note that for this specific script, it will NOT attach the debugger to the script.
		/// </summary>
		/// <param name="script">The script object to debug.</param>
		/// <param name="port">The port on which the debugger listens. It's recommended to use 41912 unless you are going to keep more than one script object around.</param>
		/// <param name="sourceFinder">A function which gets in input a source code and returns the path to
		/// source file to use. It can return null and in that case (or if the file cannot be found)
		/// a temporary file will be generated on the fly.</param>
		[Obsolete("Use the constructor taking only a port, and the 'AttachToScript' method instead.")]
		public MoonSharpVsCodeDebugServer(Script script, int port, Func<SourceCode, string> sourceFinder = null)
		{
			m_Port = port;
			m_PendingDebuggerList.Add(new AsyncDebugger(script, sourceFinder ?? (s => s.Name), "Default script"));
		}

		/// <summary>
		/// Attaches the specified script to the debugger
		/// </summary>
		/// <param name="script">The script.</param>
		/// <param name="name">The name of the script.</param>
		/// <param name="sourceFinder">A function which gets in input a source code and returns the path to
		/// source file to use. It can return null and in that case (or if the file cannot be found)
		/// a temporary file will be generated on the fly.</param>
		/// <exception cref="ArgumentException">If the script is null or has already been attached to this debugger.</exception>
		/// <exception cref="SocketException">If the server is started, but unable to open an additional port.</exception>
		public void AttachToScript(Script script, string name, Func<SourceCode, string> sourceFinder = null)
		{
			if (script == null)
			{
				throw new ArgumentException("Cannot attach to null");
			}

			lock (m_Lock)
			{
				if (m_PortSessionDictionary.Values.Any(p => p.Session.Debugger?.Script == script) || m_PendingDebuggerList.Any((d => d.Script == script)))
				{
					throw new ArgumentException("Script already attached to this debug server.");
				}

				var debugger = new AsyncDebugger(script, sourceFinder ?? (s => s.Name), name);

				if (m_MasterSession is DetachedDebugSession)
				{
					ReplaceListenerDebugger(m_Port, debugger);
				}
				else if (m_MasterSession == null)
				{
					m_PendingDebuggerList.Add(debugger);
				}
				else
				{
					StartListener(0, debugger);
				}
			}
		}

		/// <summary>
		/// Replaces the script a debugger is debugging.
		/// </summary>
		/// <param name="previousScript">A script already attached for debugging.</param>
		/// <param name="newScript">A replacement script.</param>
		/// <exception cref="ArgumentException">If the script has not been attached to this debugger.</exception>
		public void ReplaceAttachedScript(Script previousScript, Script newScript, string name = null, Func<SourceCode, string> sourceFinder = null)
		{
			lock (m_Lock)
			{
				if (newScript == null)
				{
					Detach(previousScript);
				}
				else if (m_PortSessionDictionary.Count > 0)
				{
					MoonSharpDebugSession session = m_PortSessionDictionary.Values.FirstOrDefault(p => p.Session.Debugger?.Script == previousScript).Session;

					if (session == null)
					{
						throw new ArgumentException($"Cannot replace script \"{name}\" that is not attached to this debug server.");
					}

					AsyncDebugger newDebugger = new AsyncDebugger(newScript, sourceFinder ?? session.Debugger.SourceFinder, name ?? session.Debugger.Name);
					ReplaceListenerDebugger(session.Port, newDebugger);
				}
				else
				{
					int index = m_PendingDebuggerList.FindIndex(d => d.Script == previousScript);

					if (index < 0)
					{
						throw new ArgumentException($"Cannot replace script \"{name}\" that is not attached to this pending debug server.");
					}

					previousScript.DetachDebugger();

					AsyncDebugger previousDebugger = m_PendingDebuggerList[index];
					AsyncDebugger newDebugger = new AsyncDebugger(newScript, sourceFinder ?? previousDebugger.SourceFinder, name ?? previousDebugger.Name);

					m_PendingDebuggerList[index] = newDebugger;
				}
			}
		}

		/// <summary>
		/// Detaches the specified script. The debugger attached to that script will be disconnected, the debug session terminated and TCP socket closed.
		/// </summary>
		/// <param name="script">The script.</param>
		/// <exception cref="ArgumentException">Thrown if the script cannot be found.</exception>
		public void Detach(Script script)
		{
			if (script == null)
			{
				throw new ArgumentException("Cannot detach null.");
			}

			lock (m_Lock)
			{
				if (m_MasterSession != null)
				{
					if (m_MasterSession.Debugger?.Script == script)
					{
						ReplaceListenerDebugger(m_Port, null);
					}
					else
					{
						ListenerSessionPair listenerSessionPair = m_PortSessionDictionary.Values.FirstOrDefault(d => d.Session.Debugger?.Script == script);

						if (listenerSessionPair.Session == null)
						{
							throw new ArgumentException($"Cannot detach script that is not attached to this debug server.");
						}

						StopListener(listenerSessionPair.Session.Port);
					}
				}
				else
				{
					if (m_PendingDebuggerList.RemoveAll((d) => d.Script == script) == 0)
					{
						throw new ArgumentException($"Cannot detach script that is not attached to this pending debug server.");
					}
				}
			}
		}

		/// <summary>
		/// Gets a list of the attached debuggers by id and name
		/// </summary>
		public IEnumerable<KeyValuePair<int, string>> GetAttachedDebuggersByIdAndName()
		{
			lock (m_Lock)
			{
				return m_PortSessionDictionary.Values
					.Where(p => p.Session.Debugger != null)
					.Select(p => new KeyValuePair<int, string>(p.Session.Debugger.Id, p.Session.Debugger.Name))
					.Concat(m_PendingDebuggerList.Select(d => new KeyValuePair<int, string>(d.Id, d.Name)))
					.OrderBy(p => p.Key);
			}
		}

		/// <summary>
		/// Gets a list of the listening sessions by port and debugger name
		/// </summary>
		public IEnumerable<KeyValuePair<int, string>> GetSessionsByPortAndName()
		{
			lock (m_Lock)
			{
				return m_PortSessionDictionary.Values
					.Select(p => new KeyValuePair<int, string>(((IPEndPoint) p.Listener.LocalEndpoint).Port, p.Session.Name))
					.OrderBy(p => p.Key);
			}
		}

		/// <summary>
		/// Gets a list of the attached debuggers by id and name
		/// </summary>
		public IEnumerable<KeyValuePair<int, string>> GetListenersByPortAndDebuggerName()
		{
			lock (m_Lock)
			{
				return m_PortSessionDictionary.Keys
					.Select(port => new KeyValuePair<int, string>(port, m_PortSessionDictionary[port].Session.Debugger?.Name ?? "Unattached"));
			}
		}

		/// <summary>
		/// Gets or sets a delegate which will be called when logging messages are generated
		/// </summary>
		public Action<string> Logger { get; set; }


		/// <summary>
		/// Gets the debugger object. Obsolete, use the AttachToScript method instead.
		/// </summary>
		[Obsolete("Use the AttachToScript method instead.")]
		public IDebugger GetDebugger()
		{
			return m_MasterSession?.Debugger;
		}

		/// <summary>
		/// Gets or sets the current script by ID (see GetAttachedDebuggersByIdAndName). Obsolete.
		/// </summary>
		[Obsolete("Each script/debugger now has its own debug session.")]
		public int? CurrentId
		{
			get { return m_MasterSession?.Debugger?.Id; }
			set
			{
				if (m_MasterSession?.Debugger?.Id != value)
				{
					lock (m_Lock)
					{
						if (m_MasterSession != null)
						{
							if (value == null)
							{
								AsyncDebugger currentDebugger = m_MasterSession.Debugger;

								ReplaceListenerDebugger(m_Port, null);

								if (currentDebugger != null)
								{
									StartListener(0, currentDebugger);
								}
							}
							else
							{
								MoonSharpDebugSession session = m_PortSessionDictionary.Values.FirstOrDefault(p => p.Session.Debugger?.Id == value).Session;

								if (session == null)
								{
									throw new ArgumentException("Cannot find debugger with given Id.");
								}

								SwapListenerDebuggers(m_Port, session.Port);
							}
						}
						else if (value != null)
						{
							int index = m_PendingDebuggerList.FindIndex(d => d.Id == value);

							if (index < 0)
							{
								throw new ArgumentException("Cannot find debugger with given Id.");
							}

							AsyncDebugger debugger = m_PendingDebuggerList[index];
							m_PendingDebuggerList.RemoveAt(index);
							m_PendingDebuggerList.Insert(0, debugger);
						}
					}
				}
			}
		}

		/// <summary>
		/// Gets or sets the current script. New vscode connections will attach to this script. Changing the current script does NOT disconnect
		/// connected clients. Obsolete.
		/// </summary>
		[Obsolete("Each script/debugger now has its own debug session.")]
		public Script Current
		{
			get { return m_MasterSession?.Debugger?.Script; }
			set
			{
				if (value == null)
				{
					CurrentId = null;
				}
				else
				{
					lock (m_Lock)
					{
						MoonSharpDebugSession session = m_PortSessionDictionary.Values.FirstOrDefault(p => p.Session.Debugger?.Script == value).Session;

						if (session == null)
						{
							throw new ArgumentException("Cannot find debugger with given script associated.");
						}

						CurrentId = session.Debugger.Id;
					}
				}
			}
		}

		/// <summary>
		/// Stops listening
		/// </summary>
		public void Dispose()
		{
			lock (m_Lock)
			{
				int port;

				while ((port = m_PortSessionDictionary.Keys.FirstOrDefault()) != 0)
				{
					StopListener(port);
				}
			}
		}

		/// <summary>
		/// Starts listening on the localhost for incoming connections.
		/// </summary>
		public MoonSharpVsCodeDebugServer Start()
		{
			lock (m_Lock)
			{
				if (m_MasterSession != null)
				{
					throw new InvalidOperationException("Cannot start; server has already been started.");
				}

				m_MasterSession = StartListener(m_Port, m_PendingDebuggerList.FirstOrDefault());

				foreach (AsyncDebugger debugger in m_PendingDebuggerList.Skip(1))
				{
					StartListener(0, debugger);
				}

				m_PendingDebuggerList.Clear();

				return this;
			}
		}

		private MoonSharpDebugSession StartListener(int desiredPort, AsyncDebugger debugger)
		{
			lock (m_Lock)
			{
				TcpListener listener = new TcpListener(IPAddress.Loopback, desiredPort);
				listener.Start();

				int port = ((IPEndPoint) listener.LocalEndpoint).Port;

				MoonSharpDebugSession session = debugger != null
					? (MoonSharpDebugSession) new ScriptDebugSession(port, this, debugger)
					: new DetachedDebugSession(port, this);

				debugger?.Script.AttachDebugger(debugger);

				ListenerSessionPair listenerSessionPair = new ListenerSessionPair(listener, session);
				m_PortSessionDictionary.Add(port, listenerSessionPair);

				SpawnThread("VsCodeDebugServer_" + port, () => {
					ListenThread(listener);
					m_PortSessionDictionary.Remove(port);
				});

				return session;
			}
		}

		private void StopListener(int port)
		{
			MoonSharpDebugSession session = m_PortSessionDictionary[port].Session;
			m_PortSessionDictionary.Remove(port); // Prevent listener accepting further connections

			if (m_PortSessionDictionary[port].Listener.Server.IsBound)
			{
				session.Debugger?.Script?.DetachDebugger();
				session.Terminate();
			}
			else
			{
				m_PortSessionDictionary[port].Listener.Stop();
			}
		}

		private void ReplaceListenerDebugger(int port, AsyncDebugger debugger)
		{
			MoonSharpDebugSession previousSession = m_PortSessionDictionary[port].Session;
			TcpListener listener = m_PortSessionDictionary[port].Listener;

			MoonSharpDebugSession newSession = debugger != null
				? (MoonSharpDebugSession) new ScriptDebugSession(port, this, debugger)
				: new DetachedDebugSession(port, this);

			previousSession.Debugger?.Script?.DetachDebugger();
			debugger?.Script?.AttachDebugger(debugger);

			m_PortSessionDictionary[port] = new ListenerSessionPair(listener, newSession);

			if (port == m_Port)
			{
				m_MasterSession = newSession;
			}

			previousSession.Terminate(true);
		}

		private void SwapListenerDebuggers(int port1, int port2)
		{
			MoonSharpDebugSession session1 = m_PortSessionDictionary[port1].Session;
			MoonSharpDebugSession session2 = m_PortSessionDictionary[port2].Session;

			AsyncDebugger session1Debugger = session1.Debugger;
			AsyncDebugger session2Debugger = session2.Debugger;

			if (session2Debugger != null || port1 == m_Port)
			{
				ReplaceListenerDebugger(port1, session2Debugger);
			}
			else
			{
				StopListener(port1);
			}

			if (session1Debugger != null || port2 == m_Port)
			{
				ReplaceListenerDebugger(port2, session1Debugger);
			}
			else
			{
				StopListener(port2);
			}
		}

		private void ListenThread(TcpListener listener)
		{
			try
			{
				int port = ((IPEndPoint) listener.LocalEndpoint).Port;
				string sessionIdentifier = port.ToString();
				MoonSharpDebugSession session;

				lock (m_Lock)
				{
					session = m_PortSessionDictionary[port].Session;
				}

				while (session != null)
				{
					var clientSocket = listener.AcceptSocket();

					Log("[{0}] : Accepted connection from client {1}", sessionIdentifier, clientSocket.RemoteEndPoint);

					MoonSharpDebugSession threadSession;

					lock (m_Lock)
					{
						threadSession = m_PortSessionDictionary[port].Session;
					}

					SpawnThread("VsCodeDebugSession_" + sessionIdentifier, () => {
						using (var networkStream = new NetworkStream(clientSocket))
						{
							try
							{
								threadSession.ProcessLoop(networkStream, networkStream);
							}
							catch (Exception ex)
							{
								Log("[{0}] : Error : {1}", ex.Message);
							}
						}

						clientSocket.Shutdown(SocketShutdown.Both);
						clientSocket.Close();

						Log("[{0}] : Client connection closed", sessionIdentifier);
					});

					lock (m_Lock)
					{
						session = m_PortSessionDictionary[port].Session;
					}
				}
			}
			catch (Exception e)
			{
				Log("Fatal error in listening thread : {0}", e.Message);
			}
			finally
			{
				listener?.Stop();
			}
		}

		private void Log(string format, params object[] args)
		{
			Action<string> logger = Logger;

			if (logger != null)
			{
				string msg = string.Format(format, args);
				logger(msg);
			}
		}


		private static void SpawnThread(string name, Action threadProc)
		{
			new System.Threading.Thread(() => threadProc())
			{
				IsBackground = true,
				Name = name
			}
			.Start();
		}


		private struct ListenerSessionPair
		{
			public TcpListener Listener { get; }
			public MoonSharpDebugSession Session { get; }

			public ListenerSessionPair(TcpListener listener, MoonSharpDebugSession session)
			{
				Listener = listener;
				Session = session;
			}
		}
	}
}

#else
using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace MoonSharp.VsCodeDebugger
{
	public class MoonSharpVsCodeDebugServer : IDisposable
	{
		public MoonSharpVsCodeDebugServer(int port = 41912)
		{
		}

		[Obsolete("Use the constructor taking only a port, and the 'Attach' method instead.")]
		public MoonSharpVsCodeDebugServer(Script script, int port, Func<SourceCode, string> sourceFinder = null)
		{
		}

		public void AttachToScript(Script script, string name, Func<SourceCode, string> sourceFinder = null)
		{
		}

		public void ReplaceAttachedScript(Script previousScript, Script newScript, string name = null, Func<SourceCode, string> sourceFinder = null)
		{
		}

		public IEnumerable<KeyValuePair<int, string>> GetAttachedDebuggersByIdAndName()
		{
			yield break;
		}

		public IEnumerable<KeyValuePair<int, string>> GetListenersByPortAndDebuggerName()
		{
			yield break;
		}

		public int? CurrentId
		{
			get { return null; }
			set { }
		}


		public Script Current
		{
			get { return null; }
			set { }
		}

		/// <summary>
		/// Detaches the specified script. The debugger attached to that script will get disconnected.
		/// </summary>
		/// <param name="script">The script.</param>
		/// <exception cref="ArgumentException">Thrown if the script cannot be found.</exception>
		public void Detach(Script script)
		{

		}

		public Action<string> Logger { get; set; }


		[Obsolete("Use the Attach method instead.")]
		public IDebugger GetDebugger()
		{
			return null;
		}

		public void Dispose()
		{
		}

		public MoonSharpVsCodeDebugServer Start()
		{
			return this;
		}

	}
}
#endif
