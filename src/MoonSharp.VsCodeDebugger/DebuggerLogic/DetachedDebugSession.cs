#if (!UNITY_5) || UNITY_STANDALONE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MoonSharp.Interpreter;
using MoonSharp.VsCodeDebugger.SDK;

namespace MoonSharp.VsCodeDebugger.DebuggerLogic
{
	internal class DetachedDebugSession : MoonSharpDebugSession
	{
		public override string Name => "Detached";

		internal DetachedDebugSession(int port, MoonSharpVsCodeDebugServer server) : base(port, server, null)
		{
		}

		public override void Initialize(Response response, Table args)
		{
			SendText("Connected to MoonSharp {0} [{1}] on process {2} (PID {3})",
					 Script.VERSION,
					 Script.GlobalOptions.Platform.GetPlatformName(),
					 System.Diagnostics.Process.GetCurrentProcess().ProcessName,
					 System.Diagnostics.Process.GetCurrentProcess().Id);

			SendText("No script is set as default for debugging; use the debug console to select the script to debug.\n");

			SendList();

			SendResponse(response, new Capabilities()
			{
				// This debug adapter does not need the configurationDoneRequest.
				supportsConfigurationDoneRequest = false,

				// This debug adapter does not support function breakpoints.
				supportsFunctionBreakpoints = false,

				// This debug adapter doesn't support conditional breakpoints.
				supportsConditionalBreakpoints = false,

				// This debug adapter does not support a side effect free evaluate request for data hovers.
				supportsEvaluateForHovers = true,

				// This debug adapter does not support exception info.
				supportsExceptionInfoRequest = false,

				// This debug adapter does not support exception breakpoint filters
				exceptionBreakpointFilters = new object[0]
			});

			// Debugger is ready to accept breakpoints immediately
			SendEvent(new InitializedEvent());
		}

		private void SendList()
		{
			int currId = Server.CurrentId ?? -1000;

			SendText("==========================================================");

			foreach (var pair in Server.GetAttachedDebuggersByIdAndName())
			{
				string isdef = (pair.Key == currId) ? " (default)" : "";
				SendText("{0} : {1}{2}", pair.Key.ToString().PadLeft(9), pair.Value, isdef);
			}
			SendText("");
			SendText("Type the number of the script to debug, or '!' to refresh");
		}

		public override void Attach(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void Continue(Response response, Table arguments)
		{
			SendList();
			SendResponse(response);
		}

		public override void Disconnect(Response response, Table arguments)
		{
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
			var context = getString(args, "context") ?? "hover";

			if (context == "repl")
				ExecuteRepl(expression);

			SendResponse(response);
		}

		public override void ExceptionInfo(Response response, Table arguments)
		{
			SendResponse(response);
		}

		private void ExecuteRepl(string cmd)
		{
			int id = 0;
			if (int.TryParse(cmd, out id))
			{
				Server.CurrentId = id;

				SendText("Re-attach the debugger to debug the selected script.");

				Terminate();
			}
			else
			{
				SendList();
			}
		}

		public override void Launch(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void Next(Response response, Table arguments)
		{
			SendList();
			SendResponse(response);
		}

		public override void Pause(Response response, Table arguments)
		{
			SendList();
			SendResponse(response);
		}

		public override void Source(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void Scopes(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void SetBreakpoints(Response response, Table args)
		{
			SendResponse(response);
		}

		public override void StackTrace(Response response, Table args)
		{
			SendResponse(response);
		}

		public override void StepIn(Response response, Table arguments)
		{
			SendList();
			SendResponse(response);
		}

		public override void StepOut(Response response, Table arguments)
		{
			SendList();
			SendResponse(response);
		}

		public override void Threads(Response response, Table arguments)
		{
			var threads = new List<Thread>() { new Thread(0, "Main Thread") };
			SendResponse(response, new ThreadsResponseBody(threads));
		}

		public override void Variables(Response response, Table arguments)
		{
			SendResponse(response);
		}

		private void SendText(string msg, params object[] args)
		{
			msg = string.Format(msg, args);
			SendEvent(new OutputEvent("console", msg + "\n"));
		}

		public override void Terminate(bool restart = false)
		{
			SendTerminateEvent(restart);
			Stop();
		}
	}
}
#endif
