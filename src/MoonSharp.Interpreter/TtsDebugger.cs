using MoonSharp.VsCodeDebugger;

namespace MoonSharp.Interpreter
{
    public static class TtsDebugger
    {
        private static MoonSharpVsCodeDebugServer server;

        public static MoonSharpVsCodeDebugServer getServer()
        {
            if (server == null)
            {
                server = new MoonSharpVsCodeDebugServer();
                server.Start();
            }

            return server;
        }
    }
}