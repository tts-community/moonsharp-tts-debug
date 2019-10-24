#if (!PCL) && ((!UNITY_5) || UNITY_STANDALONE)

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.VsCodeDebugger.SDK;


namespace MoonSharp.VsCodeDebugger.DebuggerLogic
{
	internal class ScriptDebugSession : MoonSharpDebugSession, IAsyncDebuggerClient
	{
		const int SCOPE_LOCALS = 1;
		const int SCOPE_SELF = 2;

		const int VARIABLE_REFERENCE_FRAME_ID_OFFSET = 16;
		const int VARIABLE_REFERENCE_SCOPE_OFFSET = VARIABLE_REFERENCE_FRAME_ID_OFFSET + 8;

		const int VARIABLE_REFERENCE_FRAME_ID_MASK = 0xFF << VARIABLE_REFERENCE_FRAME_ID_OFFSET;
		const int VARIABLE_REFERENCE_SCOPE_MASK = 0xFF << VARIABLE_REFERENCE_SCOPE_OFFSET;

		const int STOP_REASON_STEP = 0;
		const int STOP_REASON_BREAKPOINT = 1;
		const int STOP_REASON_EXCEPTION = 2;
		const int STOP_REASON_PAUSED = 3;

		readonly List<DynValue> m_Variables = new List<DynValue>();
		bool m_NotifyExecutionEnd = false;
		bool m_RestartOnUnbind = false;

		int stopReason = STOP_REASON_STEP;
		ScriptRuntimeException runtimeException = null;

		private int currentLocalsStackFrame = -1;
		private int pendingLocalsStackFrame = -1;
		private Response pendingLocalsResponse;

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
				supportsEvaluateForHovers = false,

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
			stopReason = STOP_REASON_BREAKPOINT;
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

			if (frameId != 0 && context != "repl")
				SendText("Warning : Evaluation of variables/watches is always done with the top-level scope.");

			if (context == "repl" && expression.StartsWith("!"))
			{
				ExecuteRepl(expression.Substring(1));
				SendResponse(response);
				return;
			}

			DynValue v = Debugger.Evaluate(expression) ?? DynValue.Nil;
			m_Variables.Add(v);

			SendResponse(response, new EvaluateResponseBody(v.ToDebugPrintString(), m_Variables.Count - 1)
			{
				type = v.Type.ToLuaDebuggerString()
			});
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
			if (runtimeException == null)
			{
				return false;
			}

			IList<WatchItem> exceptionCallStack = runtimeException.CallStack;
			IList<WatchItem> debuggerCallStack = Debugger.GetWatches(WatchType.CallStack);

			return exceptionCallStack.Count == debuggerCallStack.Count && exceptionCallStack.SequenceEqual(debuggerCallStack, new CallStackFrameComparator());
		}

		public override void ExceptionInfo(Response response, Table arguments)
		{
			if (IsRuntimeExceptionCurrent())
			{
				SendResponse(response, new ExceptionInfoResponseBody("runtime", "Runtime exception", "userUnhandled", ExceptionDetails(runtimeException)));
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
			stopReason = STOP_REASON_STEP;
			Debugger.QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.StepOver });
			SendResponse(response);
		}

		private StoppedEvent CreateStoppedEvent(string reason, string description, string text = null)
		{
			return new StoppedEvent(0, reason, description, text);
		}

		public override void Pause(Response response, Table arguments)
		{
			stopReason = STOP_REASON_PAUSED;
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

			int frameId = getInt(arguments, "frameId", 0);

			if (frameId >= 0 && frameId <= Debugger.GetWatches(WatchType.CallStack).Count)
			{
				scopes.Add(new Scope("Locals", (SCOPE_LOCALS << VARIABLE_REFERENCE_SCOPE_OFFSET) | (frameId << VARIABLE_REFERENCE_FRAME_ID_OFFSET)));
				scopes.Add(new Scope("Self", (SCOPE_SELF << VARIABLE_REFERENCE_SCOPE_OFFSET) | (frameId << VARIABLE_REFERENCE_FRAME_ID_OFFSET)));
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
				SendErrorResponse(response, 3010, "setBreakpoints: property 'source' is empty or misformed", null, false, true);
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
			stopReason = STOP_REASON_STEP;
			Debugger.QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.StepIn });
			SendResponse(response);
		}

		public override void StepOut(Response response, Table arguments)
		{
			stopReason = STOP_REASON_STEP;
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
			int variablesReference = getInt(arguments, "variablesReference", -1);

			int scope = (variablesReference & VARIABLE_REFERENCE_SCOPE_MASK) >> VARIABLE_REFERENCE_SCOPE_OFFSET;
			int frameId = (variablesReference & VARIABLE_REFERENCE_FRAME_ID_MASK) >> VARIABLE_REFERENCE_FRAME_ID_OFFSET;

			int index = variablesReference & ~(VARIABLE_REFERENCE_FRAME_ID_MASK | VARIABLE_REFERENCE_SCOPE_MASK);

			if (scope == SCOPE_LOCALS)
			{
				if (frameId == currentLocalsStackFrame)
				{
					SendLocalVariablesResponse(response);
				}
				else
				{
					if (pendingLocalsResponse != null)
					{
						SendErrorResponse(pendingLocalsResponse, 1200, "pending Variables request cancelled by newer request");
					}

					pendingLocalsStackFrame = frameId;
					pendingLocalsResponse = response;

					Debugger.QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.ViewFrame, StackFrame = pendingLocalsStackFrame });
				}

				return;
			}

			var variables = new List<Variable>();

			if (scope == SCOPE_SELF)
			{
				DynValue v = Debugger.Evaluate("self");
				VariableInspector.InspectVariable(v, variables);
			}
			else if (scope == 0 || index >= m_Variables.Count)
			{
				variables.Add(new Variable("<error>", null, null));
			}
			else
			{
				VariableInspector.InspectVariable(m_Variables[index], variables);
			}

			SendResponse(response, new VariablesResponseBody(variables));
		}

		void SendLocalVariablesResponse(Response response)
		{
			var variables = new List<Variable>();

			foreach (var w in Debugger.GetWatches(WatchType.Locals))
			{
				DynValue value = w.Value ?? DynValue.Void;
				variables.Add(new Variable(w.Name, value.ToDebugPrintString(), value.Type.ToLuaDebuggerString()));
			}

			SendResponse(response, new VariablesResponseBody(variables));
		}

		void IAsyncDebuggerClient.SendStopEvent()
		{
			switch (stopReason)
			{
				case STOP_REASON_STEP:
					SendEvent(CreateStoppedEvent("step", "Paused after stepping"));
					break;

				case STOP_REASON_BREAKPOINT:
					SendEvent(CreateStoppedEvent("breakpoint", "Paused on breakpoint"));
					break;

				case STOP_REASON_EXCEPTION:
					SendEvent(CreateStoppedEvent("exception", "Paused on exception", runtimeException?.Message));
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
			if (watchType == WatchType.CallStack)
			{
				m_Variables.Clear();
			}
			else if (watchType == WatchType.Locals)
			{
				currentLocalsStackFrame = stackFrameIndex;

				if (currentLocalsStackFrame == pendingLocalsStackFrame)
				{
					SendLocalVariablesResponse(pendingLocalsResponse);

					pendingLocalsStackFrame = -1;
					pendingLocalsResponse = null;
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
			stopReason = STOP_REASON_EXCEPTION;
			runtimeException = ex;
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
}
#endif
