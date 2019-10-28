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
		public override string Name => "(Detached)";

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

			SendText("There are presently no scripts attached to the debugger.\n");

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

		public override void Attach(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void Continue(Response response, Table arguments)
		{
			SendErrorResponse(response, 1, "Debug session is not attached to a script");
		}

		public override void Disconnect(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void Evaluate(Response response, Table args)
		{
			SendErrorResponse(response, 1, "Debug session is not attached to a script");
		}

		public override void ExceptionInfo(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void Launch(Response response, Table arguments)
		{
			SendResponse(response);
		}

		public override void Next(Response response, Table arguments)
		{
			SendErrorResponse(response, 1, "Debug session is not attached to a script");
		}

		public override void Pause(Response response, Table arguments)
		{
			SendErrorResponse(response, 1, "Debug session is not attached to a script");
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
			SendErrorResponse(response, 1, "Debug session is not attached to a script");
		}

		public override void StackTrace(Response response, Table args)
		{
			SendErrorResponse(response, 1, "Debug session is not attached to a script");
		}

		public override void StepIn(Response response, Table arguments)
		{
			SendErrorResponse(response, 1, "Debug session is not attached to a script");
		}

		public override void StepOut(Response response, Table arguments)
		{
			SendErrorResponse(response, 1, "Debug session is not attached to a script");
		}

		public override void Threads(Response response, Table arguments)
		{
			SendResponse(response);
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
