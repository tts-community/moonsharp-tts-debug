#if (!PCL) && ((!UNITY_5) || UNITY_STANDALONE)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.VsCodeDebugger.SDK;


namespace MoonSharp.VsCodeDebugger.DebuggerLogic
{
	internal class ScriptDebugSession : MoonSharpDebugSession, IAsyncDebuggerClient
	{
		const int SCOPE_GLOBAL = 0;
		const int SCOPE_SELF = 1;
		const int SCOPE_LOCAL = 2;
		const int SCOPE_CLOSURE = 3;

		const int VARIABLES_REFERENCE_FRAME_OFFSET = 21;
		const int VARIABLES_REFERENCE_SCOPE_OFFSET = 29; // Scope is 2 bits, but variablesReference is [1, 2147483647] i.e. The sign bit is wasted.

		const int VARIABLES_REFERENCE_FRAME_MASK = 0xFF << VARIABLES_REFERENCE_FRAME_OFFSET;
		const int VARIABLES_REFERENCE_SCOPE_MASK = 0xFF << VARIABLES_REFERENCE_SCOPE_OFFSET;
		const int VARIABLES_REFERENCE_INDEX_MASK = ~(VARIABLES_REFERENCE_FRAME_MASK | VARIABLES_REFERENCE_SCOPE_MASK); // 21 bits, 0 excluded, max 2097151

		const int STOP_REASON_STEP = 0;
		const int STOP_REASON_BREAKPOINT = 1;
		const int STOP_REASON_EXCEPTION = 2;
		const int STOP_REASON_PAUSED = 3;

		readonly object m_Lock = new object();
		readonly List<DynValue> m_Variables = new List<DynValue>();
		List<WatchItem> m_CurrentCallStack = new List<WatchItem>();
		int m_CurrentStackFrame = -1;
		int m_PendingStackFrame = -1;

		readonly VariablesScopeState m_Local = new VariablesScopeState("Local");
		readonly VariablesScopeState m_Closure = new VariablesScopeState("Closure");

		bool m_NotifyExecutionEnd = false;
		bool m_RestartOnUnbind = false;

		int m_StopReason = STOP_REASON_STEP;
		ScriptRuntimeException m_RuntimeException;

		public override string Name => Debugger.Name;

		private class CallStackFrameComparator : IEqualityComparer<WatchItem>
		{
			public bool Equals(WatchItem x, WatchItem y)
			{
				return x.Address == y.Address && x.BasePtr == y.BasePtr && x.RetAddress == y.RetAddress;
			}

			public int GetHashCode(WatchItem obj)
			{
				int hash = 27;
				hash = (13 * hash) + obj.Address;
				hash = (13 * hash) + obj.BasePtr;
				hash = (13 * hash) + obj.RetAddress;
				return hash;

			}
		}

		internal ScriptDebugSession(int port, MoonSharpVsCodeDebugServer server, AsyncDebugger debugger) : base(port, server, debugger)
		{
			if (debugger == null)
			{
				throw new ArgumentNullException(nameof(debugger));
			}
		}

		public override void Initialize(Response response, Table args)
		{
#if DOTNET_CORE
			SendText("Connected to MoonSharp {0} [{1}]",
					 Script.VERSION,
					 Script.GlobalOptions.Platform.GetPlatformName());
#else
			SendText("Connected to MoonSharp {0} [{1}] on process {2} (PID {3})",
					 Script.VERSION,
					 Script.GlobalOptions.Platform.GetPlatformName(),
					 System.Diagnostics.Process.GetCurrentProcess().ProcessName,
					 System.Diagnostics.Process.GetCurrentProcess().Id);
#endif

			SendText("Debugging script '{0}'; use the debug console to debug another script.", Debugger.Name);

			SendText("Type '!help' in the Debug Console for available commands.");

			SendResponse(response, new Capabilities() {
				// This debug adapter does not need the configurationDoneRequest.
				supportsConfigurationDoneRequest = false,

				// This debug adapter does not support function breakpoints.
				supportsFunctionBreakpoints = false,

				// This debug adapter doesn't support conditional breakpoints.
				supportsConditionalBreakpoints = false,

				// This debug adapter does not support a side effect free evaluate request for data hovers.
				supportsEvaluateForHovers = true,

				// This debug adapter supports exception info.
				supportsExceptionInfoRequest = true,

				// This debug adapter does not support exception breakpoint filters
				exceptionBreakpointFilters = new object[0]
			});

			// Debugger is ready to accept breakpoints immediately
			SendEvent(new InitializedEvent());

			Debugger.Client = this;
		}

		public override void Attach(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void Continue(Response response, Table arguments)
		{
			m_StopReason = STOP_REASON_BREAKPOINT;
			Debugger.QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.Run });
			SendResponse(response);
		}

		public override void Disconnect(Response response, Table arguments)
		{
			Debugger.Client = null;
			SendResponse(response);
		}

		private static string getString(Table args, string property, string dflt = null)
		{
			var s = (string)args[property];
			if (s == null)
			{
				return dflt;
			}
			s = s.Trim();
			if (s.Length == 0)
			{
				return dflt;
			}
			return s;
		}

		public override void Evaluate(Response response, Table args)
		{
			var expression = getString(args, "expression");
			var frameId = getInt(args, "frameId", 0);
			var context = getString(args, "context") ?? "hover";

			if (context == "repl" && expression.StartsWith("!"))
			{
				ExecuteRepl(expression.Substring(1));
				SendResponse(response);
				return;
			}

			lock (m_Lock)
			{
				var result = Debugger.Evaluate(expression, frameId) ?? DynValue.Nil;

				m_Variables.Add(result);

				SendResponse(response, new EvaluateResponseBody(result.ToDebugPrintString(), m_Variables.Count) {
					type = result.Type.ToLuaDebuggerString()
				});
			}
		}

		private ExceptionDetails ExceptionDetails(ScriptRuntimeException ex)
		{
			return new ExceptionDetails(
				ex.Message,
				null,
				null,
				null,
				null,
				null
			);
		}

		private bool IsRuntimeExceptionCurrent()
		{
			if (m_RuntimeException == null)
			{
				return false;
			}

			IList<WatchItem> exceptionCallStack = m_RuntimeException.CallStack;
			IList<WatchItem> debuggerCallStack = Debugger.GetWatches(WatchType.CallStack);

			return exceptionCallStack.Count == debuggerCallStack.Count && exceptionCallStack.SequenceEqual(debuggerCallStack, new CallStackFrameComparator());
		}

		public override void ExceptionInfo(Response response, Table arguments)
		{
			if (IsRuntimeExceptionCurrent())
			{
				SendResponse(response, new ExceptionInfoResponseBody("runtime", "Runtime exception", "userUnhandled", ExceptionDetails(m_RuntimeException)));
			}
			else
			{
				SendResponse(response);
			}
		}

		private void ExecuteRepl(string cmd)
		{
			bool showHelp = false;
			cmd = cmd.Trim();
			if (cmd == "help")
			{
				showHelp = true;
			}
			else if (cmd.StartsWith("geterror"))
			{
				SendText("Current error regex : {0}", Debugger.ErrorRegex.ToString());
			}
			else if (cmd.StartsWith("seterror"))
			{
				string regex = cmd.Substring("seterror".Length).Trim();

				try
				{
					Regex rx = new Regex(regex);
					Debugger.ErrorRegex = rx;
					SendText("Current error regex : {0}", Debugger.ErrorRegex.ToString());
				}
				catch (Exception ex)
				{
					SendText("Error setting regex: {0}", ex.Message);
				}
			}
			else if (cmd.StartsWith("execendnotify"))
			{
				string val = cmd.Substring("execendnotify".Length).Trim();

				if (val == "off")
				{
					m_NotifyExecutionEnd = false;
				}
				else if (val == "on")
				{
					m_NotifyExecutionEnd = true;
				}
				else if (val.Length > 0)
					SendText("Error : expected 'on' or 'off'");

				SendText("Notifications of execution end are : {0}", m_NotifyExecutionEnd ? "enabled" : "disabled");
			}
			else
			{
				SendText("Syntax error : {0}\n", cmd);
				showHelp = true;
			}

			if (showHelp)
			{
				SendText("Available commands : ");
				SendText("    !help - gets this help");
				SendText("    !seterror <regex> - sets the regex which tells which errors to trap");
				SendText("    !geterror - gets the current value of the regex which tells which errors to trap");
				SendText("    !execendnotify [on|off] - sets the notification of end of execution on or off (default = off)");
				SendText("    ... or type an expression to evaluate it on the fly.");
			}
		}


		public override void Launch(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void Next(Response response, Table arguments)
		{
			m_StopReason = STOP_REASON_STEP;
			Debugger.QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.StepOver });
			SendResponse(response);
		}

		private StoppedEvent CreateStoppedEvent(string reason, string description, string text = null)
		{
			return new StoppedEvent(0, reason, description, text);
		}

		public override void Pause(Response response, Table arguments)
		{
			m_StopReason = STOP_REASON_PAUSED;
			Debugger.PauseRequested = true;
			SendResponse(response);
			SendText("Pause pending -- will pause at first script statement.");
		}

		public override void Source(Response response, Table arguments)
		{
			SendErrorResponse(response, 1020, "No source available");
		}

		public override void Scopes(Response response, Table arguments)
		{
			var scopes = new List<Scope>();
			var frameId = getInt(arguments, "frameId", 0);

			if (frameId >= 0 && frameId < Debugger.GetWatches(WatchType.CallStack).Count)
			{
				var frame = frameId + 1; // DAP treats variablesReference 0 as NULL, to avoid hitting that value with the SCOPE_GLOBAL we always +1 the frameId.
				scopes.Add(new Scope("Local", (SCOPE_LOCAL << VARIABLES_REFERENCE_SCOPE_OFFSET) | (frame << VARIABLES_REFERENCE_FRAME_OFFSET)));
				scopes.Add(new Scope("Closure", (SCOPE_CLOSURE << VARIABLES_REFERENCE_SCOPE_OFFSET) | (frame << VARIABLES_REFERENCE_FRAME_OFFSET)));
				scopes.Add(new Scope("Global", (SCOPE_GLOBAL << VARIABLES_REFERENCE_SCOPE_OFFSET) | (frame << VARIABLES_REFERENCE_FRAME_OFFSET), true));
				scopes.Add(new Scope("Self", (SCOPE_SELF << VARIABLES_REFERENCE_SCOPE_OFFSET) | (frame << VARIABLES_REFERENCE_FRAME_OFFSET)));
			}

			SendResponse(response, new ScopesResponseBody(scopes));
		}

		public override void SetBreakpoints(Response response, Table args)
		{
			string path = null;

			Table args_source = args["source"] as Table;

			if (args_source != null)
			{
				string p = args_source["path"].ToString();
				if (p != null && p.Trim().Length > 0)
					path = p;
			}

			if (path == null)
			{
				SendErrorResponse(response, 3010, "setBreakpoints: property 'source' is empty or malformed", null, false, true);
				return;
			}

			path = ConvertClientPathToDebugger(path);

			SourceCode src = Debugger.FindSourceByName(path);

			if (src == null)
			{
				// we only support breakpoints in files MoonSharp can handle
				SendResponse(response, new SetBreakpointsResponseBody());
				return;
			}

			Table clientLines = args.Get("lines").Table;

			var lin = new HashSet<int>(clientLines.Values.Select(jt => ConvertClientLineToDebugger(jt.ToObject<int>())).ToArray());

			var lin2 = Debugger.DebugService.ResetBreakPoints(src, lin);

			var breakpoints = new List<Breakpoint>();
			foreach (var l in lin)
			{
				breakpoints.Add(new Breakpoint(lin2.Contains(l), l));
			}

			response.SetBody(new SetBreakpointsResponseBody(breakpoints)); SendResponse(response);
		}

		public override void StackTrace(Response response, Table args)
		{
			int maxLevels = getInt(args, "levels", 10);

			var stackFrames = new List<StackFrame>();

			var stack = Debugger.GetWatches(WatchType.CallStack);

			var coroutine = Debugger.GetWatches(WatchType.Threads).LastOrDefault();

			int level = 0;
			int max = Math.Min(maxLevels - 3, stack.Count);

			while (level < max)
			{
				WatchItem frame = stack[level];

				string name = frame.Name;
				SourceRef sourceRef = frame.Location ?? DefaultSourceRef;
				int sourceIdx = sourceRef.SourceIdx;
				string sourceFile = Debugger.GetSourceFile(sourceIdx);
				string sourcePath = sourceRef.IsClrLocation ? "(native)" : ConvertDebuggerPathToClient(sourceFile);
				string sourceName = sourceRef.IsClrLocation ? sourcePath : Path.GetFileName(sourcePath);

				bool sourceAvailable = !sourceRef.IsClrLocation && sourceFile != null;
				int sourceReference = 0;
				string sourceHint = sourceRef.IsClrLocation ? SDK.Source.HINT_DEEMPHASIZE : (level == 0 ? SDK.Source.HINT_EMPHASIZE : SDK.Source.HINT_NORMAL);
				var source = sourceAvailable ? new Source(sourceName, sourcePath, sourceReference, sourceHint) : null;

				string stackHint = sourceRef.IsClrLocation ? StackFrame.HINT_LABEL : (sourceFile != null ? StackFrame.HINT_NORMAL : StackFrame.HINT_SUBTLE);
				stackFrames.Add(new StackFrame(level, name, source,
					ConvertDebuggerLineToClient(sourceRef.FromLine), sourceRef.FromChar,
					ConvertDebuggerLineToClient(sourceRef.ToLine), sourceRef.ToChar,
					stackHint));

				level++;
			}

			if (stack.Count > maxLevels - 3)
				stackFrames.Add(new StackFrame(level++, "(...)", null, 0));

			if (coroutine != null)
				stackFrames.Add(new StackFrame(level++, "(" + coroutine.Name + ")", null, 0));
			else
				stackFrames.Add(new StackFrame(level++, "(main coroutine)", null, 0));

			stackFrames.Add(new StackFrame(level++, "(native)", null, 0));

			SendResponse(response, new StackTraceResponseBody(stackFrames, stack.Count + 2));
		}

		readonly SourceRef DefaultSourceRef = new SourceRef(-1, 0, 0, 0, 0, false);


		private int getInt(Table args, string propName, int defaultValue)
		{
			var jo = args.Get(propName);

			if (jo.Type != DataType.Number)
				return defaultValue;
			else
				return jo.ToObject<int>();
		}


		public override void StepIn(Response response, Table arguments)
		{
			m_StopReason = STOP_REASON_STEP;
			Debugger.QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.StepIn });
			SendResponse(response);
		}

		public override void StepOut(Response response, Table arguments)
		{
			m_StopReason = STOP_REASON_STEP;
			Debugger.QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.StepOut });
			SendResponse(response);
		}

		public override void Threads(Response response, Table arguments)
		{
			var threads = new List<Thread>() { new Thread(0, "Main Thread") };
			SendResponse(response, new ThreadsResponseBody(threads));
		}

		public override void Variables(Response response, Table arguments)
		{
			lock (m_Lock)
			{
				int variablesReference = getInt(arguments, "variablesReference", -1);

				int scope = (variablesReference & VARIABLES_REFERENCE_SCOPE_MASK) >> VARIABLES_REFERENCE_SCOPE_OFFSET;
				int frameId = ((variablesReference & VARIABLES_REFERENCE_FRAME_MASK) >> VARIABLES_REFERENCE_FRAME_OFFSET) - 1;
				int index = variablesReference & VARIABLES_REFERENCE_INDEX_MASK;

				if (scope == SCOPE_LOCAL || scope == SCOPE_CLOSURE)
				{
					var scopeState = scope == SCOPE_LOCAL ? m_Local : m_Closure;

					if (frameId == scopeState.CurrentStackFrame)
					{
						var watchType = scope == SCOPE_LOCAL ? WatchType.Locals : WatchType.Closure;
						SendScopeVariablesResponse(watchType, response);
					}
					else
					{
						if (scopeState.PendingResponse != null)
						{
							SendErrorResponse(scopeState.PendingResponse, 1200, $"pending {scopeState.Name} (Variables) request cancelled");
							scopeState.PendingResponse = null;
						}

						scopeState.PendingResponse = response;

						if (m_PendingStackFrame != frameId)
						{
							var otherScopeState = scope == SCOPE_LOCAL ? m_Closure : m_Local;

							if (otherScopeState.PendingResponse != null)
							{
								SendErrorResponse(otherScopeState.PendingResponse, 1200, $"pending {otherScopeState.Name} (Variables) request cancelled");
								otherScopeState.PendingResponse = null;
							}

							m_PendingStackFrame = frameId;

							if (frameId < Debugger.GetWatches(WatchType.CallStack).Count)
							{
								Debugger.QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.ViewFrame, StackFrame = m_PendingStackFrame });
							}
							else
							{
								SendResponse(response, new VariablesResponseBody(new List<Variable>()));
							}
						}
					}

					return;
				}

				var variables = new List<Variable>();

				if (scope == SCOPE_SELF)
				{
					var self = Debugger.Evaluate("self", m_CurrentStackFrame);
					VariableInspector.InspectVariable(self, variables, m_Variables);
				}
				else if (scope == SCOPE_GLOBAL)
				{
					if (index == 0)
					{
						var global = Debugger.Evaluate("_G", m_CurrentStackFrame);
						VariableInspector.InspectVariable(global, variables, m_Variables);
					}
					else if (index <= m_Variables.Count)
					{
						VariableInspector.InspectVariable(m_Variables[index - 1], variables, m_Variables);
					}
					else
					{
						variables.Add(new Variable("<error>", null, null));
					}
				}

				SendResponse(response, new VariablesResponseBody(variables));
			}
		}

		void SendScopeVariablesResponse(WatchType watchType, Response response)
		{
			var variables = new List<Variable>();

			foreach (var w in Debugger.GetWatches(watchType))
			{
				DynValue value = w.Value ?? DynValue.Void;
				var index = value.Type == DataType.Table ? m_Variables.Count + 1 : 0;

				if (index > 0)
				{
					m_Variables.Add(value);
				}

				variables.Add(new Variable(w.Name, value.ToDebugPrintString(), value.Type.ToLuaDebuggerString(), index));
			}

			SendResponse(response, new VariablesResponseBody(variables));
		}

		void IAsyncDebuggerClient.SendStopEvent()
		{
			switch (m_StopReason)
			{
				case STOP_REASON_STEP:
					SendEvent(CreateStoppedEvent("step", "Paused after stepping"));
					break;

				case STOP_REASON_BREAKPOINT:
					SendEvent(CreateStoppedEvent("breakpoint", "Paused on breakpoint"));
					break;

				case STOP_REASON_EXCEPTION:
					SendEvent(CreateStoppedEvent("exception", "Paused on exception", m_RuntimeException?.Message));
					break;

				case STOP_REASON_PAUSED:
					SendEvent(CreateStoppedEvent("pause", "Paused by debugger"));
					break;

				default:
					SendEvent(CreateStoppedEvent("unknown", "Paused for an unknown reason"));
					break;

			}
		}

		void IAsyncDebuggerClient.OnWatchesUpdated(WatchType watchType, int stackFrameIndex)
		{
			lock (m_Lock)
			{
				if (stackFrameIndex >= 0 && m_CurrentStackFrame != stackFrameIndex)
				{
					m_CurrentStackFrame = stackFrameIndex;
				}

				if (watchType == WatchType.CallStack)
				{
					var stackSize = m_CurrentCallStack.Count;
					var updatedCallStack = Debugger.GetWatches(WatchType.CallStack);
					var callStackChanged = false;

					if (m_CurrentCallStack.Count != updatedCallStack.Count)
					{
						callStackChanged = true;
					}
					else
					{
						for (var i = 0; i < stackSize; i++)
						{
							if (m_CurrentCallStack[i].Location != updatedCallStack[i].Location)
							{
								callStackChanged = true;
								break;
							}
						}
					}

					if (callStackChanged)
					{
						m_CurrentCallStack = updatedCallStack;
						m_Variables.Clear();
					}
				}

				if (watchType == WatchType.Locals || watchType == WatchType.Closure)
				{
					var scopeState = watchType == WatchType.Locals ? m_Local : m_Closure;
					scopeState.CurrentStackFrame = m_CurrentStackFrame;

					if (m_CurrentStackFrame == m_PendingStackFrame && scopeState.PendingResponse != null)
					{
						SendScopeVariablesResponse(watchType, scopeState.PendingResponse);
						scopeState.PendingResponse = null;
					}
				}
			}
		}

		void IAsyncDebuggerClient.OnSourceCodeChanged(int sourceID)
		{
			if (Debugger.IsSourceOverride(sourceID))
				SendText("Loaded source '{0}' -> '{1}'", Debugger.GetSource(sourceID).Name, Debugger.GetSourceFile(sourceID));
			else
				SendText("Loaded source '{0}'", Debugger.GetSource(sourceID).Name);
		}


		public void OnExecutionEnded()
		{
			if (m_NotifyExecutionEnd)
				SendText("Execution ended.");
		}

		private void SendText(string msg, params object[] args)
		{
			msg = string.Format(msg, args);
			// SendEvent(new OutputEvent("console", DateTime.Now.ToString("u") + ": " + msg + "\n"));
			SendEvent(new OutputEvent("console", msg + "\n"));
		}

		public void OnException(ScriptRuntimeException ex)
		{
			m_StopReason = STOP_REASON_EXCEPTION;
			m_RuntimeException = ex;
		}

		public void Unbind()
		{
			Debugger.Client = null;
			SendTerminateEvent(m_RestartOnUnbind);
			Stop();
		}

		public override void Terminate(bool restart = false)
		{
			m_RestartOnUnbind = restart;
			Unbind();
			m_RestartOnUnbind = false;
		}
	}

	public class VariablesScopeState
	{
		public string Name { get; }

		public int CurrentStackFrame { get; set; } = -1;
		public Response PendingResponse { get; set; }

		public VariablesScopeState(string name)
		{
			Name = name;
		}
	}
}

#endif
